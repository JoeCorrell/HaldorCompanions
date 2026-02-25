using System;
using System.Reflection;
using HarmonyLib;

namespace Companions
{
    [HarmonyPatch(typeof(ZNetScene), "Awake")]
    public static class ZNetScenePatch
    {
        [HarmonyPostfix]
        static void Postfix(ZNetScene __instance)
        {
            CompanionPrefabs.Init(__instance);
        }
    }

    /// <summary>
    /// Replaces the Container's auto-created inventory with Humanoid's existing
    /// inventory so both components share the same Inventory instance.
    /// Re-registers Container.OnContainerChanged on the shared inventory so
    /// Container.Save() is called when items change (ZDO persistence).
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class ContainerAwakePatch
    {
        private static readonly FieldInfo _inventoryField =
            AccessTools.Field(typeof(Container), "m_inventory");
        private static readonly FieldInfo _invWidthField =
            AccessTools.Field(typeof(Inventory), "m_width");
        private static readonly FieldInfo _invHeightField =
            AccessTools.Field(typeof(Inventory), "m_height");
        private static readonly MethodInfo _onContainerChanged =
            AccessTools.Method(typeof(Container), "OnContainerChanged");

        [HarmonyPostfix]
        static void Postfix(Container __instance)
        {
            var companion = __instance.GetComponent<CompanionSetup>();
            if (companion == null) return;

            var humanoid = __instance.GetComponent<Humanoid>();
            if (humanoid == null) return;

            var humanoidInv = humanoid.GetInventory();
            if (_inventoryField == null || humanoidInv == null) return;

            // Resize the Humanoid's inventory to 5x5 (25 slots) to match Container
            if (_invWidthField != null)  _invWidthField.SetValue(humanoidInv, 5);
            if (_invHeightField != null) _invHeightField.SetValue(humanoidInv, 5);

            // Replace Container's inventory with Humanoid's inventory
            _inventoryField.SetValue(__instance, humanoidInv);

            // Re-register Container.OnContainerChanged on the shared inventory
            // so that Container.Save() fires when items change (ZDO persistence)
            if (_onContainerChanged != null)
            {
                var callback = (Action)Delegate.CreateDelegate(typeof(Action), __instance, _onContainerChanged);
                humanoidInv.m_onChanged = (Action)Delegate.Combine(humanoidInv.m_onChanged, callback);
            }
        }
    }

    /// <summary>
    /// Ensures the CompanionInteractPanel singleton exists when InventoryGui initialises.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "Awake")]
    public static class InventoryGuiAwakePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            CompanionInteractPanel.EnsureInstance();
        }
    }
}
