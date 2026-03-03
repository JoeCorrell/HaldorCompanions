using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Harmony patches that extend vanilla Humanoid methods to support companion
    /// extra utility slots when ExtraSlots mod is installed. Each patch early-returns
    /// when ExtraSlots is not loaded or when the humanoid is not a companion.
    /// </summary>
    public static class ExtraSlotsPatches
    {
        // ── Reflection for private/protected members ────────────────────────
        private static readonly FieldInfo _equipStatusEffectsField =
            AccessTools.Field(typeof(Humanoid), "m_equipmentStatusEffects");

        private static readonly MethodInfo _getSetCountMethod =
            AccessTools.Method(typeof(Humanoid), "GetSetCount", new[] { typeof(string) });

        private static readonly MethodInfo _drainDurabilityMethod =
            AccessTools.Method(typeof(Humanoid), "DrainEquipedItemDurability",
                new[] { typeof(ItemDrop.ItemData), typeof(float) });

        private static readonly MethodInfo _setupEquipmentMethod =
            AccessTools.Method(typeof(Humanoid), "SetupEquipment");

        // Companion extra utility SEs collected in Prefix, used by RemoveStatusEffect guard
        private static readonly HashSet<StatusEffect> _companionTempEffects = new HashSet<StatusEffect>();

        /// <summary>
        /// Call Humanoid.SetupEquipment() via reflection (private method).
        /// Triggers visual updates and status effect recalculation.
        /// </summary>
        internal static void CallSetupEquipment(Humanoid h)
        {
            _setupEquipmentMethod?.Invoke(h, null);
        }

        /// <summary>
        /// Prefix: Collect companion extra utility SEs into _companionTempEffects BEFORE
        /// vanilla runs. Vanilla's loop will try to remove SEs not in its hashset —
        /// the RemoveStatusEffect prefix below blocks removal of these SEs.
        ///
        /// Postfix: After vanilla rebuilds m_equipmentStatusEffects, add companion
        /// extra utility SEs back into the set and add them to SEMan.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), "UpdateEquipmentStatusEffects")]
        private static class UpdateEquipmentStatusEffects_Companion
        {
            static void Prefix(Humanoid __instance)
            {
                _companionTempEffects.Clear();
                if (!ExtraSlotsCompat.IsLoaded) return;

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                int count = setup.GetExtraUtilityCount();
                for (int i = 0; i < count; i++)
                {
                    var item = setup.GetExtraUtilityItem(i);
                    if (item == null) continue;

                    if (item.m_shared.m_equipStatusEffect != null)
                        _companionTempEffects.Add(item.m_shared.m_equipStatusEffect);

                    if (HaveSetEffect(__instance, item))
                        _companionTempEffects.Add(item.m_shared.m_setStatusEffect);
                }
            }

            static void Postfix(Humanoid __instance)
            {
                if (_companionTempEffects.Count == 0) return;

                var equipSEs = _equipStatusEffectsField?.GetValue(__instance) as HashSet<StatusEffect>;
                if (equipSEs == null) return;

                var seman = ((Character)__instance).GetSEMan();
                if (seman == null) return;

                foreach (var se in _companionTempEffects)
                {
                    if (!equipSEs.Contains(se))
                        seman.AddStatusEffect(se, false, 0, 0f);
                    equipSEs.Add(se);
                }

                _companionTempEffects.Clear();
            }
        }

        /// <summary>
        /// Prevent vanilla from removing companion extra utility SEs during
        /// UpdateEquipmentStatusEffects. Vanilla rebuilds its SE set from slot fields
        /// only (no extra utilities) and removes anything not in the set. By setting
        /// nameHash=0, RemoveStatusEffect becomes a no-op for that SE.
        /// Same pattern ExtraSlots uses for player extra utilities.
        /// </summary>
        [HarmonyPatch(typeof(SEMan), "RemoveStatusEffect", new Type[]
        {
            typeof(int),
            typeof(bool)
        })]
        private static class RemoveStatusEffect_CompanionPreventRemoval
        {
            static void Prefix(ref int nameHash)
            {
                if (_companionTempEffects.Count == 0) return;

                foreach (var se in _companionTempEffects)
                {
                    if (se.NameHash() == nameHash)
                    {
                        nameHash = 0;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Count companion extra utility items in armor set calculation.
        /// ExtraSlots' own postfix only fires for IsValidPlayer.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), "GetSetCount")]
        private static class GetSetCount_Companion
        {
            static void Postfix(Humanoid __instance, string setName, ref int __result)
            {
                if (!ExtraSlotsCompat.IsLoaded) return;

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                int count = setup.GetExtraUtilityCount();
                for (int i = 0; i < count; i++)
                {
                    var item = setup.GetExtraUtilityItem(i);
                    if (item != null && item.m_shared.m_setName == setName)
                        __result++;
                }
            }
        }

        /// <summary>
        /// Add weight of companion extra utility items to equipment weight.
        /// ExtraSlots' own postfix only fires for IsValidPlayer.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), "GetEquipmentWeight")]
        private static class GetEquipmentWeight_Companion
        {
            static void Postfix(Humanoid __instance, ref float __result)
            {
                if (!ExtraSlotsCompat.IsLoaded) return;

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                int count = setup.GetExtraUtilityCount();
                for (int i = 0; i < count; i++)
                {
                    var item = setup.GetExtraUtilityItem(i);
                    if (item != null)
                        __result += item.m_shared.m_weight;
                }
            }
        }

        /// <summary>
        /// Return true if item is in companion's extra utility slots.
        /// ExtraSlots' own postfix only fires for IsValidPlayer.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), "IsItemEquiped")]
        private static class IsItemEquiped_Companion
        {
            static void Postfix(Humanoid __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (__result) return;
                if (!ExtraSlotsCompat.IsLoaded) return;

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                int count = setup.GetExtraUtilityCount();
                for (int i = 0; i < count; i++)
                {
                    if (setup.GetExtraUtilityItem(i) == item)
                    {
                        __result = true;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// Drain durability on companion extra utility items each frame.
        /// Vanilla UpdateEquipment drains m_utilityItem but not extra slots.
        /// ExtraSlots' own postfix only fires for IsValidPlayer.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), "UpdateEquipment")]
        private static class UpdateEquipment_CompanionDurability
        {
            static void Postfix(Humanoid __instance, float dt)
            {
                if (!ExtraSlotsCompat.IsLoaded) return;
                if (_drainDurabilityMethod == null) return;

                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return;

                int count = setup.GetExtraUtilityCount();
                for (int i = 0; i < count; i++)
                {
                    var item = setup.GetExtraUtilityItem(i);
                    if (item != null && item.m_shared.m_useDurability)
                    {
                        _drainDurabilityMethod.Invoke(__instance, new object[] { item, dt });
                    }
                }
            }
        }

        /// <summary>
        /// Re-implementation of Humanoid.HaveSetEffect (private method).
        /// Uses GetSetCount via reflection to check if the full set is equipped.
        /// </summary>
        private static bool HaveSetEffect(Humanoid h, ItemDrop.ItemData item)
        {
            if (item?.m_shared?.m_setStatusEffect == null) return false;
            if (string.IsNullOrEmpty(item.m_shared.m_setName)) return false;
            if (item.m_shared.m_setSize <= 0) return false;
            if (_getSetCountMethod == null) return false;

            int setCount = (int)_getSetCountMethod.Invoke(h, new object[] { item.m_shared.m_setName });
            return setCount >= item.m_shared.m_setSize;
        }
    }
}
