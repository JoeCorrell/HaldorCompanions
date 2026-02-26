using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Perfect parry timing and food-based max health for companions.
    /// Uses ReflectionHelper for safe block timer access.
    /// </summary>
    public static class CombatPatches
    {
        private static float _lastHealthLogTime;
        private const float HealthLogInterval = 5f;

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class BlockAttack_Patch
        {
            static void Prefix(Humanoid __instance)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                float timer = ReflectionHelper.GetBlockTimer(__instance);
                if (timer >= 0f)
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] BlockAttack — setting block timer to 0 for parry " +
                        $"(was {timer:F3}) companion=\"{__instance.m_name}\"");
                    ReflectionHelper.TrySetBlockTimer(__instance, 0f);
                }
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealth))]
        private static class GetMaxHealth_Patch
        {
            static void Postfix(Character __instance, ref float __result)
            {
                var food = __instance.GetComponent<CompanionFood>();
                if (food == null) return;

                float baseHp = CompanionFood.BaseHealth;
                float foodBonus = food.TotalHealthBonus;
                float newMax = baseHp + foodBonus;

                // Throttled logging — GetMaxHealth is called frequently
                if (UnityEngine.Time.time - _lastHealthLogTime > HealthLogInterval)
                {
                    _lastHealthLogTime = UnityEngine.Time.time;
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] GetMaxHealth — base={baseHp:F0} + food={foodBonus:F1} " +
                        $"= {newMax:F1} (vanilla was {__result:F1}) " +
                        $"companion=\"{__instance.m_name}\"");
                }

                __result = newMax;
            }
        }
    }
}
