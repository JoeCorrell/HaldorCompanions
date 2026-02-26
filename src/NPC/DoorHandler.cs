using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Detects nearby closed doors when the companion is stuck, opens them,
    /// walks through, then closes them behind. Works with vanilla Door component.
    /// </summary>
    public class DoorHandler : MonoBehaviour
    {
        private enum Phase { Idle, Approaching, WaitingForOpen, PassingThrough, Closing }

        // ── Tuning ──────────────────────────────────────────────────────────
        private const float ScanInterval      = 0.5f;
        private const float ScanRadius        = 5.0f;   // wider scan to catch doors reliably
        private const float StuckThreshold    = 1.0f;   // faster stuck detection (was 1.5)
        private const float StuckMoveDist     = 0.2f;   // more sensitive movement check (was 0.3)
        private const float StuckCheckInterval = 0.5f;   // check twice per second (was 1.0)
        private const float FollowDistMin     = 4.0f;   // engage when 4m+ from target (was 5)
        private const float InteractDist      = 2.0f;   // interact range (was 1.5)
        private const float OpenWaitTime      = 1.2f;   // wait for door animation (was 1.0)
        private const float CloseDistance      = 3.5f;   // close door when this far past it
        private const float PassTimeout       = 8.0f;   // safety timeout in PassingThrough
        private const float ProximityCheck    = 2.5f;   // don't close if player/companion near
        private const float ApproachTimeout   = 5.0f;   // give up approaching if takes too long
        private const float HeartbeatInterval = 2.0f;

        // ── Components ──────────────────────────────────────────────────────
        private MonsterAI      _ai;
        private Humanoid       _humanoid;
        private CompanionSetup _setup;
        private ZNetView       _nview;

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

        private static readonly Collider[] _scanBuffer = new Collider[32];

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _ai       = GetComponent<MonsterAI>();
            _humanoid = GetComponent<Humanoid>();
            _setup    = GetComponent<CompanionSetup>();
            _nview    = GetComponent<ZNetView>();
            CompanionsPlugin.Log.LogInfo("[DoorHandler] Initialized — door handling active");
        }

        private void Update()
        {
            if (_ai == null || _humanoid == null || _setup == null) return;
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;

            // Skip in Stay mode — not following player
            int mode = _nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode == CompanionSetup.ModeStay) return;

            float dt = Time.deltaTime;

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
                CompanionsPlugin.Log.LogInfo(
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
        //  Phase: Idle — detect when stuck near a closed door
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdle(float dt)
        {
            var followTarget = _ai.GetFollowTarget();
            if (followTarget == null) return;

            float distToTarget = Vector3.Distance(transform.position, followTarget.transform.position);
            if (distToTarget < FollowDistMin)
            {
                _stuckTimer = 0f;
                _scanAttempts = 0;
                return;
            }

            // Periodic stuck check — has companion barely moved?
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

        private void ScanForDoor()
        {
            int count = Physics.OverlapSphereNonAlloc(transform.position, ScanRadius, _scanBuffer);
            Door closest = null;
            float closestDist = float.MaxValue;
            int doorsSeen = 0;
            int doorsValid = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                var door = col.GetComponent<Door>();
                if (door == null) door = col.GetComponentInParent<Door>();
                if (door == null) continue;

                doorsSeen++;

                if (!IsValidClosedDoor(door))
                {
                    // Log why door was rejected (first few scans)
                    if (_scanAttempts < 3)
                    {
                        var dnv = door.GetComponent<ZNetView>();
                        int state = (dnv != null && dnv.GetZDO() != null)
                            ? dnv.GetZDO().GetInt(ZDOVars.s_state, 0) : -1;
                        CompanionsPlugin.Log.LogInfo(
                            $"[DoorHandler] Scan — rejected door \"{door.m_name}\" " +
                            $"state={state} locked={door.m_keyItem != null} " +
                            $"pos={door.transform.position:F1}");
                    }
                    continue;
                }

                doorsValid++;
                float dist = Vector3.Distance(transform.position, door.transform.position);
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
                CompanionsPlugin.Log.LogInfo(
                    $"[DoorHandler] Found closed door \"{closest.m_name}\" at dist={closestDist:F1}m " +
                    $"pos={_doorPos:F1} — approaching (scanned {count} colliders, " +
                    $"{doorsSeen} doors, {doorsValid} valid)");
            }
            else if (_scanAttempts <= 3 || _scanAttempts % 10 == 0)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[DoorHandler] Scan #{_scanAttempts} — no valid doors " +
                    $"(scanned {count} colliders, {doorsSeen} doors, {doorsValid} valid) " +
                    $"stuck={_stuckTimer:F1}s pos={transform.position:F1}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Phase: Approaching — walk directly toward door
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

            // Direct movement toward door (bypasses blocked navmesh)
            Vector3 dir = (_doorPos - transform.position);
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                _ai.MoveTowards(dir.normalized, true);

            if (dist < InteractDist)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[DoorHandler] Within interact range ({dist:F1}m < {InteractDist}m) — opening door");

                bool result = _targetDoor.Interact(_humanoid, false, false);
                CompanionsPlugin.Log.LogInfo(
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
                    CompanionsPlugin.Log.LogInfo(
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
        //  Phase: PassingThrough — let MonsterAI pathfind through open doorway
        // ══════════════════════════════════════════════════════════════════════

        private void UpdatePassingThrough(float dt)
        {
            if (!ValidateDoor()) return;

            _passTimer += dt;

            float distFromDoor = Vector3.Distance(transform.position, _doorPos);
            if (distFromDoor > CloseDistance || _passTimer > PassTimeout)
            {
                _phase = Phase.Closing;
                CompanionsPlugin.Log.LogInfo(
                    $"[DoorHandler] Past door (dist={distFromDoor:F1}m, timer={_passTimer:F1}s) — closing");
            }
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
                CompanionsPlugin.Log.LogInfo("[DoorHandler] Someone near door — skipping close");
                ResetToIdle();
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
                    CompanionsPlugin.Log.LogInfo("[DoorHandler] Closed door behind us");
                }
            }

            ResetToIdle();
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
            int count = Physics.OverlapSphereNonAlloc(_doorPos, ProximityCheck, _scanBuffer);
            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
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
                CompanionsPlugin.Log.LogInfo("[DoorHandler] Target door destroyed — resetting");
                ResetToIdle();
                return false;
            }
            return true;
        }

        private void ResetToIdle()
        {
            _phase = Phase.Idle;
            _targetDoor = null;
            _stuckTimer = 0f;
            _scanTimer = 0f;
            _passTimer = 0f;
            _approachTimer = 0f;
            _scanAttempts = 0;
        }
    }
}
