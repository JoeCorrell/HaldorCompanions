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
            // ── Separate-key radial: press configured key while hovering companion ──
            if (!IsRadialKeyUse)
            {
                HandleSeparateKeyRadial();
                // Still process any lingering pending taps from the prefix
                ProcessPendingTap();
                return;
            }

            // ── Same-key mode (E): tap/hold detection ──
            ProcessPendingTap();
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
        /// Process any pending tap from the Container.Interact prefix.
        /// Detects hold via Input.GetKey on the configured key.
        /// </summary>
        private void ProcessPendingTap()
        {
            if (_pendingTapContainer == null) return;

            // Use raw Unity input for the configured key + ZInput for gamepad
            bool useHeld = Input.GetKey(CompanionsPlugin.RadialMenuKey.Value)
                        || ZInput.GetButton("JoyUse");
            float elapsed = Time.time - _pendingTapTime;

            // Log once when we start processing a pending tap
            if (!_loggedPendingStart)
            {
                _loggedPendingStart = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — processing pending tap: " +
                    $"keyHeld={useHeld} key={CompanionsPlugin.RadialMenuKey.Value} " +
                    $"elapsed={elapsed:F3}s");
            }

            if (!useHeld)
            {
                // Key released → genuine tap → open inventory
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — key RELEASED after {elapsed:F3}s, opening inventory");

                var container = _pendingTapContainer;
                var player    = _pendingTapPlayer;
                _pendingTapContainer = null;
                _pendingTapPlayer    = null;
                _loggedPendingStart  = false;

                if (container != null && player != null)
                {
                    _bypassPrefix = true;
                    container.Interact(player, false, false);
                }
                return;
            }

            // Key still held — once past the threshold, it's a hold → open radial
            if (elapsed >= HoldThreshold)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — hold threshold reached ({elapsed:F3}s), opening radial");

                var setup = _pendingTapContainer.GetComponent<CompanionSetup>();
                _pendingTapContainer = null;
                _pendingTapPlayer    = null;
                _loggedPendingStart  = false;

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
            _pendingTapContainer = null;
            _pendingTapPlayer    = null;
            _loggedPendingStart  = false;
        }

        internal string GetHoverText()
        {
            string name = GetCompanionName();
            string inv = ModLocalization.Loc("hc_hover_inventory");
            string cmd = ModLocalization.Loc("hc_hover_commands");
            if (IsRadialKeyUse)
            {
                return Localization.instance.Localize(
                    $"{name}\n" +
                    $"[<color=yellow><b>$KEY_Use</b></color>] {inv}\n" +
                    $"[<color=yellow>Hold <b>$KEY_Use</b></color>] {cmd}");
            }

            string keyName = CompanionsPlugin.RadialMenuKey.Value.ToString();
            return Localization.instance.Localize(
                $"{name}\n" +
                $"[<color=yellow><b>$KEY_Use</b></color>] {inv}\n" +
                $"[<color=yellow><b>{keyName}</b></color>] {cmd}");
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

                // ── Same-key mode (E): tap/hold detection ──
                if (hold)
                {
                    // Hold call arrived (after Valheim's 0.2s debounce)
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Prefix — hold=true arrived, opening radial");

                    _pendingTapContainer = null;
                    _pendingTapPlayer    = null;
                    _loggedPendingStart  = false;

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
                    _pendingTapContainer = __instance;
                    _pendingTapPlayer    = player;
                    _pendingTapTime      = Time.time;
                    _loggedPendingStart  = false;

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
