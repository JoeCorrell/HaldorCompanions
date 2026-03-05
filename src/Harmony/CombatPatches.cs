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
    }
}
