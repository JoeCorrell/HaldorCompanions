using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Synchronises CompanionInteractPanel visibility with InventoryGui.
    /// When a companion's Container opens, we show the panel and hide
    /// the vanilla container grid.  When InventoryGui closes, we hide the panel.
    /// </summary>
    public static class InventoryGuiPatches
    {
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        private static class Show_Patch
        {
            static void Postfix(Container container)
            {
                if (container == null) return;
                var ci = container.GetComponent<CompanionInteract>();
                if (ci == null) return;

                var setup = container.GetComponent<CompanionSetup>();
                if (setup == null) return;

                // Hide vanilla container and crafting panels — our custom UI replaces them
                if (InventoryGui.instance != null)
                {
                    if (InventoryGui.instance.m_container != null)
                        InventoryGui.instance.m_container.gameObject.SetActive(false);
                    if (InventoryGui.instance.m_crafting != null)
                        InventoryGui.instance.m_crafting.gameObject.SetActive(false);
                }

                CompanionInteractPanel.EnsureInstance();
                CompanionInteractPanel.Instance?.Show(setup);
            }
        }

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        private static class Hide_Patch
        {
            static void Postfix()
            {
                CompanionInteractPanel.Instance?.Hide();
            }
        }

        /// <summary>
        /// Vanilla UpdateContainer re-enables m_container every frame.
        /// Keep it hidden while our companion panel is visible.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), "UpdateContainer")]
        private static class UpdateContainer_Patch
        {
            static void Postfix(InventoryGui __instance)
            {
                if (CompanionInteractPanel.Instance != null &&
                    CompanionInteractPanel.Instance.IsVisible)
                {
                    if (__instance.m_container != null)
                        __instance.m_container.gameObject.SetActive(false);
                    if (__instance.m_crafting != null)
                        __instance.m_crafting.gameObject.SetActive(false);
                }
            }
        }
    }
}
