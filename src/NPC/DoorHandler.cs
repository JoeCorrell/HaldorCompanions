using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Detects nearby doors and opens/closes them for the companion.
    /// Two detection modes:
    ///   1. Stuck detection — companion vel≈0 near a closed door → open it
    ///   2. Proactive detection — companion is moving but distance to player isn't
    ///      decreasing (circling a building) → find nearest door to the player
    /// </summary>
    public class DoorHandler : MonoBehaviour
    {
        private enum Phase { Idle, Approaching, WaitingForOpen, PassingThrough, Closing }

        // ── Tuning ──────────────────────────────────────────────────────────
        private const float ScanInterval      = 0.5f;
        private const float ScanRadius        = 5.0f;   // stuck-mode scan radius
        private const float StuckThreshold    = 1.0f;   // faster stuck detection (was 1.5)
        private const float StuckMoveDist     = 0.2f;   // more sensitive movement check (was 0.3)
        private const float StuckCheckInterval = 0.5f;   // check twice per second (was 1.0)
        private const float FollowDistMin     = 4.0f;   // engage when 4m+ from target (was 5)
        private const float InteractDist      = 2.0f;   // interact range
        private const float OpenWaitTime      = 1.2f;   // wait for door animation
        private const float CloseDistance      = 3.5f;   // close door when this far past it
        private const float PassTimeout       = 8.0f;   // safety timeout in PassingThrough
        private const float ProximityCheck    = 2.5f;   // don't close if player/companion near
        private const float ApproachTimeout   = 10.0f;  // give up approaching if takes too long
        private const float HeartbeatInterval = 2.0f;

        // ── Proactive detection tuning ─────────────────────────────────────
        private const float ProactiveCheckInterval = 1.0f;  // measure progress every 1s
        private const float ProactiveStagnationTime = 3.0f; // trigger after 3s of no progress
        private const float ProactiveScanRadius   = 15.0f;  // wider scan for proactive mode
        private const float MinProactiveSpeed     = 0.5f;   // must be actively moving
        private const float MinProgressRate       = 0.3f;   // must close 0.3m per check to count

        /// <summary>True when DoorHandler needs exclusive movement control.
        /// Covers Approaching, WaitingForOpen, and PassingThrough. The navmesh is
        /// static and doesn't update for open doors, so pathfinding-based Follow()
        /// can't move through doorways — DoorHandler drives movement directly via
        /// MoveTowards instead.</summary>
        public bool IsActive => _phase != Phase.Idle && _phase != Phase.Closing;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI       _ai;
        private Humanoid          _humanoid;
        private CompanionSetup    _setup;
        private ZNetView          _nview;
        private HarvestController _harvest;
        private RepairController  _repair;
        private CompanionRest     _rest;

        // ── State ───────────────────────────────────────────────────────────
        private Phase   _phase;
        private Door    _targetDoor;
        private Vector3 _doorPos;
        private float   _scanTimer;
        private float   _stuckTimer;
        private float   _stuckCheckTimer;
        private Vector3 _lastStuckPos;
        private float   _waitTimer;
        private float   _passTimer;
        private float   _approachTimer;
        private float   _heartbeatTimer;
        private int     _scanAttempts;      // how many scans found no doors
        private float   _cooldownTimer;     // prevents re-detecting same door immediately after closing
        private const float PostCloseCooldown = 4f;

        // ── Proactive detection state ──────────────────────────────────────
        private float   _proactiveCheckTimer;      // interval between progress checks
        private float   _proactiveStagnationTimer;  // accumulated time with no progress
        private float   _lastFollowDist;            // distance to player at last check
        private bool    _lastFollowDistValid;       // first check has no baseline

        private readonly Collider[] _proximityBuffer = new Collider[16];

        // ── Door cache (avoids FindObjectsByType every scan) ────────────
        private static Door[] _doorCache;
        private static float  _doorCacheTimer;
        private const float   DoorCacheInterval = 5f;

        private static Door[] GetDoorCache()
        {
            if (_doorCache == null || _doorCacheTimer <= 0f)
            {
                _doorCache = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
                _doorCacheTimer = DoorCacheInterval;
            }
            return _doorCache;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _ai       = GetComponent<CompanionAI>();
            _humanoid = GetComponent<Humanoid>();
            _setup    = GetComponent<CompanionSetup>();
            _nview    = GetComponent<ZNetView>();
            _harvest  = GetComponent<HarvestController>();
            _repair   = GetComponent<RepairController>();
            _rest     = GetComponent<CompanionRest>();
            CompanionsPlugin.Log.LogDebug("[DoorHandler] Initialized — door handling active");
        }

        private void Update()
        {
            if (_ai == null || _humanoid == null || _setup == null) return;
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;

            // Skip in Stay mode — not following player
            int mode = _nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode == CompanionSetup.ModeStay) return;

            float dt = Time.deltaTime;
            _doorCacheTimer -= dt;

            // Heartbeat logging
            _heartbeatTimer -= dt;
            if (_heartbeatTimer <= 0f && _phase != Phase.Idle)
            {
                _heartbeatTimer = HeartbeatInterval;
                var followTarget = _ai.GetFollowTarget();
                float distToFollow = followTarget != null
                    ? Vector3.Distance(transform.position, followTarget.transform.position) : -1f;
                float distToDoor = _targetDoor != null
                    ? Vector3.Distance(transform.position, _doorPos) : -1f;
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] ♥ phase={_phase} " +
                    $"door=\"{(_targetDoor != null ? _targetDoor.m_name : "null")}\" " +
                    $"doorDist={distToDoor:F1} followDist={distToFollow:F1} " +
                    $"pos={transform.position:F1} doorPos={_doorPos:F1}");
            }

            switch (_phase)
            {
                case Phase.Idle:           UpdateIdle(dt); break;
                case Phase.Approaching:    UpdateApproaching(dt); break;
                case Phase.WaitingForOpen: UpdateWaitingForOpen(dt); break;
                case Phase.PassingThrough: UpdatePassingThrough(dt); break;
                case Phase.Closing:        UpdateClosing(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API — directed door interaction from hotkey
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Direct the companion to open a specific door (from hotkey command).
        /// Skips stuck/proactive detection and goes straight to Approaching.
        /// </summary>
        public void DirectOpenDoor(Door door)
        {
            if (door == null) return;
            if (!IsValidClosedDoor(door))
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] DirectOpenDoor — door \"{door.m_name}\" is not a valid closed door");
                return;
            }

            _targetDoor = door;
            _doorPos = door.transform.position;
            _phase = Phase.Approaching;
            _approachTimer = 0f;
            _stuckTimer = 0f;
            _cooldownTimer = 0f;
            ResetProactive();
            CompanionsPlugin.Log.LogDebug(
                $"[DoorHandler] DirectOpenDoor — approaching \"{door.m_name}\" " +
                $"dist={Vector3.Distance(transform.position, _doorPos):F1}m");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: Idle — detect doors via stuck detection OR proactive detection
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdle(float dt)
        {
            // Cooldown after completing a door cycle — prevents re-detecting the
            // same door immediately after closing it, before the companion has
            // had time to move away.
            if (_cooldownTimer > 0f)
            {
                _cooldownTimer -= dt;
                _stuckTimer = 0f;
                ResetProactive();
                return;
            }

            // Sleeping/sitting companions are stationary by design — skip entirely
            if (_rest != null && _rest.IsResting)
            {
                _stuckTimer = 0f;
                _stuckCheckTimer = 0f;
                ResetProactive();
                return;
            }

            // When a controller is actively working at a station (repairing, harvesting
            // an attack), the companion is intentionally stationary. Don't let stuck
            // detection accumulate — otherwise DoorHandler fires the instant the
            // controller finishes and hijacks the companion before the next repair scan.
            // Exception: repair MovingToStation — companion IS trying to move and
            // might genuinely be stuck behind a door.
            bool controllerStationary =
                (_harvest != null && _harvest.IsActive && !_harvest.IsMoving) ||
                (_repair  != null && _repair.IsActive  && _repair.Phase != RepairController.RepairPhase.MovingToStation);

            if (controllerStationary)
            {
                _stuckTimer = 0f;
                _stuckCheckTimer = 0f;
                ResetProactive();
                return;
            }

            // The companion needs a reason to be moving for stuck detection to matter.
            // In follow mode: must be 4m+ from player (otherwise it's resting normally).
            // In harvest/repair mode: the controller is trying to reach a target, so
            // being stuck (vel~0) for any reason — including a blocked door — matters.
            // If follow is null but companion should be following (mode=Follow/Gather),
            // still allow stuck detection — companion might be trapped after UI close
            // or zone reload.
            bool controllerMoving = (_harvest != null && _harvest.IsActive) ||
                                    (_repair  != null && _repair.IsActive);

            GameObject followTarget = null;
            float distToTarget = 0f;

            if (!controllerMoving)
            {
                followTarget = _ai?.GetFollowTarget();
                if (followTarget != null)
                {
                    distToTarget = Vector3.Distance(transform.position, followTarget.transform.position);
                    if (distToTarget < FollowDistMin)
                    {
                        _stuckTimer = 0f;
                        _scanAttempts = 0;
                        ResetProactive();
                        return;
                    }
                }
                else
                {
                    // No follow target — still allow stuck detection for companions that
                    // SHOULD be following but lost their target (e.g. after UI close, zone reload).
                    // StayHome companions are intentionally stationary at home — skip them.
                    if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition())
                        return;
                }
            }

            // ── Proactive door detection ──
            // Detects when companion is actively moving (following navmesh around a
            // building) but not actually getting closer to the player. After 3s of
            // stagnation, scan a wide radius for doors scored by proximity to the player.
            if (!controllerMoving && followTarget != null)
            {
                var vel = _humanoid?.GetVelocity() ?? Vector3.zero;
                float horizSpeed = new Vector3(vel.x, 0f, vel.z).magnitude;

                if (horizSpeed > MinProactiveSpeed)
                {
                    _proactiveCheckTimer += dt;
                    if (_proactiveCheckTimer >= ProactiveCheckInterval)
                    {
                        _proactiveCheckTimer = 0f;

                        if (_lastFollowDistValid)
                        {
                            float progress = _lastFollowDist - distToTarget;
                            if (progress < MinProgressRate)
                                _proactiveStagnationTimer += ProactiveCheckInterval;
                            else
                                _proactiveStagnationTimer = Mathf.Max(0f,
                                    _proactiveStagnationTimer - ProactiveCheckInterval);
                        }

                        _lastFollowDist = distToTarget;
                        _lastFollowDistValid = true;
                    }

                    if (_proactiveStagnationTimer >= ProactiveStagnationTime)
                    {
                        ResetProactive();
                        ScanForDoorProactive(followTarget.transform.position);
                        return;
                    }
                }
            }
            else if (!controllerMoving)
            {
                ResetProactive();
            }

            // ── Stuck detection (original fallback) ──
            // Companion vel≈0 for 1s near a door → scan close radius.
            _stuckCheckTimer += dt;
            if (_stuckCheckTimer >= StuckCheckInterval)
            {
                float moved = Vector3.Distance(transform.position, _lastStuckPos);
                _lastStuckPos = transform.position;
                _stuckCheckTimer = 0f;

                if (moved < StuckMoveDist)
                    _stuckTimer += StuckCheckInterval;
                else
                    _stuckTimer = 0f;
            }

            if (_stuckTimer < StuckThreshold) return;

            // Throttle scanning
            _scanTimer += dt;
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            ScanForDoor();
        }

        private void ResetProactive()
        {
            _proactiveCheckTimer = 0f;
            _proactiveStagnationTimer = 0f;
            _lastFollowDistValid = false;
        }

        private void ScanForDoor()
        {
            // Search Door components directly instead of OverlapSphere.
            // Inside buildings the collider buffer was saturated by wall/floor/beam
            // colliders (32+) and door colliders were never reached.
            var allDoors = GetDoorCache();
            Door closest = null;
            float closestDist = float.MaxValue;
            int doorsSeen = 0;
            int doorsValid = 0;

            foreach (var door in allDoors)
            {
                if (door == null) continue;

                float dist = Vector3.Distance(transform.position, door.transform.position);
                if (dist > ScanRadius) continue;

                doorsSeen++;

                if (!IsValidClosedDoor(door))
                {
                    if (_scanAttempts < 3)
                    {
                        var dnv = door.GetComponent<ZNetView>();
                        int state = (dnv != null && dnv.GetZDO() != null)
                            ? dnv.GetZDO().GetInt(ZDOVars.s_state, 0) : -1;
                        CompanionsPlugin.Log.LogDebug(
                            $"[DoorHandler] Scan — rejected door \"{door.m_name}\" " +
                            $"state={state} locked={door.m_keyItem != null} " +
                            $"pos={door.transform.position:F1}");
                    }
                    continue;
                }

                doorsValid++;
                if (dist < closestDist)
                {
                    closestDist = dist;
                    closest = door;
                }
            }

            _scanAttempts++;

            if (closest != null)
            {
                _targetDoor = closest;
                _doorPos = closest.transform.position;
                _phase = Phase.Approaching;
                _approachTimer = 0f;
                _stuckTimer = 0f;
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] Found closed door \"{closest.m_name}\" at dist={closestDist:F1}m " +
                    $"pos={_doorPos:F1} — approaching " +
                    $"({doorsSeen} doors nearby, {doorsValid} valid)");
            }
            else if (_scanAttempts <= 3 || _scanAttempts % 10 == 0)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] Scan #{_scanAttempts} — no valid doors " +
                    $"({doorsSeen} doors nearby, {doorsValid} valid) " +
                    $"stuck={_stuckTimer:F1}s pos={transform.position:F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Proactive scan — find the best door when circling a building
        //  Scores doors by proximity to the player (the building entrance is
        //  likely near the player) with a small penalty for companion distance.
        // ══════════════════════════════════════════════════════════════════════

        private void ScanForDoorProactive(Vector3 playerPos)
        {
            var allDoors = GetDoorCache();
            Door best = null;
            float bestScore = float.MaxValue;
            float bestDistToMe = 0f;
            float bestDistToPlayer = 0f;
            int doorsSeen = 0;

            foreach (var door in allDoors)
            {
                if (door == null) continue;

                float distToMe = Vector3.Distance(transform.position, door.transform.position);
                if (distToMe > ProactiveScanRadius) continue;

                doorsSeen++;

                if (!IsValidClosedDoor(door)) continue;

                // Score: primarily by closeness to player, secondarily by closeness to companion.
                // The door the player used is likely the closest door to them.
                float distToPlayer = Vector3.Distance(playerPos, door.transform.position);
                float score = distToPlayer + distToMe * 0.3f;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = door;
                    bestDistToMe = distToMe;
                    bestDistToPlayer = distToPlayer;
                }
            }

            if (best != null)
            {
                _targetDoor = best;
                _doorPos = best.transform.position;
                _phase = Phase.Approaching;
                _approachTimer = 0f;
                _stuckTimer = 0f;
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] PROACTIVE — found door \"{best.m_name}\" " +
                    $"dist={bestDistToMe:F1}m playerDist={bestDistToPlayer:F1}m " +
                    $"score={bestScore:F1} ({doorsSeen} doors nearby) — approaching");
            }
            else
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] PROACTIVE scan — no valid doors within {ProactiveScanRadius}m " +
                    $"({doorsSeen} doors seen)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: Approaching — walk toward door (pathfinding when far, direct when close)
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateApproaching(float dt)
        {
            if (!ValidateDoor()) return;

            _approachTimer += dt;
            if (_approachTimer > ApproachTimeout)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[DoorHandler] Approach timeout ({ApproachTimeout}s) — door \"{_targetDoor.m_name}\" " +
                    $"dist={Vector3.Distance(transform.position, _doorPos):F1}m — giving up");
                ResetToIdle();
                return;
            }

            float dist = Vector3.Distance(transform.position, _doorPos);

            if (dist > InteractDist * 2.5f)
            {
                // Far from door — use pathfinding to navigate around building corners.
                // The door is on the building exterior, so the navmesh path to it is valid.
                _ai.MoveToPoint(dt, _doorPos, InteractDist, true);
            }
            else
            {
                // Close to door — direct movement (bypasses navmesh issues near doorways)
                Vector3 dir = (_doorPos - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                    _ai.MoveTowards(dir.normalized, true);
            }

            if (dist < InteractDist)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] Within interact range ({dist:F1}m < {InteractDist}m) — opening door");

                bool result = _targetDoor.Interact(_humanoid, false, false);
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] Door.Interact returned {result}");

                _phase = Phase.WaitingForOpen;
                _waitTimer = OpenWaitTime;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: WaitingForOpen — door animation playing
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateWaitingForOpen(float dt)
        {
            if (!ValidateDoor()) return;

            _waitTimer -= dt;
            if (_waitTimer > 0f) return;

            // Check if door actually opened
            var doorNview = _targetDoor.GetComponent<ZNetView>();
            if (doorNview != null && doorNview.GetZDO() != null)
            {
                int state = doorNview.GetZDO().GetInt(ZDOVars.s_state, 0);
                if (state != 0)
                {
                    _phase = Phase.PassingThrough;
                    _passTimer = 0f;
                    CompanionsPlugin.Log.LogDebug(
                        $"[DoorHandler] Door opened (state={state}) — passing through");
                    return;
                }

                // Door still closed — try again
                CompanionsPlugin.Log.LogWarning(
                    $"[DoorHandler] Door still closed after wait — retrying interact " +
                    $"(dist={Vector3.Distance(transform.position, _doorPos):F1}m)");
                bool retry = _targetDoor.Interact(_humanoid, false, false);
                if (retry)
                {
                    _waitTimer = OpenWaitTime;
                    return;
                }
            }

            // Failed to open
            CompanionsPlugin.Log.LogWarning("[DoorHandler] Door didn't open after retry — resetting");
            ResetToIdle();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: PassingThrough — push companion through the open doorway
        //  The navmesh is static and doesn't update for open doors, so
        //  pathfinding-based Follow/MoveTo can't navigate through. We use
        //  MoveTowards (direct push) to bypass the navmesh entirely.
        // ══════════════════════════════════════════════════════════════════════

        private void UpdatePassingThrough(float dt)
        {
            if (!ValidateDoor()) return;

            _passTimer += dt;

            float distFromDoor = Vector3.Distance(transform.position, _doorPos);
            if (distFromDoor > CloseDistance || _passTimer > PassTimeout)
            {
                _phase = Phase.Closing;
                CompanionsPlugin.Log.LogDebug(
                    $"[DoorHandler] Past door (dist={distFromDoor:F1}m, timer={_passTimer:F1}s) — closing");
                return;
            }

            // Push through doorway — aim toward follow target if available,
            // otherwise push away from the door (through to the other side).
            var followTarget = _ai.GetFollowTarget();
            Vector3 pushDir;
            if (followTarget != null)
            {
                pushDir = followTarget.transform.position - transform.position;
            }
            else
            {
                pushDir = transform.position - _doorPos;
            }
            pushDir.y = 0f;
            if (pushDir.sqrMagnitude > 0.01f)
                _ai.PushDirection(pushDir.normalized, true);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: Closing — close the door behind us
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateClosing()
        {
            if (_targetDoor == null || !_targetDoor)
            {
                ResetToIdle();
                return;
            }

            // Don't close if player or another companion is still near
            if (IsAnyoneNearDoor())
            {
                CompanionsPlugin.Log.LogDebug("[DoorHandler] Someone near door — skipping close");
                ResetToIdle(withCooldown: true);
                return;
            }

            // Check door is still open
            var doorNview = _targetDoor.GetComponent<ZNetView>();
            if (doorNview != null && doorNview.GetZDO() != null)
            {
                int state = doorNview.GetZDO().GetInt(ZDOVars.s_state, 0);
                if (state != 0)
                {
                    _targetDoor.Interact(_humanoid, false, false);
                    CompanionsPlugin.Log.LogDebug("[DoorHandler] Closed door behind us");
                }
            }

            ResetToIdle(withCooldown: true);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private static bool IsValidClosedDoor(Door door)
        {
            if (door == null || !door) return false;

            // Must have network view with valid ZDO
            var nview = door.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            // Must be closed (state == 0)
            if (nview.GetZDO().GetInt(ZDOVars.s_state, 0) != 0) return false;

            // Skip locked doors
            if (door.m_keyItem != null) return false;

            // Respect ward protection
            if (door.m_checkGuardStone && !PrivateArea.CheckAccess(door.transform.position, 0f, false))
                return false;

            return true;
        }

        private bool IsAnyoneNearDoor()
        {
            // Check player
            if (Player.m_localPlayer != null)
            {
                float playerDist = Vector3.Distance(Player.m_localPlayer.transform.position, _doorPos);
                if (playerDist < ProximityCheck) return true;
            }

            // Check other companions (but not ourselves)
            int count = Physics.OverlapSphereNonAlloc(_doorPos, ProximityCheck, _proximityBuffer);
            for (int i = 0; i < count; i++)
            {
                var col = _proximityBuffer[i];
                if (col == null) continue;
                if (col.gameObject == gameObject) continue;
                if (col.GetComponent<CompanionSetup>() != null) return true;
            }

            return false;
        }

        private bool ValidateDoor()
        {
            if (_targetDoor == null || !_targetDoor)
            {
                CompanionsPlugin.Log.LogDebug("[DoorHandler] Target door destroyed — resetting");
                ResetToIdle();
                return false;
            }
            return true;
        }

        private void ResetToIdle(bool withCooldown = false)
        {
            _phase = Phase.Idle;
            _targetDoor = null;
            _stuckTimer = 0f;
            _scanTimer = 0f;
            _passTimer = 0f;
            _approachTimer = 0f;
            _scanAttempts = 0;
            ResetProactive();
            if (withCooldown)
                _cooldownTimer = PostCloseCooldown;
        }
    }
}
