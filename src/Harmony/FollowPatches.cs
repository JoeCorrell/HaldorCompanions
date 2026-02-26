using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Sets CompanionStamina.IsRunning when BaseAI.Follow decides to run
    /// (distance > 10m from follow target). Without this patch, companions
    /// never drain stamina from running.
    /// </summary>
    [HarmonyPatch(typeof(BaseAI), "Follow")]
    internal static class Follow_RunDetection
    {
        static void Postfix(BaseAI __instance, GameObject go)
        {
            var stamina = __instance.GetComponent<CompanionStamina>();
            if (stamina == null) return;

            float dist = Vector3.Distance(
                go.transform.position, __instance.transform.position);
            stamina.IsRunning = dist > 10f;
        }
    }
}
