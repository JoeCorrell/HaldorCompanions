using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Controls target acquisition for companions:
    /// - Suppresses targeting while UI is open (prevents combat during interaction)
    /// - During gathering, only suppresses targeting when no enemies are within
    ///   self-defense range — companions will fight back if attacked nearby
    /// - Adds detailed logging to MonsterAI.UpdateAI for debugging follow/combat decisions
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
    internal static class UpdateTarget_HarvestPatch
    {
        private const float SelfDefenseRange = 10f;

        // Per-instance log timers keyed by instance ID
        private static readonly Dictionary<int, float> _logTimers = new Dictionary<int, float>();

        static bool Prefix(MonsterAI __instance)
        {
            var setup = __instance.GetComponent<CompanionSetup>();
            if (setup == null) return true;

            // Always suppress targeting while the player has this companion's UI open
            var panel = CompanionInteractPanel.Instance;
            bool uiOpen = panel != null && panel.IsVisible && panel.CurrentCompanion == setup;

            if (uiOpen)
            {
                LogSuppression(__instance, "UIOpen");
                ReflectionHelper.ClearAllTargets(__instance);
                return false;
            }

            var harvest = __instance.GetComponent<HarvestController>();
            bool gathering = harvest != null && harvest.IsInGatherMode;

            if (gathering)
            {
                // During gathering, check if any enemy is close enough to require self-defense
                var character = __instance.GetComponent<Character>();
                bool enemyNearby = false;
                Character nearestEnemy = null;
                float nearestDist = float.MaxValue;

                if (character != null)
                {
                    foreach (Character c in Character.GetAllCharacters())
                    {
                        if (c == character || c.IsDead()) continue;
                        // Belt-and-suspenders: also skip enemies at 0 hp (IsDead flag
                        // may lag behind actual death due to OnDeath animation callback)
                        if (c.GetHealth() <= 0f) continue;
                        if (!BaseAI.IsEnemy(character, c)) continue;
                        float d = Vector3.Distance(__instance.transform.position, c.transform.position);
                        if (d < SelfDefenseRange)
                        {
                            enemyNearby = true;
                            if (d < nearestDist)
                            {
                                nearestDist = d;
                                nearestEnemy = c;
                            }
                        }
                    }
                }

                if (enemyNearby)
                {
                    // Enemy nearby — let UpdateTarget run so companion can fight back
                    int id = __instance.GetInstanceID();
                    _logTimers.TryGetValue(id, out float timer);
                    timer -= Time.deltaTime;
                    if (timer <= 0f)
                    {
                        timer = 2f;
                        string name = character?.m_name ?? "?";
                        var currentTarget = ReflectionHelper.GetTargetCreature(__instance);
                        CompanionsPlugin.Log.LogInfo(
                            $"[TargetPatch|{name}] SELF-DEFENSE ALLOW — nearest enemy \"{nearestEnemy?.m_name ?? "?"}\" " +
                            $"at {nearestDist:F1}m — allowing targeting. currentTarget=\"{currentTarget?.m_name ?? "null"}\"");
                    }
                    _logTimers[id] = timer;
                    return true; // Let vanilla targeting run
                }
                else
                {
                    // No enemies nearby — suppress targeting as before
                    LogSuppression(__instance, "Gathering(safe)");
                    ReflectionHelper.ClearAllTargets(__instance);
                    // Clear alert state — m_alerted lingers after combat ends and causes
                    // MonsterAI.UpdateAI to override harvest movement with alert-scanning.
                    if (__instance.IsAlerted())
                        ReflectionHelper.SetAlerted(__instance, false);
                    return false;
                }
            }

            return true;
        }

        private static void LogSuppression(MonsterAI ai, string reason)
        {
            int id = ai.GetInstanceID();
            _logTimers.TryGetValue(id, out float timer);
            timer -= Time.deltaTime;
            if (timer <= 0f)
            {
                timer = 3f;
                var character = ai.GetComponent<Character>();
                string name = character?.m_name ?? "?";
                CompanionsPlugin.Log.LogInfo(
                    $"[TargetPatch|{name}] Suppressing UpdateTarget — reason={reason}");
            }
            _logTimers[id] = timer;
        }
    }

    /// <summary>
    /// Logs MonsterAI.UpdateAI decisions for companion debugging.
    /// Tracks when the AI chooses follow vs combat, and detects stuck states.
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
    internal static class UpdateAI_DebugLog
    {
        private static readonly Dictionary<int, float> _logTimers = new Dictionary<int, float>();
        private static readonly Dictionary<int, float> _stuckTimers = new Dictionary<int, float>();
        private static readonly Dictionary<int, Vector3> _lastPositions = new Dictionary<int, Vector3>();

        static void Postfix(MonsterAI __instance, float dt)
        {
            var setup = __instance.GetComponent<CompanionSetup>();
            if (setup == null) return;

            int id = __instance.GetInstanceID();

            // Stuck detection: log warning if companion hasn't moved
            _lastPositions.TryGetValue(id, out Vector3 lastPos);
            float moved = Vector3.Distance(__instance.transform.position, lastPos);
            _lastPositions[id] = __instance.transform.position;

            _stuckTimers.TryGetValue(id, out float stuckTime);
            var character = __instance.GetComponent<Character>();
            bool inAttack = character != null && character.InAttack();

            // Check if standing still is expected (not actually stuck)
            var follow = __instance.GetFollowTarget();
            float distToFollow = follow != null
                ? Vector3.Distance(__instance.transform.position, follow.transform.position) : -1f;
            bool atFollowDist = follow != null && distToFollow < 5f;

            // Also check if in harvest mode — HarvestController manages its own stuck detection
            var harvest = __instance.GetComponent<HarvestController>();
            bool harvesting = harvest != null && harvest.IsInGatherMode;

            if (moved < 0.1f && !inAttack && !atFollowDist && !harvesting)
                stuckTime += dt;
            else
                stuckTime = 0f;
            _stuckTimers[id] = stuckTime;

            if (stuckTime > 5f)
            {
                // Log every 2s while stuck
                _logTimers.TryGetValue(id, out float warnTimer);
                warnTimer -= dt;
                if (warnTimer <= 0f)
                {
                    warnTimer = 2f;
                    var target = ReflectionHelper.GetTargetCreature(__instance);
                    string name = character?.m_name ?? "?";
                    float distToTarget = target != null
                        ? Vector3.Distance(__instance.transform.position, target.transform.position) : -1f;

                    CompanionsPlugin.Log.LogWarning(
                        $"[AI|{name}] STUCK {stuckTime:F1}s — " +
                        $"target=\"{target?.m_name ?? "null"}\" targetDist={distToTarget:F1} " +
                        $"follow=\"{follow?.name ?? "null"}\" followDist={distToFollow:F1} " +
                        $"inAttack={inAttack} isAlerted={__instance.IsAlerted()} " +
                        $"charging={__instance.IsCharging()} " +
                        $"pos={__instance.transform.position:F1}");
                }
                _logTimers[id] = warnTimer;
            }

            // Periodic state dump (every 3s)
            _logTimers.TryGetValue(id, out float logTimer);
            // Use a different key suffix for periodic vs stuck logging
            int periodicKey = id + 1000000;
            _logTimers.TryGetValue(periodicKey, out float periodicTimer);
            periodicTimer -= dt;
            if (periodicTimer <= 0f)
            {
                periodicTimer = 3f;
                var target = ReflectionHelper.GetTargetCreature(__instance);
                // Reuse follow/distToFollow from stuck detection above
                string name = character?.m_name ?? "?";
                float distToTarget = target != null
                    ? Vector3.Distance(__instance.transform.position, target.transform.position) : -1f;
                var weapon = (__instance.GetComponent<Humanoid>())?.GetCurrentWeapon();
                var combat = __instance.GetComponent<CombatController>();

                CompanionsPlugin.Log.LogInfo(
                    $"[AI|{name}] target=\"{target?.m_name ?? "null"}\"({distToTarget:F1}) " +
                    $"follow=\"{follow?.name ?? "null"}\"({distToFollow:F1}) " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                    $"combat={combat?.Phase} " +
                    $"alerted={__instance.IsAlerted()} " +
                    $"vel={character?.GetVelocity().magnitude ?? 0f:F1} " +
                    $"moved={moved:F2}");
            }
            _logTimers[periodicKey] = periodicTimer;
        }
    }
}
