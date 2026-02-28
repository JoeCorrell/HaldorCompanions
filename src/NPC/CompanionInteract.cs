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
    /// Tap E  → opens inventory panel (via Container → InventoryGui)
    /// Hold E → opens radial command wheel
    ///
    /// Valheim's Player.Interact debounces hold calls for 0.2s after any
    /// tap (m_lastHoverInteractTime), so Container.Interact(hold=true) never
    /// arrives in time. Instead we check ZInput.GetButton directly in Update
    /// to distinguish tap from hold:
    ///   - Key still held after 0.2s → open radial
    ///   - Key released before 0.2s → fire the deferred tap (open inventory)
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

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
        }

        private void Update()
        {
            if (_pendingTapContainer == null) return;

            bool useHeld = ZInput.GetButton("Use") || ZInput.GetButton("JoyUse");

            if (!useHeld)
            {
                // Key released → genuine tap → open inventory
                var container = _pendingTapContainer;
                var player    = _pendingTapPlayer;
                _pendingTapContainer = null;
                _pendingTapPlayer    = null;

                if (container != null && player != null)
                {
                    _bypassPrefix = true;
                    container.Interact(player, false, false);
                }
                return;
            }

            // Key still held — once past the threshold, it's a hold → open radial
            if (Time.time - _pendingTapTime >= HoldThreshold)
            {
                var setup = _pendingTapContainer.GetComponent<CompanionSetup>();
                _pendingTapContainer = null;
                _pendingTapPlayer    = null;

                if (setup != null)
                {
                    CompanionRadialMenu.EnsureInstance();
                    if (!CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                }
            }
        }

        /// <summary>Clear pending tap (e.g. when radial closes or companion dies).</summary>
        internal static void ClearPendingTap()
        {
            _pendingTapContainer = null;
            _pendingTapPlayer    = null;
        }

        internal string GetHoverText()
        {
            string name = GetCompanionName();
            return Localization.instance.Localize(
                $"{name}\n" +
                $"[<color=yellow><b>$KEY_Use</b></color>] Inventory\n" +
                $"[<color=yellow>Hold <b>$KEY_Use</b></color>] Commands");
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
            return _character != null ? _character.m_name : "Companion";
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
                    _bypassPrefix = false;
                    return true;
                }

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) return true;

                var player = character as Player;
                if (player == null) { __result = false; return false; }

                // Ownership check
                string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
                if (owner.Length > 0 && owner != player.GetPlayerID().ToString())
                {
                    player.Message(MessageHud.MessageType.Center, "This is not your companion.");
                    __result = false;
                    return false;
                }

                if (hold)
                {
                    // Hold call arrived (after Valheim's 0.2s debounce).
                    // If we haven't opened the radial yet, cancel any pending tap
                    // and open it now. Usually Update() handles this first.
                    _pendingTapContainer = null;
                    _pendingTapPlayer    = null;

                    CompanionRadialMenu.EnsureInstance();
                    if (!CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                    __result = true;
                    return false;
                }

                // Tap E → defer. Update() will check the raw input state to
                // decide whether this is a genuine tap or the start of a hold.
                _pendingTapContainer = __instance;
                _pendingTapPlayer    = player;
                _pendingTapTime      = Time.time;
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
