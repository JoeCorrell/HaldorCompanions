using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Bridge Character.UseStamina/HaveStamina to CompanionStamina.
    /// Intercepts vanilla no-op stamina methods and routes them to the custom system.
    /// </summary>
    public static class StaminaPatches
    {
        [HarmonyPatch(typeof(Character), nameof(Character.UseStamina))]
        private static class UseStamina_Patch
        {
            static bool Prefix(Character __instance, float stamina)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true;

                cs.Drain(stamina);
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.HaveStamina))]
        private static class HaveStamina_Patch
        {
            static bool Prefix(Character __instance, float amount, ref bool __result)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true;

                __result = cs.Stamina >= amount;
                return false;
            }
        }
    }
}
