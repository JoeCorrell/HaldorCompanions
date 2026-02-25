using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Harmony patches for companion AI:
    /// - Zone-based follow with hysteresis (Inner/Comfort/CatchUp/Sprint)
    /// - Safe teleport at 40m using ground height sampling
    /// - Stuck escalation via CompanionAI.UpdateFollowStuck
    /// - Leash enforcement with harvest grace periods
    /// - Combat/harvest target coordination
    /// - Bridge Character.UseStamina/HaveStamina to CompanionStamina
    /// - Override GetMaxHealth for food-based health (base 25 + food bonuses)
    /// - Perfect parry timing for blocking
    /// </summary>
    public static class CompanionAIPatches
    {
        // ── Reflection for protected methods/fields ───────────────────────────
        private static readonly MethodInfo _moveToMethod =
            AccessTools.Method(typeof(BaseAI), "MoveTo",
                new[] { typeof(float), typeof(Vector3), typeof(float), typeof(bool) });
        private static readonly FieldInfo _monsterTargetCreatureField =
            AccessTools.Field(typeof(MonsterAI), "m_targetCreature");
        private static readonly FieldInfo _monsterTargetStaticField =
            AccessTools.Field(typeof(MonsterAI), "m_targetStatic");
        private static readonly FieldInfo _monsterLastKnownTargetPosField =
            AccessTools.Field(typeof(MonsterAI), "m_lastKnownTargetPos");
        private static readonly FieldInfo _monsterBeenAtLastPosField =
            AccessTools.Field(typeof(MonsterAI), "m_beenAtLastPos");

        // ── Follow zone thresholds ────────────────────────────────────────────
        private const float TeleportDistance = 40f;
        private const float HarvestGraceLeash = CompanionSetup.MaxLeashDistance + 5f;
        private const float HarvestEnemyOverrideRange = 15f;

        private static bool IsBeyondLeash(Vector3 position, Player player)
        {
            if (player == null) return false;
            return Vector3.Distance(position, player.transform.position) > CompanionSetup.MaxLeashDistance;
        }

        private static void ForceReturnToPlayer(BaseAI ai, Player player, float dt, bool moveNow)
        {
            if (ai == null || player == null) return;

            if (ai is MonsterAI monsterAI)
            {
                monsterAI.SetFollowTarget(player.gameObject);
                _monsterTargetCreatureField?.SetValue(monsterAI, null);
                _monsterTargetStaticField?.SetValue(monsterAI, null);
                _monsterLastKnownTargetPosField?.SetValue(monsterAI, player.transform.position);
                _monsterBeenAtLastPosField?.SetValue(monsterAI, false);
            }

            ai.GetComponent<CompanionHarvest>()?.AbortForLeash(player.gameObject);

            var stamina = ai.GetComponent<CompanionStamina>();
            if (stamina != null) stamina.IsRunning = moveNow;

            if (moveNow)
            {
                _moveToMethod?.Invoke(ai,
                    new object[] { dt, player.transform.position, 0f, true });
            }
            else
            {
                ai.StopMoving();
            }
        }

        // ── Zone determination with hysteresis ────────────────────────────────

        private static CompanionAI.FollowZone DetermineFollowZone(
            float dist, CompanionAI.FollowZone current)
        {
            switch (current)
            {
                case CompanionAI.FollowZone.Inner:
                    if (dist > 2.2f) return CompanionAI.FollowZone.Comfort;
                    return CompanionAI.FollowZone.Inner;

                case CompanionAI.FollowZone.Comfort:
                    if (dist < 1.5f) return CompanionAI.FollowZone.Inner;
                    if (dist > 4.0f) return CompanionAI.FollowZone.CatchUp;
                    return CompanionAI.FollowZone.Comfort;

                case CompanionAI.FollowZone.CatchUp:
                    if (dist < 3.0f) return CompanionAI.FollowZone.Comfort;
                    if (dist > 10.0f) return CompanionAI.FollowZone.Sprint;
                    return CompanionAI.FollowZone.CatchUp;

                case CompanionAI.FollowZone.Sprint:
                    if (dist < 8.0f) return CompanionAI.FollowZone.CatchUp;
                    return CompanionAI.FollowZone.Sprint;

                default:
                    if (dist < 1.5f) return CompanionAI.FollowZone.Inner;
                    if (dist < 4.0f) return CompanionAI.FollowZone.Comfort;
                    if (dist < 10.0f) return CompanionAI.FollowZone.CatchUp;
                    return CompanionAI.FollowZone.Sprint;
            }
        }

        // ── Safe teleport ─────────────────────────────────────────────────────

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

            // Fallback: use player height
            behind.y = playerGo.transform.position.y + 0.5f;
            return behind;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Follow patch — zone-based following + stuck recovery + teleport
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(BaseAI), "Follow")]
        private static class Follow_Patch
        {
            static bool Prefix(BaseAI __instance, GameObject go, float dt)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null)
                    return true; // not a companion — use vanilla

                var stamina = __instance.GetComponent<CompanionStamina>();
                if (go == null)
                {
                    if (stamina != null) stamina.IsRunning = false;
                    return true;
                }

                var player = go.GetComponent<Player>();
                var localPlayer = Player.m_localPlayer;
                var companionAI = __instance.GetComponent<CompanionAI>();

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
                        __instance, companionAI, stamina, player, go, dist, dt);
                else
                    return HandleWaypointFollow(
                        __instance, companionAI, stamina, go, dist, dt);
            }

            // ── Player following (zone-based) ─────────────────────────────────

            private static bool HandlePlayerFollow(
                BaseAI ai, CompanionAI companionAI, CompanionStamina stamina,
                Player player, GameObject go, float dist, float dt)
            {
                // Teleport if far from player
                if (dist > TeleportDistance)
                {
                    Vector3 tp = FindSafeTeleportPosition(go);
                    ai.transform.position = tp;
                    if (companionAI != null) companionAI.OnTeleported();
                    if (stamina != null) stamina.IsRunning = false;
                    return false;
                }

                // Determine follow zone with hysteresis
                var currentZone = companionAI != null
                    ? companionAI.CurrentFollowZone
                    : CompanionAI.FollowZone.Comfort;
                var zone = DetermineFollowZone(dist, currentZone);
                if (companionAI != null) companionAI.CurrentFollowZone = zone;

                // Inner zone: stop and idle
                if (zone == CompanionAI.FollowZone.Inner)
                {
                    ai.StopMoving();
                    if (stamina != null) stamina.IsRunning = false;
                    return false;
                }

                // Determine run state based on zone
                bool wantRun;
                switch (zone)
                {
                    case CompanionAI.FollowZone.Comfort:
                        wantRun = false;
                        break;
                    case CompanionAI.FollowZone.CatchUp:
                        wantRun = player.IsRunning();
                        break;
                    case CompanionAI.FollowZone.Sprint:
                        wantRun = true;
                        break;
                    default:
                        wantRun = false;
                        break;
                }

                bool canRun = stamina == null || stamina.Stamina > 0f;
                bool encumbered = companionAI != null && companionAI.IsEncumbered;
                bool run = wantRun && canRun && !encumbered;

                if (stamina != null) stamina.IsRunning = run;

                // Move toward player
                _moveToMethod?.Invoke(ai,
                    new object[] { dt, go.transform.position, 0f, run });

                // Stuck detection (only when not in Inner zone)
                if (companionAI != null)
                {
                    var stuckAction = companionAI.UpdateFollowStuck(dt, dist);
                    if (stuckAction == CompanionAI.StuckAction.Teleport)
                    {
                        Vector3 tp = FindSafeTeleportPosition(go);
                        ai.transform.position = tp;
                        companionAI.OnTeleported();
                        if (stamina != null) stamina.IsRunning = false;
                    }
                }

                return false;
            }

            // ── Waypoint following (harvest targets) ──────────────────────────

            private static bool HandleWaypointFollow(
                BaseAI ai, CompanionAI companionAI, CompanionStamina stamina,
                GameObject go, float dist, float dt)
            {
                // No stop distance — navigate all the way to the waypoint.
                // Run if > 3m away, walk otherwise.
                bool wantRun = dist > 3f;
                bool canRun = stamina == null || stamina.Stamina > 0f;
                bool encumbered = companionAI != null && companionAI.IsEncumbered;
                bool run = wantRun && canRun && !encumbered;

                if (stamina != null) stamina.IsRunning = run;

                _moveToMethod?.Invoke(ai,
                    new object[] { dt, go.transform.position, 0f, run });

                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  MoveTo leash — prevent companion from pathfinding beyond leash
        // ══════════════════════════════════════════════════════════════════════

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

                // Harvest grace: allow drops collection within extended leash
                var harvest = __instance.GetComponent<CompanionHarvest>();
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

        // ══════════════════════════════════════════════════════════════════════
        //  UpdateTarget leash + harvest/combat coordination
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(MonsterAI), "UpdateTarget")]
        private static class UpdateTarget_LeashPatch
        {
            static void Postfix(MonsterAI __instance, float dt)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;

                var player = Player.m_localPlayer;
                if (player == null) return;

                bool outOfRange = IsBeyondLeash(__instance.transform.position, player);

                if (!outOfRange)
                {
                    var targetCreature = _monsterTargetCreatureField?.GetValue(__instance) as Character;
                    if (targetCreature != null && targetCreature)
                        outOfRange = IsBeyondLeash(targetCreature.transform.position, player);
                }

                if (!outOfRange)
                {
                    var targetStatic = _monsterTargetStaticField?.GetValue(__instance) as StaticTarget;
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
                {
                    ForceReturnToPlayer(__instance, player, dt, moveNow: false);
                    return;
                }

                // Harvest coordination: don't let distant combat targets override active gathering
                var harvest = __instance.GetComponent<CompanionHarvest>();
                if (harvest != null && harvest.IsActivelyHarvesting)
                {
                    var targetCreature = _monsterTargetCreatureField?.GetValue(__instance) as Character;
                    if (targetCreature != null && targetCreature)
                    {
                        float enemyDist = Vector3.Distance(
                            __instance.transform.position, targetCreature.transform.position);
                        if (enemyDist > HarvestEnemyOverrideRange)
                        {
                            // Clear distant combat target — let harvesting continue
                            _monsterTargetCreatureField.SetValue(__instance, null);
                        }
                    }
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stamina bridge — route Character.UseStamina to CompanionStamina
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Character), nameof(Character.UseStamina))]
        private static class UseStamina_Patch
        {
            static bool Prefix(Character __instance, float stamina)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true; // not a companion

                cs.Drain(stamina);
                return false; // skip original no-op
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Stamina bridge — route Character.HaveStamina to CompanionStamina
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Character), nameof(Character.HaveStamina))]
        private static class HaveStamina_Patch
        {
            static bool Prefix(Character __instance, float amount, ref bool __result)
            {
                var cs = __instance.GetComponent<CompanionStamina>();
                if (cs == null) return true; // not a companion

                __result = cs.Stamina > amount;
                return false; // skip original always-true
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Perfect parry — force m_blockTimer = 0 for companions
        // ══════════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Humanoid), "BlockAttack")]
        private static class BlockAttack_Patch
        {
            private static readonly FieldInfo _blockTimerField =
                AccessTools.Field(typeof(Humanoid), "m_blockTimer");

            static void Prefix(Humanoid __instance)
            {
                if (__instance.GetComponent<CompanionSetup>() == null) return;
                if (_blockTimerField == null) return;

                float timer = (float)_blockTimerField.GetValue(__instance);
                // Only force parry if currently blocking (timer >= 0)
                if (timer >= 0f)
                    _blockTimerField.SetValue(__instance, 0f);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Health — food-based max health for companions
        // ══════════════════════════════════════════════════════════════════════

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
