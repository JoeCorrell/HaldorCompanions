using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Perfect parry timing and food-based max health for companions.
    /// Uses ReflectionHelper for safe block timer access.
    ///
    /// Note: EquipBestWeapon and DoAttack patches were removed — CompanionAI
    /// owns weapon selection and attack logic directly (no MonsterAI).
    /// </summary>
    public static class CombatPatches
    {
        private static float _lastHealthLogTime;
        private const float HealthLogInterval = 5f;

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class BlockAttack_Patch
        {
            static void Prefix(Humanoid __instance, HitData hit, Character attacker)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                float timer = ReflectionHelper.GetBlockTimer(__instance);
                string attackerName = attacker != null ? attacker.m_name : "unknown";
                float totalDmg = hit != null ? hit.GetTotalDamage() : 0f;

                if (timer >= 0f)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] PARRY — BlockAttack fired! timer={timer:F3}→0 " +
                        $"attacker=\"{attackerName}\" dmg={totalDmg:F0} " +
                        $"companion=\"{__instance.m_name}\"");
                    ReflectionHelper.TrySetBlockTimer(__instance, 0f);
                }
                else
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[Combat] BlockAttack fired but timer={timer:F3} (not blocking?) " +
                        $"attacker=\"{attackerName}\" dmg={totalDmg:F0} " +
                        $"companion=\"{__instance.m_name}\"");
                }
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
