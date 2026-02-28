using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Synchronises CompanionInteractPanel visibility with InventoryGui.
    /// When a companion's Container opens, we show the panel and hide
    /// the vanilla container grid.  When InventoryGui closes, we hide the panel.
    /// </summary>
    public static class InventoryGuiPatches
    {
        /// <summary>
        /// True while the currently-open container belongs to a companion.
        /// Used by UpdateContainer_Patch to skip vanilla container UI entirely.
        /// </summary>
        private static bool _companionContainerOpen;

        private static readonly FieldInfo _currentContainerField =
            AccessTools.Field(typeof(InventoryGui), "m_currentContainer");

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        private static class Show_Patch
        {
            static void Postfix(Container container)
            {
                _companionContainerOpen = false;
                if (container == null)
                {
                    CompanionsPlugin.Log.LogDebug("[InvGUI] Show — container=null, skipping");
                    return;
                }
                var ci = container.GetComponent<CompanionInteract>();
                if (ci == null)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[InvGUI] Show — container \"{container.name}\" has no CompanionInteract, skipping");
                    return;
                }

                var setup = container.GetComponent<CompanionSetup>();
                if (setup == null)
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[InvGUI] Show — container \"{container.name}\" has CompanionInteract but no CompanionSetup!");
                    return;
                }

                _companionContainerOpen = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[InvGUI] Show — COMPANION container opened for \"{container.name}\", " +
                    $"hiding vanilla panels");

                // Hide vanilla container and crafting panels — our custom UI replaces them
                HideVanillaPanels();

                CompanionInteractPanel.EnsureInstance();
                CompanionInteractPanel.Instance?.Show(setup);

                CompanionsPlugin.Log.LogDebug(
                    $"[InvGUI] Show — CompanionInteractPanel visible={CompanionInteractPanel.Instance?.IsVisible}");
            }
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class Hide_Patch
        {
            static void Postfix()
            {
                if (_companionContainerOpen)
                    CompanionsPlugin.Log.LogInfo("[InvGUI] Hide — closing companion container session");
                _companionContainerOpen = false;
                CompanionInteractPanel.Instance?.Hide();
            }
        }

        /// <summary>
        /// Vanilla UpdateContainer re-enables m_container.gameObject every frame
        /// when a container is open, and also runs hold-to-stack-all logic.
        /// Prefix skips the entire method when a companion container is open —
        /// our CompanionInteractPanel handles everything instead.
        /// We still call SetInUse(true) to keep the container session alive.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        private static class UpdateContainer_Patch
        {
            private static float _lastLogTime;

            static bool Prefix(Player player)
            {
                if (!_companionContainerOpen) return true;

                var gui = InventoryGui.instance;
                if (gui == null) return true;

                // If container was lost or ownership changed, fall through to vanilla cleanup
                var container = _currentContainerField?.GetValue(gui) as Container;
                if (container == null || !container.IsOwner())
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[InvGUI] UpdateContainer — companion container lost " +
                        $"(null={container == null}, owner={container?.IsOwner()}), " +
                        $"falling through to vanilla");
                    _companionContainerOpen = false;
                    return true;
                }

                // Keep the container session alive (vanilla does this in UpdateContainer)
                container.SetInUse(true);

                // Ensure vanilla panels stay hidden (belt-and-suspenders)
                bool containerWasActive = gui.m_container != null &&
                                          gui.m_container.gameObject.activeSelf;
                bool craftingWasActive = gui.m_crafting != null &&
                                         gui.m_crafting.gameObject.activeSelf;
                HideVanillaPanels();

                // Throttled warning if vanilla somehow re-enabled panels
                if ((containerWasActive || craftingWasActive) &&
                    Time.time - _lastLogTime > 2f)
                {
                    _lastLogTime = Time.time;
                    CompanionsPlugin.Log.LogWarning(
                        $"[InvGUI] UpdateContainer — vanilla panels were active " +
                        $"(container={containerWasActive}, crafting={craftingWasActive}), " +
                        $"re-hidden");
                }

                return false; // Skip vanilla UpdateContainer entirely
            }
        }

        private static void HideVanillaPanels()
        {
            if (InventoryGui.instance == null) return;
            if (InventoryGui.instance.m_container != null)
                InventoryGui.instance.m_container.gameObject.SetActive(false);
            if (InventoryGui.instance.m_crafting != null)
                InventoryGui.instance.m_crafting.gameObject.SetActive(false);
        }
    }
}
