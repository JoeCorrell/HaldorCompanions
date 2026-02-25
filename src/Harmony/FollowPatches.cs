using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Zone-based following with hysteresis (Inner/Comfort/CatchUp/Sprint),
    /// teleport at 40m, waypoint following for harvest, and MoveTo leash enforcement.
    /// Uses ReflectionHelper.TryMoveTo instead of raw reflection.
    /// </summary>
    public static class FollowPatches
    {
        private const float TeleportDistance      = 40f;
        private const float ComfortHoldDistance   = 2.7f;
        private const float ComfortHoldPlayerSpeed = 0.75f;
        private const float CatchUpStuckMinDist   = 4.5f;
        private const float HarvestGraceLeash     = CompanionSetup.MaxLeashDistance + 5f;

        private static bool IsBeyondLeash(Vector3 position, Player player)
        {
            if (player == null) return false;
            return Vector3.Distance(position, player.transform.position)
                   > CompanionSetup.MaxLeashDistance;
        }

        private static float GetPlanarSpeed(Character character)
        {
            if (character == null) return 0f;
            var velocity = character.GetVelocity();
            velocity.y = 0f;
            return velocity.magnitude;
        }

        private static Vector3 GetFollowAnchor(
            Player player, BaseAI ai, CompanionBrain.FollowZone zone)
        {
            Vector3 playerPos = player.transform.position;
            Vector3 forward = player.transform.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 0.01f)
            {
                forward = player.GetMoveDir();
                forward.y = 0f;
            }
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            float sideSign = ((ai.GetInstanceID() & 1) == 0) ? 1f : -1f;

            float backOffset, sideOffset;
            switch (zone)
            {
                case CompanionBrain.FollowZone.Sprint:
                    backOffset = 2.3f; sideOffset = 0.4f; break;
                case CompanionBrain.FollowZone.CatchUp:
                    backOffset = 2.0f; sideOffset = 0.6f; break;
                default:
                    backOffset = 1.7f; sideOffset = 0.8f; break;
            }

            Vector3 target = playerPos - forward * backOffset + right * (sideOffset * sideSign);
            target.y = playerPos.y;
            return target;
        }

        private static CompanionBrain.FollowZone DetermineFollowZone(
            float dist, CompanionBrain.FollowZone current)
        {
            switch (current)
            {
                case CompanionBrain.FollowZone.Inner:
                    return dist > 2.2f ? CompanionBrain.FollowZone.Comfort
                                       : CompanionBrain.FollowZone.Inner;
                case CompanionBrain.FollowZone.Comfort:
                    if (dist < 1.5f) return CompanionBrain.FollowZone.Inner;
                    if (dist > 4.0f) return CompanionBrain.FollowZone.CatchUp;
                    return CompanionBrain.FollowZone.Comfort;
                case CompanionBrain.FollowZone.CatchUp:
                    if (dist < 3.0f) return CompanionBrain.FollowZone.Comfort;
                    if (dist > 10.0f) return CompanionBrain.FollowZone.Sprint;
                    return CompanionBrain.FollowZone.CatchUp;
                case CompanionBrain.FollowZone.Sprint:
                    return dist < 8.0f ? CompanionBrain.FollowZone.CatchUp
                                       : CompanionBrain.FollowZone.Sprint;
                default:
                    if (dist < 1.5f) return CompanionBrain.FollowZone.Inner;
                    if (dist < 4.0f) return CompanionBrain.FollowZone.Comfort;
                    if (dist < 10.0f) return CompanionBrain.FollowZone.CatchUp;
                    return CompanionBrain.FollowZone.Sprint;
            }
        }

        private static Vector3 FindSafeTeleportPosition(GameObject playerGo)
        {
            Vector3 behind = playerGo.transform.position + playerGo.transform.forward * -1.5f;
            if (ZoneSystem.instance != null)
            {
                float groundHeight;
                if (ZoneSystem.instance.GetSolidHeight(behind, out groundHeight))
                {
                    behind.y = groundHeight + 0.1f;
                    return behind;
                }
            }
            behind.y = playerGo.transform.position.y + 0.5f;
            return behind;
        }

        private static void ForceReturnToPlayer(BaseAI ai, Player player, float dt, bool moveNow)
        {
            if (ai == null || player == null) return;

            if (ai is MonsterAI monsterAI)
            {
                monsterAI.SetFollowTarget(player.gameObject);
                ReflectionHelper.ClearAllTargets(monsterAI);
                ReflectionHelper.TrySetLastKnownTargetPos(monsterAI, player.transform.position);
                ReflectionHelper.TrySetBeenAtLastPos(monsterAI, false);
            }

            ai.GetComponent<HarvestController>()?.AbortForLeash(player.gameObject);

            var stamina = ai.GetComponent<CompanionStamina>();
            if (stamina != null) stamina.IsRunning = moveNow;

            if (moveNow)
            {
                if (!ReflectionHelper.TryMoveTo(ai, dt, player.transform.position, 0f, true))
                    ai.StopMoving();
            }
            else
            {
                ai.StopMoving();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Follow patch — zone-based following + stuck + teleport
        // ══════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BaseAI), "Follow")]
        private static class Follow_Patch
        {
            static bool Prefix(BaseAI __instance, GameObject go, float dt)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var stamina = __instance.GetComponent<CompanionStamina>();
                if (go == null)
                {
                    if (stamina != null) stamina.IsRunning = false;
                    return true;
                }

                var player = go.GetComponent<Player>();
                var localPlayer = Player.m_localPlayer;
                var brain = __instance.GetComponent<CompanionBrain>();

                // Leash check for non-player follow targets (waypoints)
                if (localPlayer != null && player == null &&
                    (IsBeyondLeash(go.transform.position, localPlayer) ||
                     IsBeyondLeash(__instance.transform.position, localPlayer)))
                {
                    ForceReturnToPlayer(__instance, localPlayer, dt, moveNow: true);
                    return false;
                }

                float dist = Vector3.Distance(
                    go.transform.position, __instance.transform.position);

                if (player != null)
                    return HandlePlayerFollow(
                        __instance, brain, stamina, player, go, dist, dt);
                else
                    return HandleWaypointFollow(
                        __instance, brain, stamina, go, dist, dt);
            }

            private static bool HandlePlayerFollow(
                BaseAI ai, CompanionBrain brain, CompanionStamina stamina,
                Player player, GameObject go, float dist, float dt)
            {
                if (dist > TeleportDistance)
                {
                    Vector3 tp = FindSafeTeleportPosition(go);
                    ai.transform.position = tp;
                    brain?.OnTeleported();
                    if (stamina != null) stamina.IsRunning = false;
                    return false;
                }

                var currentZone = brain != null
                    ? brain.CurrentFollowZone
                    : CompanionBrain.FollowZone.Comfort;
                var zone = DetermineFollowZone(dist, currentZone);
                if (brain != null) brain.CurrentFollowZone = zone;
                float playerSpeed = GetPlanarSpeed(player);

                if (zone == CompanionBrain.FollowZone.Inner)
                {
                    ai.StopMoving();
                    if (stamina != null) stamina.IsRunning = false;
                    brain?.FollowStuck?.Reset();
                    return false;
                }

                if (zone == CompanionBrain.FollowZone.Comfort &&
                    dist <= ComfortHoldDistance &&
                    playerSpeed <= ComfortHoldPlayerSpeed)
                {
                    ai.StopMoving();
                    if (stamina != null) stamina.IsRunning = false;
                    brain?.FollowStuck?.Reset();
                    return false;
                }

                // Velocity matching: blend between walk and run based on player speed + distance
                bool canRun = stamina == null || stamina.Stamina > 0f;
                bool encumbered = brain?.Encumbrance?.IsEncumbered == true;
                bool wantRun;
                switch (zone)
                {
                    case CompanionBrain.FollowZone.Comfort:
                        // Match player: run only if player is running AND moving away
                        wantRun = player.IsRunning() && playerSpeed > 2f;
                        break;
                    case CompanionBrain.FollowZone.CatchUp:
                        // Run if player is running OR if gap is growing
                        wantRun = player.IsRunning() || dist > 6f;
                        break;
                    case CompanionBrain.FollowZone.Sprint:
                        wantRun = true;
                        break;
                    default:
                        wantRun = false;
                        break;
                }
                bool run = wantRun && canRun && !encumbered;
                if (stamina != null) stamina.IsRunning = run;

                // Anchor-based following: stay behind and to the side of player
                Vector3 moveTarget;
                if (zone == CompanionBrain.FollowZone.Sprint && dist > 12f)
                    moveTarget = go.transform.position; // direct pursuit when far
                else
                    moveTarget = GetFollowAnchor(player, ai, zone);

                if (!ReflectionHelper.TryMoveTo(ai, dt, moveTarget, 0f, run))
                {
                    if (stamina != null) stamina.IsRunning = false;
                    return true;
                }

                if (brain?.FollowStuck != null)
                {
                    bool shouldCheckStuck =
                        (zone == CompanionBrain.FollowZone.CatchUp ||
                         zone == CompanionBrain.FollowZone.Sprint) &&
                        dist >= CatchUpStuckMinDist;

                    if (shouldCheckStuck)
                    {
                        var stuckAction = brain.FollowStuck.Update(dt, dist);
                        if (stuckAction == FollowStuckEscalation.StuckAction.Teleport)
                        {
                            Vector3 tp = FindSafeTeleportPosition(go);
                            ai.transform.position = tp;
                            brain.OnTeleported();
                            if (stamina != null) stamina.IsRunning = false;
                        }
                    }
                    else
                    {
                        brain.FollowStuck.Reset();
                    }
                }

                return false;
            }

            private static bool HandleWaypointFollow(
                BaseAI ai, CompanionBrain brain, CompanionStamina stamina,
                GameObject go, float dist, float dt)
            {
                // Stop distance 1.5f prevents oscillation: MoveTo internally stops
                // when within max(dist, runThreshold), so 1.5f gives a stable arrival
                // zone. HarvestController checks its own attack range for actual
                // in-range detection, so this just needs to get us "close enough".
                const float WaypointStopDist = 1.5f;

                if (dist <= WaypointStopDist)
                {
                    ai.StopMoving();
                    if (stamina != null) stamina.IsRunning = false;
                    return false;
                }

                bool wantRun = dist > 4f;
                bool canRun = stamina == null || stamina.Stamina > 0f;
                bool encumbered = brain?.Encumbrance?.IsEncumbered == true;
                bool run = wantRun && canRun && !encumbered;
                if (stamina != null) stamina.IsRunning = run;

                if (!ReflectionHelper.TryMoveTo(ai, dt, go.transform.position, WaypointStopDist, run))
                {
                    if (stamina != null) stamina.IsRunning = false;
                    return true;
                }
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  MoveTo leash — prevent pathfinding beyond leash distance
        // ══════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BaseAI), "MoveTo",
            new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) })]
        private static class MoveTo_LeashPatch
        {
            static bool Prefix(BaseAI __instance, float dt, Vector3 point, ref bool __result)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var player = Player.m_localPlayer;
                if (player == null) return true;

                var harvest = __instance.GetComponent<HarvestController>();
                if (harvest != null && harvest.IsCollectingDrops)
                {
                    float playerDist = Vector3.Distance(
                        __instance.transform.position, player.transform.position);
                    if (playerDist <= HarvestGraceLeash) return true;
                }

                if (!IsBeyondLeash(point, player)) return true;

                ForceReturnToPlayer(__instance, player, dt, moveNow: false);
                __result = false;
                return false;
            }
        }
    }
}
