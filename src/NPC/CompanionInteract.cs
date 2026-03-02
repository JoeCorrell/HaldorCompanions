using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Attached to every companion prefab. Provides custom hover text via
    /// Harmony patches on Character. The Container component on the same GO
    /// handles the actual E/Use interaction (Interactable) natively —
    /// Character does not implement Interactable, so Container is found first
    /// by Player.Interact → GetComponentInParent&lt;Interactable&gt;().
    ///
    /// When RadialMenuKey == E (default):
    ///   Tap E  → opens inventory panel (via Container → InventoryGui)
    ///   Hold E → opens radial command wheel
    ///
    /// When RadialMenuKey != E:
    ///   E      → opens inventory directly (no tap/hold)
    ///   Key    → opens radial when hovering a companion
    /// </summary>
    public class CompanionInteract : MonoBehaviour
    {
        private ZNetView  _nview;
        private Character _character;

        // ── Pending-tap state (static — only one interact at a time) ──
        private static Container _pendingTapContainer;
        private static Player    _pendingTapPlayer;
        private static float     _pendingTapTime;
        private static bool      _bypassPrefix;

        // ── Gamepad hold detection (prefix-based, legacy) ──
        private static bool _pendingIsGamepad;
        private static bool _gamepadReleaseDetected;

        // ── Independent gamepad hold detection ──
        // Bypasses the Container.Interact prefix chain entirely.
        // Monitors ZInput.GetButton("JoyUse") directly while hovering a companion.
        private static bool           _gpHoldActive;
        private static float          _gpHoldStart;
        private static CompanionSetup _gpHoldTarget;

        private const float HoldThreshold = 0.2f;

        // ── Player hover reflection (for separate-key radial detection) ──
        private static readonly FieldInfo _playerHoveringField =
            AccessTools.Field(typeof(Player), "m_hovering");

        // ── Diagnostic logging ──
        private static bool _loggedPendingStart;

        /// <summary>True when RadialMenuKey is the same as the vanilla Use key (E).</summary>
        private static bool IsRadialKeyUse =>
            CompanionsPlugin.RadialMenuKey.Value == KeyCode.E;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
        }

        private void Update()
        {
            // ── Independent gamepad hold detection (runs for all modes) ──
            // Only the instance whose companion is being hovered will act.
            if (ZInput.IsGamepadActive())
                HandleGamepadHold();

            // ── Separate-key radial: press configured key while hovering companion ──
            if (!IsRadialKeyUse)
            {
                HandleSeparateKeyRadial();
                // Still process any lingering pending taps from the prefix
                if (!_gpHoldActive) ProcessPendingTap();
                return;
            }

            // ── Same-key mode (E): tap/hold detection ──
            // Suppress prefix-based ProcessPendingTap while independent gamepad hold is active
            if (!_gpHoldActive) ProcessPendingTap();
        }

        /// <summary>
        /// When RadialMenuKey is NOT E, detect the configured key press independently.
        /// Opens the radial when the player presses the key while hovering a companion.
        /// </summary>
        private void HandleSeparateKeyRadial()
        {
            if (!Input.GetKeyDown(CompanionsPlugin.RadialMenuKey.Value)) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Check what the player is hovering over
            var hovering = _playerHoveringField?.GetValue(player) as GameObject;
            if (hovering == null) return;

            // The hover target could be a child — walk up to find CompanionSetup
            var setup = hovering.GetComponentInParent<CompanionSetup>();
            if (setup == null) return;

            // Ownership check
            var nview = setup.GetComponent<ZNetView>();
            if (nview?.GetZDO() == null) return;

            string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
            if (owner.Length > 0 && owner != player.GetPlayerID().ToString())
            {
                player.Message(MessageHud.MessageType.Center, ModLocalization.Loc("hc_msg_not_yours"));
                return;
            }

            CompanionsPlugin.Log.LogInfo(
                $"[Interact] Separate key ({CompanionsPlugin.RadialMenuKey.Value}) — opening radial");
            CompanionRadialMenu.EnsureInstance();
            if (CompanionRadialMenu.Instance != null && !CompanionRadialMenu.Instance.IsVisible)
                CompanionRadialMenu.Instance.Show(setup);
        }

        /// <summary>
        /// Independent gamepad hold detection. Monitors ZInput.GetButton("JoyUse")
        /// directly while the player hovers THIS companion instance.
        /// Bypasses the Container.Interact prefix chain entirely — avoids timing
        /// issues with vanilla's 0.2s debounce and m_hovering validity.
        /// </summary>
        private void HandleGamepadHold()
        {
            var player = Player.m_localPlayer;
            if (player == null) { ResetGamepadHold(); return; }

            // Only the instance whose companion is being hovered should act
            var hovering = _playerHoveringField?.GetValue(player) as GameObject;
            var setup = hovering != null ? hovering.GetComponentInParent<CompanionSetup>() : null;
            var mySetup = GetComponent<CompanionSetup>();

            // Not hovering this companion — reset if we were tracking
            if (setup == null || setup != mySetup)
            {
                if (_gpHoldActive && _gpHoldTarget == mySetup)
                    ResetGamepadHold();
                return;
            }

            // Radial already visible — nothing to do
            if (CompanionRadialMenu.Instance != null && CompanionRadialMenu.Instance.IsVisible)
            {
                ResetGamepadHold();
                return;
            }

            if (ZInput.GetButton("JoyUse"))
            {
                if (!_gpHoldActive)
                {
                    // First frame of hold
                    _gpHoldActive = true;
                    _gpHoldStart = Time.time;
                    _gpHoldTarget = setup;
                    CompanionsPlugin.Log.LogDebug(
                        $"[Interact] Gamepad independent hold START on \"{setup.name}\"");
                }
                else if (_gpHoldTarget == setup && Time.time - _gpHoldStart >= HoldThreshold)
                {
                    // Hold threshold reached — open radial
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad independent hold → RADIAL " +
                        $"(held {Time.time - _gpHoldStart:F3}s)");

                    // Clear any prefix-based pending state to avoid double-handling
                    ClearPendingTap();
                    ResetGamepadHold();

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                }
            }
            else if (_gpHoldActive && _gpHoldTarget == setup)
            {
                float held = Time.time - _gpHoldStart;
                ClearPendingTap();
                ResetGamepadHold();

                if (held >= HoldThreshold)
                {
                    // Released at or after threshold — treat as hold → open radial
                    // (handles edge case where GetButton returns false on the
                    //  same frame the threshold is crossed)
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad hold → RADIAL on release " +
                        $"(held {held:F3}s, threshold={HoldThreshold}s)");

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                }
                else
                {
                    // Released before threshold — genuine tap → open inventory
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad hold → TAP " +
                        $"(released after {held:F3}s), opening inventory");

                    var container = setup.GetComponent<Container>();
                    if (container != null)
                    {
                        _bypassPrefix = true;
                        container.Interact(player, false, false);
                    }
                }
            }
        }

        private static void ResetGamepadHold()
        {
            _gpHoldActive = false;
            _gpHoldTarget = null;
        }

        /// <summary>
        /// Process any pending tap from the Container.Interact prefix.
        /// Keyboard: detects hold via Input.GetKey on the configured key.
        /// Gamepad: tracks ZInput.GetButtonUp("JoyUse") as a positive release
        /// signal — assumes held until ButtonUp fires, because GetButton is
        /// unreliable (returns false on frames where the button IS still held
        /// due to ZInput internal state timing vs Player.Interact debounce).
        /// </summary>
        private void ProcessPendingTap()
        {
            if (_pendingTapContainer == null) return;

            float elapsed = Time.time - _pendingTapTime;

            // Gamepad: detect release via discrete ButtonUp event
            if (_pendingIsGamepad && !_gamepadReleaseDetected &&
                ZInput.GetButtonUp("JoyUse"))
            {
                _gamepadReleaseDetected = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Gamepad ButtonUp detected at {elapsed:F3}s");
            }

            // Determine hold state based on input source
            bool useHeld;
            if (_pendingIsGamepad)
                useHeld = !_gamepadReleaseDetected;
            else
                useHeld = Input.GetKey(CompanionsPlugin.RadialMenuKey.Value);

            // Log once when we start processing a pending tap
            if (!_loggedPendingStart)
            {
                _loggedPendingStart = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — processing pending tap: " +
                    $"useHeld={useHeld} gamepad={_pendingIsGamepad} " +
                    $"key={CompanionsPlugin.RadialMenuKey.Value} " +
                    $"elapsed={elapsed:F3}s");
            }

            if (!useHeld)
            {
                // Button released → genuine tap → open inventory
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — RELEASED after {elapsed:F3}s " +
                    $"(gamepad={_pendingIsGamepad}), opening inventory");

                var container = _pendingTapContainer;
                var player    = _pendingTapPlayer;
                _pendingTapContainer    = null;
                _pendingTapPlayer       = null;
                _loggedPendingStart     = false;
                _gamepadReleaseDetected = false;

                if (container != null && player != null)
                {
                    _bypassPrefix = true;
                    container.Interact(player, false, false);
                }
                return;
            }

            // Key/button still held — once past the threshold, it's a hold → open radial
            if (elapsed >= HoldThreshold)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — hold threshold reached ({elapsed:F3}s), " +
                    $"gamepad={_pendingIsGamepad}, opening radial");

                var setup = _pendingTapContainer.GetComponent<CompanionSetup>();
                _pendingTapContainer    = null;
                _pendingTapPlayer       = null;
                _loggedPendingStart     = false;
                _gamepadReleaseDetected = false;

                if (setup != null)
                {
                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                    {
                        CompanionRadialMenu.Instance.Show(setup);
                        CompanionsPlugin.Log.LogInfo("[Interact] Radial menu Show() called");
                    }
                }
            }
        }

        /// <summary>Clear pending tap (e.g. when radial closes or companion dies).</summary>
        internal static void ClearPendingTap()
        {
            if (_pendingTapContainer != null)
                CompanionsPlugin.Log.LogDebug("[Interact] ClearPendingTap called while pending");
            _pendingTapContainer    = null;
            _pendingTapPlayer       = null;
            _loggedPendingStart     = false;
            _gamepadReleaseDetected = false;
        }

        internal string GetHoverText()
        {
            string name = GetCompanionName();
            string inv = ModLocalization.Loc("hc_hover_inventory");
            string cmd = ModLocalization.Loc("hc_hover_commands");

            // Resolve the interact button name explicitly — $KEY_Use localization
            // can fail to show the gamepad glyph in some timing scenarios.
            string useBtn;
            if (ZInput.IsGamepadActive())
                useBtn = ZInput.instance.GetBoundKeyString("JoyUse");
            else
                useBtn = ZInput.instance.GetBoundKeyString("Use");

            if (IsRadialKeyUse)
            {
                return $"{name}\n" +
                    $"[<color=yellow><b>{useBtn}</b></color>] {inv}\n" +
                    $"[<color=yellow>Hold <b>{useBtn}</b></color>] {cmd}";
            }

            string radialBtn;
            if (ZInput.IsGamepadActive())
                radialBtn = useBtn; // On gamepad, radial uses same button (hold)
            else
                radialBtn = CompanionsPlugin.RadialMenuKey.Value.ToString();

            return $"{name}\n" +
                $"[<color=yellow><b>{useBtn}</b></color>] {inv}\n" +
                $"[<color=yellow><b>{radialBtn}</b></color>] {cmd}";
        }

        internal string GetHoverName()
        {
            return GetCompanionName();
        }

        private string GetCompanionName()
        {
            if (_nview != null && _nview.GetZDO() != null)
            {
                string custom = _nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (!string.IsNullOrEmpty(custom))
                    return custom;
            }
            return _character != null ? _character.m_name : ModLocalization.Loc("hc_msg_name_default");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Harmony patches — ownership check + defer tap on Container.Interact
        // ═══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static class ContainerInteract_Patch
        {
            static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
            {
                // Bypass flag — used when we manually invoke after confirming a tap
                if (_bypassPrefix)
                {
                    CompanionsPlugin.Log.LogDebug("[Interact] Prefix — bypass flag set, allowing vanilla");
                    _bypassPrefix = false;
                    return true;
                }

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) return true;

                var player = character as Player;
                if (player == null)
                {
                    __result = false;
                    return false;
                }

                // Ownership check
                string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
                string playerId = player.GetPlayerID().ToString();
                if (owner.Length > 0 && owner != playerId)
                {
                    player.Message(MessageHud.MessageType.Center, ModLocalization.Loc("hc_msg_not_yours"));
                    __result = false;
                    return false;
                }

                // ── Gamepad: independent hold detection handles everything ──
                // Must be checked BEFORE separate-key mode — gamepad always uses
                // HandleGamepadHold() for tap/hold split, regardless of radial key config.
                if (ZInput.IsGamepadActive())
                {
                    __result = false;
                    return false;
                }

                // ── Separate-key mode: no tap/hold deferral, let vanilla open inventory ──
                if (!IsRadialKeyUse)
                {
                    if (hold)
                    {
                        // Suppress vanilla hold repeats (they'd re-open the container)
                        __result = false;
                        return false;
                    }
                    // hold=false (tap) → let vanilla Container.Interact run → opens inventory
                    CompanionsPlugin.Log.LogDebug(
                        "[Interact] Prefix — separate key mode, letting vanilla open inventory");
                    return true;
                }

                // ── Same-key mode (E) — keyboard only below this point ──
                if (hold)
                {
                    // Hold call arrived (after Valheim's 0.2s debounce)
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Prefix — hold=true arrived, opening radial");

                    _pendingTapContainer    = null;
                    _pendingTapPlayer       = null;
                    _loggedPendingStart     = false;
                    _gamepadReleaseDetected = false;

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                    __result = true;
                    return false;
                }

                // Tap E → defer. Update() will check raw input to distinguish tap vs hold.
                // Guard: only set if not already pending (prevents timer reset).
                if (_pendingTapContainer == null)
                {
                    _pendingTapContainer    = __instance;
                    _pendingTapPlayer       = player;
                    _pendingTapTime         = Time.time;
                    _loggedPendingStart     = false;
                    _pendingIsGamepad       = false; // keyboard only at this point
                    _gamepadReleaseDetected = false;

                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Prefix — tap deferred, " +
                        $"InputKey={Input.GetKey(CompanionsPlugin.RadialMenuKey.Value)} " +
                        $"time={Time.time:F3}");
                }
                else
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Interact] Prefix — already pending, keeping existing timer " +
                        $"(elapsed={Time.time - _pendingTapTime:F3}s)");
                }

                __result = false;
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Harmony patches — intercept Character hover text for companions
        // ═══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
        private static class CharacterGetHoverText_Patch
        {
            static bool Prefix(Character __instance, ref string __result)
            {
                var ci = __instance.GetComponent<CompanionInteract>();
                if (ci == null) return true;
                __result = ci.GetHoverText();
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverName))]
        private static class CharacterGetHoverName_Patch
        {
            static bool Prefix(Character __instance, ref string __result)
            {
                var ci = __instance.GetComponent<CompanionInteract>();
                if (ci == null) return true;
                __result = ci.GetHoverName();
                return false;
            }
        }
    }
}
