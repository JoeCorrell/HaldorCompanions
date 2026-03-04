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

            // Reset cached vanilla data so it's re-read from the new world's ObjectDB
            CompanionRestedBuff.ResetCache();
        }
    }

    /// <summary>
    /// For Player-clone companions: replaces the Container's auto-created inventory
    /// with Humanoid's existing inventory so both components share the same Inventory.
    ///
    /// For Dverger variants: inventories are kept separate to avoid exposing the
    /// Humanoid's fixed default equipment in the storage UI.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class ContainerAwakePatch
    {
        private const int CompanionInventoryWidth = 8;
        private const int CompanionInventoryHeight = 4;

        private static readonly FieldInfo _inventoryField =
            AccessTools.Field(typeof(Container), "m_inventory");
        internal static readonly FieldInfo _invWidthField =
            AccessTools.Field(typeof(Inventory), "m_width");
        internal static readonly FieldInfo _invHeightField =
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

            // Keep container metadata aligned with companion inventory shape.
            __instance.m_width = CompanionInventoryWidth;
            __instance.m_height = CompanionInventoryHeight;

            // Dverger variants: keep inventories separate.
            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null &&
                CompanionTierData.IsDvergerVariant(nview.GetZDO().GetPrefab()))
            {
                ForceInventoryDimensions(__instance.GetInventory(), CompanionInventoryWidth, CompanionInventoryHeight);
                CompanionsPlugin.Log.LogDebug("[ContainerAwake] Dverger variant, keeping separate inventories");
                return;
            }

            // Fallback when ZDO is not ready yet.
            if ((nview == null || nview.GetZDO() == null) && __instance.gameObject.name.Contains("Dverger"))
            {
                ForceInventoryDimensions(__instance.GetInventory(), CompanionInventoryWidth, CompanionInventoryHeight);
                CompanionsPlugin.Log.LogDebug("[ContainerAwake] Dverger variant (name check), keeping separate inventories");
                return;
            }

            var humanoidInv = humanoid.GetInventory();
            if (_inventoryField == null || humanoidInv == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[ContainerAwake] Cannot share inventory, field={_inventoryField != null} humanoidInv={humanoidInv != null}");
                return;
            }

            int width = CompanionInventoryWidth;
            int height = CompanionInventoryHeight;
            int oldW = humanoidInv.GetWidth();
            int oldH = humanoidInv.GetHeight();

            ForceInventoryDimensions(humanoidInv, width, height);

            // Replace Container's inventory with Humanoid's inventory
            _inventoryField.SetValue(__instance, humanoidInv);

            // Re-register Container.OnContainerChanged on the shared inventory
            // so that Container.Save() fires when items change.
            if (_onContainerChanged != null)
            {
                var callback = (Action)Delegate.CreateDelegate(typeof(Action), __instance, _onContainerChanged);
                humanoidInv.m_onChanged = (Action)Delegate.Remove(humanoidInv.m_onChanged, callback);
                humanoidInv.m_onChanged = (Action)Delegate.Combine(humanoidInv.m_onChanged, callback);
            }

            CompanionsPlugin.Log.LogDebug(
                $"[ContainerAwake] Shared inventory, container={__instance.m_width}x{__instance.m_height} " +
                $"humanoidInv dim {oldW}x{oldH} -> {width}x{height} items={humanoidInv.NrOfItems()}");
        }

        private static void ForceInventoryDimensions(Inventory inv, int width, int height)
        {
            if (inv == null) return;
            int oldW = inv.GetWidth();
            NormalizeLegacyInventoryLayout(inv, oldW, width);
            _invWidthField?.SetValue(inv, width);
            _invHeightField?.SetValue(inv, height);
        }

        private static void NormalizeLegacyInventoryLayout(Inventory inv, int sourceWidth, int targetWidth)
        {
            if (inv == null || targetWidth <= 0) return;

            var items = inv.GetAllItems();
            if (items == null || items.Count == 0) return;

            int oldW = sourceWidth > 0 ? sourceWidth : targetWidth;
            bool needsNormalize = oldW != targetWidth;

            if (!needsNormalize)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item == null) continue;
                    if (item.m_gridPos.x >= targetWidth)
                    {
                        needsNormalize = true;
                        break;
                    }
                }
            }

            if (!needsNormalize) return;

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item == null) continue;
                int linear = Math.Max(0, item.m_gridPos.y) * oldW + Math.Max(0, item.m_gridPos.x);
                item.m_gridPos = new Vector2i(linear % targetWidth, linear / targetWidth);
            }

            CompanionsPlugin.Log.LogDebug(
                $"[ContainerAwake] Normalized companion inventory layout to {targetWidth}-wide grid");
        }
    }

    /// <summary>
    /// Restores companion tombstone inventory dimensions on zone reload.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class TombstoneContainerAwakePatch
    {
        [HarmonyPostfix]
        [HarmonyAfter("companions.containerawake")]
        static void Postfix(Container __instance)
        {
            var tombstone = __instance.GetComponent<TombStone>();
            if (tombstone == null) return;

            var nview = __instance.GetComponent<ZNetView>();
            var zdo = nview?.GetZDO();
            if (zdo == null) return;

            int w = zdo.GetInt(CompanionSetup.TombInvWidthHash, 0);
            int h = zdo.GetInt(CompanionSetup.TombInvHeightHash, 0);
            if (w <= 0 || h <= 0) return;

            var inv = __instance.GetInventory();
            if (inv == null) return;

            int curW = inv.GetWidth();
            int curH = inv.GetHeight();
            if (curW == w && curH == h) return;

            if (ContainerAwakePatch._invWidthField != null)
                ContainerAwakePatch._invWidthField.SetValue(inv, w);
            if (ContainerAwakePatch._invHeightField != null)
                ContainerAwakePatch._invHeightField.SetValue(inv, h);

            CompanionsPlugin.Log.LogDebug(
                $"[TombstoneAwake] Restored companion tombstone inventory dims: {curW}x{curH} -> {w}x{h}");
        }
    }

    /// <summary>
    /// Ensures the CompanionInteractPanel singleton exists when InventoryGui initializes.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), "Awake")]
    public static class InventoryGuiAwakePatch
    {
        [HarmonyPostfix]
        static void Postfix()
        {
            CompanionInteractPanel.EnsureInstance();
            CompanionRadialMenu.EnsureInstance();
            CompanionRadialMenu.WarmCache();
        }
    }
}
