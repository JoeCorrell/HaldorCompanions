using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Damage mitigation patches for companions.
    /// </summary>
    public static class CombatPatches
    {
        private static float _lastHealthLogTime;
        private const float HealthLogInterval = 5f;

        /// <summary>
        /// Neutralize backstab damage multiplier on companions.
        /// Vanilla checks m_baseAI != null && hit.m_backstabBonus > 1f — since companions
        /// have a BaseAI, they'd take full backstab damage from behind. Setting the bonus
        /// to 1f before the check means the multiplier is never applied.
        /// </summary>
        [HarmonyPatch(typeof(Character), "RPC_Damage")]
        private static class RPC_Damage_NoBackstab
        {
            static void Prefix(Character __instance, HitData hit)
            {
                if (hit == null) return;
                if (hit.m_backstabBonus <= 1f) return;
                if (__instance.GetComponent<CompanionSetup>() == null) return;
                hit.m_backstabBonus = 1f;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealth))]
        private static class GetMaxHealth_Patch
        {
            static void Postfix(Character __instance, ref float __result)
            {
                // Early-out: skip non-companion characters cheaply
                var ai = __instance.GetBaseAI();
                if (ai == null || !(ai is CompanionAI)) return;
                var food = __instance.GetComponent<CompanionFood>();
                if (food == null) return;

                float baseHp = CompanionFood.BaseHealth;
                float foodBonus = food.TotalHealthBonus;
                float newMax = baseHp + foodBonus;

                // Throttled logging — GetMaxHealth is called frequently
                if (UnityEngine.Time.time - _lastHealthLogTime > HealthLogInterval)
                {
                    _lastHealthLogTime = UnityEngine.Time.time;
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] GetMaxHealth — base={baseHp:F0} + food={foodBonus:F1} " +
                        $"= {newMax:F1} (vanilla was {__result:F1}) " +
                        $"companion=\"{__instance.m_name}\"");
                }

                __result = newMax;
            }
        }

        /// <summary>
        /// Prevent companions from using tools as combat weapons.
        /// EquipBestWeapon calls BaseAI.CanUseAttack for each candidate — returning
        /// false here stops fishing rods, pickaxes, and other tools from being selected.
        /// </summary>
        [HarmonyPatch(typeof(BaseAI), nameof(BaseAI.CanUseAttack))]
        public static class ToolCombatExclude
        {
            static void Postfix(BaseAI __instance, ItemDrop.ItemData item, ref bool __result)
            {
                if (!__result) return;
                if (!(__instance is CompanionAI)) return;

                // Fishing rod
                if (item.m_shared.m_animationState == ItemDrop.ItemData.AnimationState.FishingRod)
                {
                    __result = false;
                    return;
                }

                // Pickaxes — TwoHandedWeapon with pickaxe damage
                if (item.GetDamage().m_pickaxe > 0f)
                {
                    __result = false;
                    return;
                }

                // Tool item type (cultivators, hammers, hoes)
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool)
                {
                    __result = false;
                }
            }
        }

        /// <summary>
        /// Vanilla EquipBestWeapon picks RANDOMLY from all valid in-range weapons.
        /// For companions, override with highest combat damage selection so a flint
        /// axe (low slash + high chop) doesn't beat a proper sword/battleaxe.
        /// </summary>
        [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.EquipBestWeapon))]
        private static class EquipBestWeapon_PreferHighestDamage
        {
            static void Postfix(Humanoid __instance)
            {
                var baseAI = __instance.GetBaseAI();
                if (baseAI == null || !(baseAI is CompanionAI)) return;

                var current = __instance.GetCurrentWeapon();
                if (current == null) return;

                float currentCombatDmg = GetCombatDamage(current);
                var inv = __instance.GetInventory();
                if (inv == null) return;

                ItemDrop.ItemData best = current;
                float bestDmg = currentCombatDmg;

                foreach (var item in inv.GetAllItems())
                {
                    if (item == null || item.m_shared == null) continue;
                    if (!item.IsWeapon()) continue;
                    if (!baseAI.CanUseAttack(item)) continue;
                    if (item.m_shared.m_aiTargetType != ItemDrop.ItemData.AiTarget.Enemy) continue;
                    if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;

                    float dmg = GetCombatDamage(item);
                    if (dmg > bestDmg)
                    {
                        best = item;
                        bestDmg = dmg;
                    }
                }

                if (best != current)
                    __instance.EquipItem(best);
            }

            /// <summary>Combat-only damage — excludes chop/pickaxe which don't hurt enemies.</summary>
            private static float GetCombatDamage(ItemDrop.ItemData item)
            {
                var d = item.GetDamage();
                return d.m_damage + d.m_blunt + d.m_slash + d.m_pierce
                     + d.m_fire + d.m_frost + d.m_lightning + d.m_poison + d.m_spirit;
            }
        }
    }
}
