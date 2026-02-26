using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Perfect parry timing and food-based max health for companions.
    /// Uses ReflectionHelper for safe block timer access.
    /// </summary>
    public static class CombatPatches
    {
        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class BlockAttack_Patch
        {
            static void Prefix(Humanoid __instance)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                float timer = ReflectionHelper.GetBlockTimer(__instance);
                if (timer >= 0f)
                    ReflectionHelper.TrySetBlockTimer(__instance, 0f);
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetMaxHealth))]
        private static class GetMaxHealth_Patch
        {
            static void Postfix(Character __instance, ref float __result)
            {
                var food = __instance.GetComponent<CompanionFood>();
                if (food == null) return;
                __result = CompanionFood.BaseHealth + food.TotalHealthBonus;
            }
        }
    }
}
