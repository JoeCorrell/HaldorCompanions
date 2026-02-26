using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Suppresses MonsterAI.UpdateTarget while the companion is actively
    /// in a gather mode. Without this, the companion would constantly
    /// acquire enemy targets and fight instead of harvesting.
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
    internal static class UpdateTarget_HarvestPatch
    {
        // Per-instance log timers keyed by instance ID
        private static readonly Dictionary<int, float> _logTimers = new Dictionary<int, float>();

        static bool Prefix(MonsterAI __instance)
        {
            var harvest = __instance.GetComponent<HarvestController>();
            if (harvest == null) return true;

            if (harvest.IsInGatherMode)
            {
                // Per-instance periodic logging (not every frame)
                int id = __instance.GetInstanceID();
                _logTimers.TryGetValue(id, out float timer);
                timer -= Time.deltaTime;
                if (timer <= 0f)
                {
                    timer = 3f;
                    var creature = ReflectionHelper.GetTargetCreature(__instance);
                    var staticTarget = ReflectionHelper.GetTargetStatic(__instance);
                    var character = __instance.GetComponent<Character>();
                    string name = character?.m_name ?? "?";
                    CompanionsPlugin.Log.LogInfo(
                        $"[TargetPatch|{name}] Suppressing UpdateTarget — " +
                        $"IsActive={harvest.IsActive} " +
                        $"hadCreature=\"{creature?.m_name ?? ""}\" " +
                        $"hadStatic=\"{staticTarget?.name ?? ""}\"");
                }
                _logTimers[id] = timer;

                ReflectionHelper.ClearAllTargets(__instance);
                return false;
            }
            return true;
        }
    }
}
