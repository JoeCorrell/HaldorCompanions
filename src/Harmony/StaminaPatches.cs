using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Bridge Character.UseStamina/HaveStamina to CompanionStamina.
    /// Intercepts vanilla no-op stamina methods and routes them to the custom system.
    /// </summary>
    public static class StaminaPatches
    {
        private static float _lastLogTime;
        private const float LogInterval = 2f;

        [HarmonyPatch(typeof(Character), nameof(Character.UseStamina))]
        private static class UseStamina_Patch
        {
            static bool Prefix(Character __instance, float stamina)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true;

                float before = cs.Stamina;
                cs.Drain(stamina);
                CompanionsPlugin.Log.LogInfo(
                    $"[Stamina] UseStamina — drained {stamina:F1} " +
                    $"({before:F1} → {cs.Stamina:F1} / {cs.MaxStamina:F1}) " +
                    $"companion=\"{__instance.m_name}\"");
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

                // Throttled logging to avoid spam (HaveStamina is called very frequently)
                if (!__result || UnityEngine.Time.time - _lastLogTime > LogInterval)
                {
                    _lastLogTime = UnityEngine.Time.time;
                    if (!__result)
                        CompanionsPlugin.Log.LogWarning(
                            $"[Stamina] HaveStamina FAILED — need {amount:F1} " +
                            $"have {cs.Stamina:F1} / {cs.MaxStamina:F1} " +
                            $"companion=\"{__instance.m_name}\"");
                }
                return false;
            }
        }
    }
}
