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
    /// with Humanoid's existing inventory so both components share the same Inventory
    /// instance.  This lets items placed in the Container be equipped by the Humanoid.
    ///
    /// For Dverger variants: inventories are kept separate.  The Humanoid inventory
    /// holds fixed default gear (staffs, suits) that GiveDefaultItems() adds at spawn.
    /// Sharing would expose those items in the Container UI as invisible slots,
    /// letting the player accidentally pick them up — which causes
    /// IndexOutOfRangeException in InventoryGrid.UpdateGui and breaks the game UI.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class ContainerAwakePatch
    {
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

            // Dverger variants: keep inventories separate.
            // Their Humanoid inventory holds fixed default gear (staffs, suits, projectile
            // attacks) from GiveDefaultItems().  The Container gets its own clean inventory
            // for player storage.
            var nview = __instance.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null &&
                CompanionTierData.IsDvergerVariant(nview.GetZDO().GetPrefab()))
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[ContainerAwake] Dverger variant — keeping separate inventories");
                return;
            }
            // Also check prefab name as fallback (ZDO may not be ready yet)
            if (nview == null || nview.GetZDO() == null)
            {
                if (__instance.gameObject.name.Contains("Dverger"))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[ContainerAwake] Dverger variant (name check) — keeping separate inventories");
                    return;
                }
            }

            var humanoidInv = humanoid.GetInventory();
            if (_inventoryField == null || humanoidInv == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[ContainerAwake] Cannot share inventory — " +
                    $"field={_inventoryField != null} humanoidInv={humanoidInv != null}");
                return;
            }

            // Match Humanoid inventory dimensions to the Container definition.
            // Companion UI expects the same grid shape for deterministic slot mapping.
            int width  = __instance.m_width > 0 ? __instance.m_width : 4;
            int height = __instance.m_height > 0 ? __instance.m_height : 8;
            int oldW = humanoidInv.GetWidth();
            int oldH = humanoidInv.GetHeight();
            if (_invWidthField != null)  _invWidthField.SetValue(humanoidInv, width);
            if (_invHeightField != null) _invHeightField.SetValue(humanoidInv, height);

            // Replace Container's inventory with Humanoid's inventory
            _inventoryField.SetValue(__instance, humanoidInv);

            // Re-register Container.OnContainerChanged on the shared inventory
            // so that Container.Save() fires when items change (ZDO persistence).
            // Remove first to guard against duplicate subscriptions.
            if (_onContainerChanged != null)
            {
                var callback = (Action)Delegate.CreateDelegate(typeof(Action), __instance, _onContainerChanged);
                humanoidInv.m_onChanged = (Action)Delegate.Remove(humanoidInv.m_onChanged, callback);
                humanoidInv.m_onChanged = (Action)Delegate.Combine(humanoidInv.m_onChanged, callback);
            }

            CompanionsPlugin.Log.LogDebug(
                $"[ContainerAwake] Shared inventory — " +
                $"container={__instance.m_width}x{__instance.m_height} " +
                $"humanoidInv dim {oldW}x{oldH} → {width}x{height} " +
                $"items={humanoidInv.NrOfItems()}");
        }
    }

    /// <summary>
    /// Restores companion tombstone inventory dimensions on zone reload.
    ///
    /// Inventory.Save/Load does NOT persist m_width/m_height. When a tombstone
    /// unloads and reloads (player leaves zone and returns), Container.Awake
    /// creates a new inventory with the prefab's default dimensions (typically 3×2).
    /// Inventory.Load then calls AddItem(item, amount, x, y) which silently drops
    /// items where x >= m_width or y >= m_height. For companion inventories
    /// (8×4 = 32 slots), most items are lost.
    ///
    /// We save the correct dimensions in custom ZDO fields (HC_TombInvW/H) during
    /// tombstone creation, then restore them here before the first CheckForChanges
    /// → Load cycle runs.
    /// </summary>
    [HarmonyPatch(typeof(Container), "Awake")]
    public static class TombstoneContainerAwakePatch
    {
        [HarmonyPostfix]
        [HarmonyAfter("companions.containerawake")]  // run after companion sharing patch
        static void Postfix(Container __instance)
        {
            // Only apply to tombstones — they have a TombStone component
            var tombstone = __instance.GetComponent<TombStone>();
            if (tombstone == null) return;

            var nview = __instance.GetComponent<ZNetView>();
            var zdo = nview?.GetZDO();
            if (zdo == null) return;

            int w = zdo.GetInt(CompanionSetup.TombInvWidthHash, 0);
            int h = zdo.GetInt(CompanionSetup.TombInvHeightHash, 0);
            if (w <= 0 || h <= 0) return;  // not a companion tombstone

            var inv = __instance.GetInventory();
            if (inv == null) return;

            int curW = inv.GetWidth();
            int curH = inv.GetHeight();
            if (curW == w && curH == h) return;  // already correct

            if (ContainerAwakePatch._invWidthField != null)
                ContainerAwakePatch._invWidthField.SetValue(inv, w);
            if (ContainerAwakePatch._invHeightField != null)
                ContainerAwakePatch._invHeightField.SetValue(inv, h);

            CompanionsPlugin.Log.LogDebug(
                $"[TombstoneAwake] Restored companion tombstone inventory dims: " +
                $"{curW}x{curH} → {w}x{h}");
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
            CompanionRadialMenu.EnsureInstance();
            CompanionRadialMenu.WarmCache();
        }
    }
}
