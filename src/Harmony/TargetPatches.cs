using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// UpdateTarget coordination: leash enforcement, harvest suppression.
    /// During active harvesting, PREVENTS MonsterAI from acquiring new combat targets
    /// unless an enemy is very close (within 8m). This stops the companion from
    /// abandoning harvest to chase distant enemies.
    /// </summary>
    public static class TargetPatches
    {
        private const float HarvestEnemyOverrideRange = 8f;

        private static bool IsBeyondLeash(Vector3 position, Player player)
        {
            if (player == null) return false;
            return Vector3.Distance(position, player.transform.position)
                   > CompanionSetup.MaxLeashDistance;
        }

        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        private static class UpdateTarget_Patch
        {
            /// <summary>
            /// PREFIX: During active harvest, skip MonsterAI's target acquisition entirely.
            /// MonsterAI.UpdateTarget runs every 2s and calls FindEnemy() which sets
            /// m_targetCreature. This overrides the harvest follow target and causes the
            /// companion to stop gathering. We suppress this unless an enemy is very close.
            /// </summary>
            static bool Prefix(MonsterAI __instance)
            {
                var harvest = __instance.GetComponent<HarvestController>();
                if (harvest == null || !harvest.IsActivelyHarvesting) return true;

                // Allow UpdateTarget if a close enemy is threatening
                var brain = __instance.GetComponent<CompanionBrain>();
                if (brain != null && brain.Enemies != null &&
                    brain.Enemies.NearestEnemy != null &&
                    brain.Enemies.NearestEnemyDist < HarvestEnemyOverrideRange)
                    return true; // Let MonsterAI acquire the close threat

                // Suppress target acquisition — clear any stale targets
                ReflectionHelper.ClearAllTargets(__instance);
                return false; // Skip original UpdateTarget
            }

            /// <summary>
            /// POSTFIX: Leash enforcement for all companions (runs even when prefix skips).
            /// </summary>
            static void Postfix(MonsterAI __instance, float dt)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                var player = Player.m_localPlayer;
                if (player == null) return;

                bool outOfRange = IsBeyondLeash(__instance.transform.position, player);

                if (!outOfRange)
                {
                    var targetCreature = ReflectionHelper.GetTargetCreature(__instance);
                    if (targetCreature != null && targetCreature)
                        outOfRange = IsBeyondLeash(targetCreature.transform.position, player);
                }

                if (!outOfRange)
                {
                    var targetStatic = ReflectionHelper.GetTargetStatic(__instance);
                    if (targetStatic != null && targetStatic)
                        outOfRange = IsBeyondLeash(targetStatic.transform.position, player);
                }

                if (!outOfRange)
                {
                    var followTarget = __instance.GetFollowTarget();
                    if (followTarget != null && !followTarget.GetComponent<Player>())
                        outOfRange = IsBeyondLeash(followTarget.transform.position, player);
                }

                if (outOfRange)
                    ForceReturnToPlayer(__instance, player, dt);
            }

            private static void ForceReturnToPlayer(MonsterAI ai, Player player, float dt)
            {
                ai.SetFollowTarget(player.gameObject);
                ReflectionHelper.ClearAllTargets(ai);
                ReflectionHelper.TrySetLastKnownTargetPos(ai, player.transform.position);
                ReflectionHelper.TrySetBeenAtLastPos(ai, false);

                ai.GetComponent<HarvestController>()?.AbortForLeash(player.gameObject);

                var stamina = ai.GetComponent<CompanionStamina>();
                if (stamina != null) stamina.IsRunning = false;
                ai.StopMoving();
            }
        }
    }
}
