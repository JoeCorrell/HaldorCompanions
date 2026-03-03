using System;
using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Custom AI for companions, replacing vanilla MonsterAI.
    /// Inherits BaseAI for pathfinding, perception, and movement infrastructure.
    /// Owns the AI loop directly — no Harmony patches needed for targeting,
    /// weapon selection, or attack suppression.
    ///
    /// Keeps: sleep/wakeup, follow, targeting with harvest suppression,
    ///        combat movement + DoAttack with SuppressAttack.
    /// Drops: despawn, item consumption, circle target, flee behaviors,
    ///        hunt player, attack player objects.
    /// </summary>
    public class CompanionAI : BaseAI
    {
        // ══════════════════════════════════════════════════════════════════════
        //  Sleep System (from MonsterAI)
        // ══════════════════════════════════════════════════════════════════════

        [Header("Sleep")]
        public bool m_sleeping;
        public float m_wakeupRange = 5f;
        public bool m_noiseWakeup;
        public float m_maxNoiseWakeupRange = 50f;
        public EffectList m_wakeupEffects = new EffectList();
        public EffectList m_sleepEffects = new EffectList();
        public float m_wakeUpDelayMin;
        public float m_wakeUpDelayMax;
        public float m_fallAsleepDistance;

        private float m_sleepDelay = 0.5f;
        private float m_sleepTimer;
        private static readonly int s_sleeping = ZSyncAnimation.GetHash("sleeping");
        private static readonly int s_crouching = ZSyncAnimation.GetHash("crouching");

        // ══════════════════════════════════════════════════════════════════════
        //  Target Management (direct access — no reflection needed)
        // ══════════════════════════════════════════════════════════════════════

        internal Character m_targetCreature;
        internal StaticTarget m_targetStatic;
        internal Vector3 m_lastKnownTargetPos = Vector3.zero;
        internal bool m_beenAtLastPos;

        // ══════════════════════════════════════════════════════════════════════
        //  Follow System
        // ══════════════════════════════════════════════════════════════════════

        private GameObject m_follow;

        // ══════════════════════════════════════════════════════════════════════
        //  Combat State
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// When true, DoAttack is suppressed. Set by CombatController's
        /// blocking system so the companion doesn't attack while parrying.
        /// </summary>
        internal bool SuppressAttack;

        /// <summary>
        /// When > 0, UpdateTarget skips FindEnemy — player directed this
        /// companion to attack a specific target via hotkey.
        /// </summary>
        internal float DirectedTargetLockTimer;

        private float m_timeSinceAttacking;
        private float m_timeSinceSensedTargetCreature;
        private float m_updateTargetTimer;
        private float _attackRetryTime;      // cooldown after failed DoAttack (stamina, etc.)
        private bool  _inCombatStopRange;    // hysteresis flag — prevents stop-range oscillation

        /// <summary>
        /// When > 0, all AI movement is suppressed. Used to hold position
        /// after snapping to a bed/cart so animation starts at correct spot.
        /// </summary>
        internal float FreezeTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Heal System (Support Dverger)
        // ══════════════════════════════════════════════════════════════════════

        private bool _isDvergerSupport;
        private Character _healTarget;
        private float _healScanTimer;
        private float _healEquipTimer;
        private float _healLogTimer;
        private const float HealThreshold = 0.7f;
        private const float HealScanInterval = 1f;
        private const float HealLogInterval = 3f;
        private const float HealRange = 50f;

        // ══════════════════════════════════════════════════════════════════════
        //  Ship Attachment
        // ══════════════════════════════════════════════════════════════════════

        private bool       _isOnShip;
        private Ship       _attachedShip;
        private Vector3    _shipLocalPos;   // companion position in ship-local space
        private Quaternion _shipLocalRot;   // companion rotation in ship-local space
        private Collider[] _shipColliders;  // ignored while seated (prevents pushing the ship)
        private ZSyncAnimation _zanim;

        internal bool IsOnShip => _isOnShip;

        // Pending ship boarding — companion walks to ladder, climbs aboard, then sits
        private Ship   _pendingShip;
        private Chair  _pendingShipChair;
        private Ladder _pendingShipLadder;  // ladder to climb onto ship (null → direct range)
        private float  _pendingShipTimeout;
        private const float LadderBoardRange = 6f;    // distance to trigger ladder climb (ladder is elevated on hull)
        private const float ShipBoardingRange = 10f;  // fallback when no ladder
        private const float ShipBoardingTimeout = 30f;

        internal bool IsBoardingShip => _pendingShip != null;

        // ══════════════════════════════════════════════════════════════════════
        //  Pending Cart Navigation
        // ══════════════════════════════════════════════════════════════════════

        internal Vagon PendingCartAttach;
        internal Humanoid PendingCartHumanoid;
        private float _pendingCartTimeout;

        // ══════════════════════════════════════════════════════════════════════
        //  Pending Move-to-Position
        // ══════════════════════════════════════════════════════════════════════

        internal Vector3? PendingMoveTarget;
        private float _pendingMoveTimeout;

        // ══════════════════════════════════════════════════════════════════════
        //  Pending Deposit (walk to chest, then transfer items)
        // ══════════════════════════════════════════════════════════════════════

        internal Container PendingDepositContainer;
        internal Humanoid PendingDepositHumanoid;
        private float _pendingDepositTimeout;
        private bool  _depositChestOpen;
        private float _depositWorkTimer;
        private int   _depositCount;
        private readonly System.Collections.Generic.List<ItemDrop.ItemData> _depositQueue
            = new System.Collections.Generic.List<ItemDrop.ItemData>();
        private const float DepositItemInterval = 0.6f; // seconds between each item transfer

        // ══════════════════════════════════════════════════════════════════════
        //  Tombstone Recovery (after respawn)
        // ══════════════════════════════════════════════════════════════════════

        private TombStone _pendingTombstone;
        private float _tombstoneScanTimer;
        private float _tombstoneNavTimeout;
        private float _tombstoneNavStuckTime;
        private static float TombstoneScanInterval => ModConfig.TombstoneScanInterval.Value;
        private static float TombstoneNavTimeoutMax => ModConfig.TombstoneNavTimeout.Value;
        private const float TombstoneLootRange = 3f;

        // ══════════════════════════════════════════════════════════════════════
        //  Auto-Pickup (passive item collection like the Player)
        // ══════════════════════════════════════════════════════════════════════

        private int _autoPickupMask;
        private readonly Collider[] _autoPickupBuffer = new Collider[32];
        private static float AutoPickupRange => ModConfig.AutoPickupRange.Value;
        private const float AutoPickupPullSpeed = 15f;

        // ══════════════════════════════════════════════════════════════════════
        //  Drowning (damage when swimming with no stamina)
        // ══════════════════════════════════════════════════════════════════════

        internal static EffectList DrownEffects;   // copied from Player prefab
        private CompanionStamina _companionStamina;
        private float _drownDamageTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Hazard Recovery (tar, water)
        // ══════════════════════════════════════════════════════════════════════

        private float _hazardRecoveryTimer;
        private static readonly int s_taredHash = "Tared".GetStableHashCode();

        // ══════════════════════════════════════════════════════════════════════
        //  Constants
        // ══════════════════════════════════════════════════════════════════════

        private static float GiveUpTime => ModConfig.GiveUpTime.Value;
        private static float UpdateTargetIntervalNear => ModConfig.UpdateTargetIntervalNear.Value;
        private static float UpdateTargetIntervalFar => ModConfig.UpdateTargetIntervalFar.Value;
        private static float SelfDefenseRange => ModConfig.SelfDefenseRange.Value;
        private const float AlertRange = 9999f;

        // ══════════════════════════════════════════════════════════════════════
        //  Debug Logging
        // ══════════════════════════════════════════════════════════════════════

        private float _debugLogTimer;
        private float _stuckDetectTimer;
        private Vector3 _lastDebugPos;
        private float _targetLogTimer;
        private bool _lastPathOk = true;  // track path result transitions for one-shot logging

        private const float DebugLogInterval = 3f;
        private const float StuckThreshold = 5f;
        private static float FollowTeleportDist => ModConfig.FollowTeleportDistance.Value;

        // ── StayHome patrol enforcement ─────────────────────────────────────
        private float _homePatrolTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Formation Following
        // ══════════════════════════════════════════════════════════════════════

        private int _formationSlot = -1;
        private static float FormationOffset => ModConfig.FormationOffset.Value;
        private static float FormationCatchupDist => ModConfig.FormationCatchupDist.Value;

        // ══════════════════════════════════════════════════════════════════════
        //  Cached Components (avoid GetComponent every frame)
        // ══════════════════════════════════════════════════════════════════════

        private CompanionSetup _setup;
        private HarvestController _harvest;
        private CombatController _combat;
        private RepairController _repair;
        private SmeltController _smelt;
        private FarmController _farm;
        private HomesteadController _homestead;
        private DoorHandler _doorHandler;
        private CompanionRest _rest;
        private CompanionTalk _talk;
        private Rigidbody _body;

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        protected override void Awake()
        {
            base.Awake();

            _setup = GetComponent<CompanionSetup>();
            _harvest = GetComponent<HarvestController>();
            _combat = GetComponent<CombatController>();
            _repair = GetComponent<RepairController>();
            _smelt = GetComponent<SmeltController>();
            _farm = GetComponent<FarmController>();
            _homestead = GetComponent<HomesteadController>();
            _doorHandler = GetComponent<DoorHandler>();
            _rest = GetComponent<CompanionRest>();
            _talk = GetComponent<CompanionTalk>();
            _body = GetComponent<Rigidbody>();
            _zanim = GetComponent<ZSyncAnimation>();
            _autoPickupMask = LayerMask.GetMask("item");
            _companionStamina = GetComponent<CompanionStamina>();

            // Restore sleep state from ZDO
            ZDO zdo = m_nview.GetZDO();
            if (zdo != null)
            {
                m_sleeping = zdo.GetBool(ZDOVars.s_sleeping, m_sleeping);
                if (m_animator != null)
                    m_animator.SetBool(s_sleeping, IsSleeping());
            }

            // Randomize initial target scan delay
            m_updateTargetTimer = UnityEngine.Random.Range(0f, 2f);

            // Setup wake delay
            if (m_wakeUpDelayMin > 0f || m_wakeUpDelayMax > 0f)
                m_sleepDelay = UnityEngine.Random.Range(m_wakeUpDelayMin, m_wakeUpDelayMax);

            // Register sleep RPCs
            m_nview.Register("RPC_Wakeup", new Action<long>(RPC_Wakeup));
            m_nview.Register("RPC_Sleep", new Action<long>(RPC_Sleep));

            // Log animator details for debugging Dverger animation issues.
            // Note: CanWearArmor() uses ZDO.GetPrefab() which may not be ready at Awake.
            // Use the prefab name directly for the log instead.
            var unityAnimator = GetComponentInChildren<Animator>();
            string animCtrl = unityAnimator != null && unityAnimator.runtimeAnimatorController != null
                ? unityAnimator.runtimeAnimatorController.name : "NONE";
            int animParams = unityAnimator != null ? unityAnimator.parameterCount : 0;
            bool isDverger = gameObject.name.Contains("Dverger");
            _isDvergerSupport = gameObject.name.Contains("DvergerSupport");
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionAI] Awake complete — name=\"{m_character?.m_name}\" " +
                $"isDverger={isDverger} isSupport={_isDvergerSupport} " +
                $"animator=\"{animCtrl}\" params={animParams} " +
                $"zanim={m_animator != null}");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API — Follow
        // ══════════════════════════════════════════════════════════════════════

        public GameObject GetFollowTarget() => m_follow;

        public void SetFollowTarget(GameObject go) { m_follow = go; }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API — Targets
        // ══════════════════════════════════════════════════════════════════════

        public override Character GetTargetCreature() => m_targetCreature;

        public StaticTarget GetStaticTarget() => m_targetStatic;

        public void ClearTargets()
        {
            if (m_targetCreature != null || m_targetStatic != null)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[CompanionAI] ClearTargets — creature=\"{m_targetCreature?.m_name ?? ""}\" " +
                    $"static=\"{m_targetStatic?.name ?? ""}\"");
            }
            m_targetCreature = null;
            m_targetStatic = null;
            _inCombatStopRange = false;
            _attackRetryTime = 0f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Exposed Protected BaseAI Wrappers
        //  (eliminates all BaseAI reflection — controllers call these directly)
        // ══════════════════════════════════════════════════════════════════════

        // ══════════════════════════════════════════════════════════════════════
        //  Context Steering — obstacle-aware movement
        //
        //  Casts a fan of rays in a forward hemisphere to build a local awareness
        //  map of obstacles. Each candidate direction gets a score that balances
        //  goal-seeking (interest) with obstacle avoidance (danger). The companion
        //  picks the highest-scoring direction, letting it smoothly steer around
        //  walls, furniture, and other structures the NavMesh doesn't know about.
        // ══════════════════════════════════════════════════════════════════════

        private const int   CtxRayCount    = 13;    // rays from -90° to +90° in 15° steps
        private const float CtxProbeRange  = 4f;    // how far ahead to scan for obstacles
        private const float CtxDangerWeight = 1.8f; // how strongly obstacles repel
        private const float CtxMinClearance = 0.4f;  // below this distance, danger = max

        // Fallback stuck detection (when context steering alone can't escape)
        private float _avoidStuckTimer;
        private float _avoidCornerTimer;
        private float _avoidCornerAngle;
        private Vector3 _avoidLastPos;

        // Two masks for context steering:
        // _ctxSteerMask:      excludes "piece" — used for NavMesh-arrived fallback where
        //                     the target IS often a piece (smelter, chest). Including "piece"
        //                     here causes spinning when approaching structures.
        // _ctxSteerPieceMask: includes "piece" — used for path-stuck fallback where a
        //                     half-wall blocks the NavMesh path. The companion needs to see
        //                     the wall to navigate around it.
        private static int _ctxSteerMask;
        private static int _ctxSteerPieceMask;

        // True when MoveToPoint is using context steering fallback (pathfinding failed).
        // Prevents stuck recovery and proactive jump from fighting the fallback system.
        private bool _inContextSteerFallback;

        // Frame count when MoveToPoint last returned false (actively navigating).
        // Stuck recovery only fires when this is recent — prevents false positives
        // when controllers are in stationary phases (inserting items, repairing, etc.).
        private int _moveToPointActiveFrame;

        // Path-stuck detection: NavMesh says "still pathing" (MoveTo returns false)
        // but the companion can't actually move (half-wall blocking the NavMesh path).
        // After this timer exceeds the threshold, context steer takes over.
        private float _pathStuckTimer;
        private const float PathStuckThreshold = 1.5f;

        // Logging throttle — context steer decisions are per-frame, throttle to every 2s
        private float _ctxLogTimer;

        /// <summary>
        /// Scans a 180° forward arc with raycasts and returns the best movement
        /// direction that maximises progress toward the goal while avoiding obstacles.
        ///
        /// Open field: all rays clear → moves straight at goal.
        /// Wall ahead: forward rays blocked → steers to the clearest side.
        /// Corridor:   side rays blocked, forward clear → walks straight.
        /// Corner:     forward + one side blocked → steers to the open side.
        /// Dead end:   all forward rays blocked → picks perpendicular escape.
        /// </summary>
        private Vector3 ContextSteer(Vector3 goalDir, string reason = null, int maskOverride = 0)
        {
            goalDir.y = 0f;
            if (goalDir.sqrMagnitude < 0.01f)
                return transform.forward;
            goalDir.Normalize();

            Vector3 center = m_character.GetCenterPoint();

            // Lazy-init masks
            if (_ctxSteerMask == 0)
            {
                _ctxSteerMask = LayerMask.GetMask("Default", "static_solid", "Default_small", "terrain", "vehicle");
                _ctxSteerPieceMask = _ctxSteerMask | LayerMask.GetMask("piece");
            }

            float bestScore = float.MinValue;
            float bestAngle = 0f;
            Vector3 bestDir = goalDir;
            int dangerousRays = 0;
            float closestHit = CtxProbeRange;

            for (int i = 0; i < CtxRayCount; i++)
            {
                // Spread rays from -90° to +90° relative to goal direction
                float angle = -90f + i * (180f / (CtxRayCount - 1));
                Vector3 probeDir = Quaternion.Euler(0f, angle, 0f) * goalDir;

                // How far is the nearest obstacle in this direction?
                int mask = maskOverride != 0 ? maskOverride : _ctxSteerMask;
                float hitDist = Physics.Raycast(center, probeDir, out RaycastHit ctxHit, CtxProbeRange, mask)
                    ? ctxHit.distance : CtxProbeRange;

                // Interest: how aligned with goal (1.0 = perfect, 0 = perpendicular)
                float interest = Vector3.Dot(probeDir, goalDir);

                // Danger: 0 = far/clear, 1 = at minimum clearance or closer
                float danger = 0f;
                if (hitDist < CtxProbeRange)
                {
                    float safe = Mathf.Clamp01((hitDist - CtxMinClearance)
                                               / (CtxProbeRange - CtxMinClearance));
                    danger = 1f - safe;
                    dangerousRays++;
                    if (hitDist < closestHit) closestHit = hitDist;
                }

                // Squared danger for strong close-range repulsion
                float score = interest - CtxDangerWeight * danger * danger;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAngle = angle;
                    bestDir = probeDir;
                }
            }

            // Throttled logging — only log every 2 seconds
            _ctxLogTimer -= Time.fixedDeltaTime;
            if (_ctxLogTimer <= 0f)
            {
                _ctxLogTimer = 2f;
                CompanionsPlugin.Log.LogDebug(
                    $"[AI:CtxSteer] reason={reason ?? "?"} bestAngle={bestAngle:F0}° " +
                    $"score={bestScore:F2} dangerRays={dangerousRays}/{CtxRayCount} " +
                    $"closestHit={closestHit:F1}m pos={transform.position:F1}");
            }

            return bestDir;
        }

        internal bool MoveToPoint(float dt, Vector3 point, float dist, bool run)
        {
            // Flying creatures use vanilla MoveTo (MoveAndAvoid handles them)
            if (m_character.m_flying)
                return MoveTo(dt, point, dist, run);

            _inContextSteerFallback = false;

            float distXZ = Utils.DistanceXZ(point, transform.position);

            // Already close enough — use caller's dist directly (no floor).
            // Combat passes dist=0 to get right on top of the target; a floor
            // would cause the companion to stop too early for melee range.
            if (distXZ < dist)
            {
                StopMoving();
                _avoidCornerTimer = 0f;
                _avoidStuckTimer = 0f;
                _pathStuckTimer = 0f;
                return true;
            }

            Vector3 goalDir = point - transform.position;
            goalDir.y = 0f;
            goalDir.Normalize();

            // Try pathfinded movement
            bool result = MoveTo(dt, point, dist, run);

            // MoveTo returned "arrived" but we're still far — pathfinding failed.
            // Navigate with context steering directly toward the goal.
            if (result && distXZ > dist + 0.5f)
            {
                _inContextSteerFallback = true;
                _moveToPointActiveFrame = Time.frameCount;
                Vector3 bestDir = ContextSteer(goalDir, "fallback");
                MoveTowards(bestDir, run);
                UpdateFallbackStuck(dt, goalDir, run);
                return false;
            }

            // Path following is in progress. Check for path-stuck: NavMesh says
            // "still going" but the companion physically can't move (half-wall
            // blocking the path). After PathStuckThreshold seconds of near-zero
            // velocity, switch to context steer to navigate around the obstacle.
            if (!result)
            {
                _moveToPointActiveFrame = Time.frameCount;

                float vel = m_character.GetVelocity().magnitude;
                if (vel < GroundStuckVelThreshold && !m_character.InAttack())
                {
                    _pathStuckTimer += dt;
                    if (_pathStuckTimer >= PathStuckThreshold)
                    {
                        // NavMesh path goes through an obstacle — use context steer
                        // with "piece" layer so half-walls are detected and avoided.
                        _inContextSteerFallback = true;
                        Vector3 bestDir = ContextSteer(goalDir, "pathstuck", _ctxSteerPieceMask);
                        MoveTowards(bestDir, run);
                        UpdateFallbackStuck(dt, goalDir, run);
                        return false;
                    }
                }
                else
                {
                    _pathStuckTimer = 0f;
                }

                _avoidCornerTimer = 0f;
                _avoidStuckTimer = 0f;
            }
            else
            {
                _pathStuckTimer = 0f;
            }

            return result;
        }

        /// <summary>
        /// Stuck escape for fallback navigation — if context steering can't make
        /// progress (e.g. deep corner), back up at a random angle to break free.
        /// </summary>
        private void UpdateFallbackStuck(float dt, Vector3 goalDir, bool run)
        {
            if (m_character.InAttack()) return;

            _avoidCornerTimer -= dt;
            if (_avoidCornerTimer > 0f)
            {
                // Actively escaping a corner — override direction
                Vector3 escapeDir = Quaternion.Euler(0f, _avoidCornerAngle, 0f) * -goalDir;
                MoveTowards(escapeDir, run);
                return;
            }

            _avoidStuckTimer += dt;
            if (_avoidStuckTimer > 1.5f)
            {
                if (Vector3.Distance(transform.position, _avoidLastPos) < 0.3f)
                {
                    // Haven't moved — trigger corner escape
                    _avoidCornerTimer = 2f;
                    _avoidCornerAngle = UnityEngine.Random.Range(-60f, 60f);
                    _avoidStuckTimer = 0f;
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI:CtxSteer] Corner escape — backing up at {_avoidCornerAngle:F0}° " +
                        $"pos={transform.position:F1}");
                    return;
                }
                _avoidStuckTimer = 0f;
                _avoidLastPos = transform.position;
            }
        }

        internal void LookAtPoint(Vector3 point) => LookAt(point);

        internal bool IsLookingAtPoint(Vector3 point, float angle, bool invert = false)
            => IsLookingAt(point, angle, invert);

        internal new void SetAlerted(bool alert)
        {
            if (alert)
                m_timeSinceSensedTargetCreature = 0f;
            base.SetAlerted(alert);
        }

        internal Character FindNearbyEnemy() => FindEnemy();

        internal void FollowObject(GameObject go, float dt) => Follow(go, dt);

        internal void PushDirection(Vector3 dir, bool run) => MoveTowards(dir, run);

        internal void SetFormationSlot(int slot) { _formationSlot = slot; }

        internal void SetPendingCart(Vagon vagon, Humanoid humanoid)
        {
            PendingCartAttach = vagon;
            PendingCartHumanoid = humanoid;
            _pendingCartTimeout = 20f;
            SetFollowTarget(null);
        }

        internal void CancelPendingCart()
        {
            if (PendingCartAttach == null) return;
            PendingCartAttach = null;
            PendingCartHumanoid = null;
            RestoreFollowOrPatrol();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Ship Boarding
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Attach companion to a ship stool. Teleports to the chair's attach
        /// point, plays sit animation, disables physics, and snaps position
        /// to the ship every frame via UpdateShipAttach().
        /// </summary>
        internal void AttachToShip(Ship ship, Chair chair)
        {
            if (_isOnShip) DetachFromShip();

            _attachedShip = ship;
            _isOnShip = true;

            // Ignore collision between companion and ship so we don't push it
            var myCollider = GetComponent<CapsuleCollider>();
            if (myCollider != null)
            {
                _shipColliders = ship.GetComponentsInChildren<Collider>();
                foreach (var col in _shipColliders)
                    Physics.IgnoreCollision(myCollider, col, true);
            }

            // Snap to chair attach point
            Vector3 seatPos = chair.m_attachPoint.position;
            Quaternion seatRot = chair.m_attachPoint.rotation;
            transform.position = seatPos;
            transform.rotation = seatRot;

            // Store position in ship-local space for per-frame sync
            _shipLocalPos = ship.transform.InverseTransformPoint(seatPos);
            _shipLocalRot = Quaternion.Inverse(ship.transform.rotation) * seatRot;

            // Freeze rigidbody so physics doesn't push companion off the stool
            if (_body != null)
            {
                _body.position = seatPos;
                _body.velocity = Vector3.zero;
                _body.useGravity = false;
                _body.isKinematic = true;
            }

            // Sit animation (Player-model companions only)
            bool hasPlayerAnims = _setup != null && _setup.CanWearArmor();
            if (_zanim != null && hasPlayerAnims)
                _zanim.SetBool("attach_chair", true);

            // Hide weapons while seated
            var humanoid = m_character as Humanoid;
            if (humanoid != null) humanoid.HideHandItems();

            // Clear follow so AI doesn't try to walk
            SetFollowTarget(null);

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Ship attach — \"{m_character?.m_name}\" seated on \"{ship.name}\" " +
                $"at {seatPos:F1} localPos={_shipLocalPos:F2}");
        }

        /// <summary>
        /// Detach companion from ship. Restores physics, stops sit animation,
        /// and returns companion to following the player.
        /// </summary>
        internal void DetachFromShip()
        {
            if (!_isOnShip) return;

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Ship detach — \"{m_character?.m_name}\" leaving ship");

            Ship shipRef = _attachedShip;

            // Stop sit animation
            bool hasPlayerAnims = _setup != null && _setup.CanWearArmor();
            if (_zanim != null && hasPlayerAnims)
                _zanim.SetBool("attach_chair", false);

            // Restore collision with ship colliders
            if (_shipColliders != null)
            {
                var myCollider = GetComponent<CapsuleCollider>();
                if (myCollider != null)
                {
                    foreach (var col in _shipColliders)
                        if (col != null)
                            Physics.IgnoreCollision(myCollider, col, false);
                }
                _shipColliders = null;
            }

            // Restore physics
            if (_body != null)
            {
                _body.isKinematic = false;
                _body.useGravity = true;
                _body.velocity = Vector3.zero;
            }

            _isOnShip = false;
            _attachedShip = null;

            // Teleport off the ship — use the ladder (climb down) if available,
            // otherwise teleport near the player. The ship deck has no NavMesh
            // so the companion can't pathfind off it.
            bool teleported = false;
            if (shipRef != null)
            {
                var ladder = shipRef.GetComponentInChildren<Ladder>();
                if (ladder != null)
                {
                    // Ladder transform is at the base (water/dock side) — climb down
                    Vector3 disembarkPos = ladder.transform.position + Vector3.up * 0.3f;
                    transform.position = disembarkPos;
                    if (_body != null)
                    {
                        _body.position = disembarkPos;
                        _body.velocity = Vector3.zero;
                    }
                    teleported = true;
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI] Ship detach — climbed down ladder to {disembarkPos:F1}");
                }
            }

            if (!teleported && Player.m_localPlayer != null)
            {
                // No ladder — teleport near the player
                Vector3 playerPos = Player.m_localPlayer.transform.position;
                Vector3 spawnPos = playerPos + Vector3.right * 2f;
                for (int i = 0; i < 20; i++)
                {
                    Vector2 rnd = UnityEngine.Random.insideUnitCircle * 3f;
                    Vector3 candidate = playerPos + new Vector3(rnd.x, 0f, rnd.y);
                    if (ZoneSystem.instance != null &&
                        ZoneSystem.instance.FindFloor(candidate, out float height))
                    {
                        candidate.y = height;
                        spawnPos = candidate;
                        break;
                    }
                }
                transform.position = spawnPos;
                if (_body != null)
                {
                    _body.position = spawnPos;
                    _body.velocity = Vector3.zero;
                }
                CompanionsPlugin.Log.LogDebug(
                    $"[AI] Ship detach — teleported near player at {spawnPos:F1}");
            }

            if (!teleported && Player.m_localPlayer == null)
            {
                // Last resort — nudge up so they don't clip
                transform.position += new Vector3(0f, 0.5f, 0f);
            }

            RestoreFollowOrPatrol();
        }

        /// <summary>
        /// Per-frame update while seated on a ship. Snaps position/rotation
        /// to the stored local-space offset so the companion moves with the ship.
        /// </summary>
        private void UpdateShipAttach()
        {
            // Ship destroyed or unloaded?
            if (_attachedShip == null)
            {
                DetachFromShip();
                return;
            }

            // Snap position to ship-local offset
            Vector3 worldPos = _attachedShip.transform.TransformPoint(_shipLocalPos);
            Quaternion worldRot = _attachedShip.transform.rotation * _shipLocalRot;
            transform.position = worldPos;
            transform.rotation = worldRot;

            if (_body != null)
            {
                _body.position = worldPos;
                // Sync velocity with ship so any physics interaction stays consistent
                Rigidbody shipBody = _attachedShip.GetComponent<Rigidbody>();
                if (shipBody != null)
                    _body.velocity = shipBody.GetPointVelocity(worldPos);
            }
        }

        /// <summary>
        /// Begin walking toward a ship to board it. Once within range, the companion
        /// will teleport to the chair and attach. Falls back to direct attach on timeout.
        /// </summary>
        internal void SetPendingShipBoard(Ship ship, Chair chair)
        {
            _pendingShip = ship;
            _pendingShipChair = chair;
            _pendingShipTimeout = ShipBoardingTimeout;
            SetFollowTarget(null);

            // Find the nearest ladder on the ship to climb aboard
            var ladders = ship.GetComponentsInChildren<Ladder>();
            if (ladders != null && ladders.Length > 0)
            {
                Ladder best = null;
                float bestDist = float.MaxValue;
                foreach (var lad in ladders)
                {
                    float d = Vector3.Distance(transform.position, lad.transform.position);
                    if (d < bestDist) { bestDist = d; best = lad; }
                }
                _pendingShipLadder = best;
            }
            else
            {
                _pendingShipLadder = null;
            }

            string ladderInfo = _pendingShipLadder != null
                ? $"ladder at {_pendingShipLadder.transform.position:F1}"
                : "no ladder found";
            CompanionsPlugin.Log.LogDebug(
                $"[AI] SetPendingShipBoard — ship=\"{ship.name}\" " +
                $"chair at {chair.m_attachPoint.position:F1} " +
                $"{ladderInfo} dist={Vector3.Distance(transform.position, ship.transform.position):F1}");
        }

        internal void CancelPendingShipBoard()
        {
            if (_pendingShip == null) return;
            CompanionsPlugin.Log.LogDebug(
                $"[AI] CancelPendingShipBoard — was heading to \"{_pendingShip?.name}\"");
            _pendingShip = null;
            _pendingShipChair = null;
            _pendingShipLadder = null;
            RestoreFollowOrPatrol();
        }

        internal void SetMoveTarget(Vector3 pos)
        {
            CompanionsPlugin.Log.LogDebug(
                $"[AI] SetMoveTarget — target={pos:F1} dist={Vector3.Distance(transform.position, pos):F1}");
            PendingMoveTarget = pos;
            _pendingMoveTimeout = 30f;
            SetFollowTarget(null);
        }

        internal void CancelMoveTarget()
        {
            if (PendingMoveTarget == null) return;
            CompanionsPlugin.Log.LogDebug(
                $"[AI] CancelMoveTarget — was={PendingMoveTarget.Value:F1}");
            PendingMoveTarget = null;
            RestoreFollowOrPatrol();
        }

        internal void SetPendingDeposit(Container chest, Humanoid humanoid)
        {
            PendingDepositContainer = chest;
            PendingDepositHumanoid = humanoid;
            _pendingDepositTimeout = 20f;
            SetFollowTarget(null);
            CompanionsPlugin.Log.LogDebug(
                $"[AI] SetPendingDeposit — chest=\"{chest.m_name}\" " +
                $"dist={Vector3.Distance(transform.position, chest.transform.position):F1}");
        }

        internal void CancelPendingDeposit()
        {
            if (PendingDepositContainer == null) return;
            CompanionsPlugin.Log.LogDebug("[AI] CancelPendingDeposit");

            // Close chest if we opened it
            if (_depositChestOpen && PendingDepositContainer != null)
            {
                PendingDepositContainer.SetInUse(false);
                _depositChestOpen = false;
            }

            PendingDepositContainer = null;
            PendingDepositHumanoid = null;
            _depositQueue.Clear();
            _depositCount = 0;

            RestoreFollowOrPatrol();
        }

        /// <summary>
        /// Restore follow to player OR patrol at home, depending on Follow toggle and StayHome state.
        /// Used after cancelling pending actions.
        /// </summary>
        private void RestoreFollowOrPatrol()
        {
            bool follow = _setup != null && _setup.GetFollow();
            if (follow && Player.m_localPlayer != null)
            {
                SetFollowTarget(Player.m_localPlayer.gameObject);
                CompanionsPlugin.Log.LogDebug("[AI] RestoreFollowOrPatrol → follow player (Follow ON)");
            }
            else if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition())
            {
                SetFollowTarget(null);
                SetPatrolPointAt(_setup.GetHomePosition());
                CompanionsPlugin.Log.LogDebug(
                    $"[AI] RestoreFollowOrPatrol → patrol at home {_setup.GetHomePosition():F1}");
            }
            else
            {
                SetFollowTarget(null);
                CompanionsPlugin.Log.LogDebug("[AI] RestoreFollowOrPatrol → idle (Follow OFF, no StayHome)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Tombstone Recovery
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Returns true if tombstone recovery is driving movement (caller should return true).
        /// After respawn, the companion has HC_TombstoneId set on its ZDO. It scans for
        /// a TombStone with a matching ID, navigates to it, and loots all items.
        /// </summary>
        private bool UpdateTombstoneRecovery(float dt)
        {
            // Already navigating to a tombstone — use Unity's overloaded != to detect destroyed objects
            if (_pendingTombstone != null)
            {
                // Ensure follow is clear — external systems (UI freeze/restore,
                // CompanionSetup.Update) can re-set it, pulling the companion away.
                if (m_follow != null)
                    SetFollowTarget(null);

                _tombstoneNavTimeout -= dt;
                if (_tombstoneNavTimeout <= 0f)
                {
                    CompanionsPlugin.Log.LogInfo("[AI] Tombstone navigation timed out — giving up");
                    _pendingTombstone = null;
                    ClearTombstoneId();
                    RestoreFollowOrPatrol();
                    return false;
                }

                Vector3 tombPos = _pendingTombstone.transform.position;
                float dist = Vector3.Distance(transform.position, tombPos);

                if (dist < TombstoneLootRange)
                {
                    StopMoving();
                    LookAtPoint(tombPos);
                    CompanionsPlugin.Log.LogInfo(
                        $"[AI] Reached tombstone at {tombPos:F1} — looting items");
                    LootTombstone(_pendingTombstone);
                    _pendingTombstone = null;
                    ClearTombstoneId();
                    RestoreFollowOrPatrol();
                    return false;
                }

                // Pathfinding fallback: if within 10m but nav is stuck for 10s,
                // warp directly to the tombstone rather than giving up.
                if (!FoundPath() && dist < 10f)
                {
                    _tombstoneNavStuckTime += dt;
                    if (_tombstoneNavStuckTime > 10f)
                    {
                        CompanionsPlugin.Log.LogInfo(
                            $"[AI] Tombstone nav stuck for 10s at dist={dist:F1}m — warping to tombstone");
                        transform.position = tombPos + Vector3.up * 0.5f;
                        if (_body != null)
                        {
                            _body.position = transform.position;
                            _body.velocity = Vector3.zero;
                        }
                        _tombstoneNavStuckTime = 0f;
                        // Next frame dist will be < TombstoneLootRange → loot
                        return true;
                    }
                }
                else
                {
                    _tombstoneNavStuckTime = 0f;
                }

                // Log navigation progress periodically
                _tombstoneScanTimer -= dt;
                if (_tombstoneScanTimer <= 0f)
                {
                    _tombstoneScanTimer = TombstoneScanInterval;
                    CompanionsPlugin.Log.LogInfo(
                        $"[AI] Navigating to tombstone — dist={dist:F1}m timeout={_tombstoneNavTimeout:F0}s pathOK={FoundPath()}");
                }

                // Run to tombstone
                bool run = dist > 10f;
                MoveToPoint(dt, tombPos, TombstoneLootRange - 0.5f, run);
                return true;
            }

            // If C# reference is non-null but Unity considers it destroyed (player looted / despawned),
            // the above Unity != check returns false. Detect this via ReferenceEquals.
            if (!System.Object.ReferenceEquals(_pendingTombstone, null))
            {
                CompanionsPlugin.Log.LogInfo(
                    "[AI] Tombstone was destroyed during navigation (player looted or despawned) — stopping recovery");
                _pendingTombstone = null;
                ClearTombstoneId();
                RestoreFollowOrPatrol();
                return false;
            }

            // No pending tombstone — check if we have a tombstone ID to look for
            var zdo = m_nview?.GetZDO();
            if (zdo == null) return false;
            long tombstoneId = zdo.GetLong(CompanionSetup.TombstoneIdHash, 0L);
            if (tombstoneId == 0L) return false;

            // Periodic scan for matching tombstone
            _tombstoneScanTimer -= dt;
            if (_tombstoneScanTimer > 0f) return false;
            _tombstoneScanTimer = TombstoneScanInterval;

            var tombstones = UnityEngine.Object.FindObjectsByType<TombStone>(FindObjectsSortMode.None);
            CompanionsPlugin.Log.LogDebug(
                $"[AI] Scanning for tombstone id={tombstoneId} — {tombstones.Length} tombstones in scene");

            foreach (var ts in tombstones)
            {
                if (ts == null) continue;
                var tsNview = ts.GetComponent<ZNetView>();
                if (tsNview?.GetZDO() == null) continue;

                long tsId = tsNview.GetZDO().GetLong(CompanionSetup.TombstoneIdHash, 0L);
                if (tsId != tombstoneId) continue;

                // Found our tombstone
                _pendingTombstone = ts;
                _tombstoneNavTimeout = TombstoneNavTimeoutMax;
                _tombstoneNavStuckTime = 0f;

                float dist = Vector3.Distance(transform.position, ts.transform.position);
                CompanionsPlugin.Log.LogInfo(
                    $"[AI] Found tombstone at {ts.transform.position:F1} — dist={dist:F1}m, navigating to recover items");

                if (_talk != null)
                    _talk.Say(ModLocalization.Loc("hc_speech_tomb_found"));

                // Override follow to navigate
                SetFollowTarget(null);
                return true;
            }

            return false;
        }

        /// <summary>True when the companion is navigating to or scanning for its tombstone.</summary>
        internal bool IsRecoveringTombstone
        {
            get
            {
                if (_pendingTombstone != null) return true;
                var zdo = m_nview?.GetZDO();
                return zdo != null && zdo.GetLong(CompanionSetup.TombstoneIdHash, 0L) != 0L;
            }
        }

        /// <summary>
        /// Directed command entry point — player points at a tombstone and presses
        /// the command key, injecting the tombstone directly into the nav pipeline.
        /// </summary>
        internal void SetDirectedTombstoneRecovery(TombStone tombstone)
        {
            _pendingTombstone = tombstone;
            _tombstoneNavTimeout = TombstoneNavTimeoutMax;
            _tombstoneScanTimer = TombstoneScanInterval;
            _tombstoneNavStuckTime = 0f;
            SetFollowTarget(null);

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Directed tombstone recovery — target at {tombstone.transform.position:F1}");

            if (_talk != null)
                _talk.Say(ModLocalization.Loc("hc_speech_tomb_found"));
        }

        /// <summary>Cancel any in-progress tombstone recovery and restore follow.</summary>
        internal void CancelTombstoneRecovery()
        {
            if (_pendingTombstone == null && System.Object.ReferenceEquals(_pendingTombstone, null))
                return;
            _pendingTombstone = null;
            ClearTombstoneId();
            RestoreFollowOrPatrol();
            CompanionsPlugin.Log.LogDebug("[AI] CancelTombstoneRecovery");
        }

        private void LootTombstone(TombStone tombstone)
        {
            var container = tombstone.GetComponent<Container>();
            if (container == null)
            {
                CompanionsPlugin.Log.LogError("[AI] Tombstone has no Container — cannot loot");
                return;
            }

            var tombInv = container.GetInventory();
            var humanoid = m_character as Humanoid;
            if (tombInv == null || humanoid == null)
            {
                CompanionsPlugin.Log.LogError(
                    $"[AI] Cannot loot tombstone — tombInv={tombInv != null} humanoid={humanoid != null}");
                return;
            }

            var myInv = humanoid.GetInventory();
            if (myInv == null)
            {
                CompanionsPlugin.Log.LogError("[AI] Companion has no inventory — cannot loot");
                return;
            }

            int totalInTomb = tombInv.NrOfItems();
            CompanionsPlugin.Log.LogInfo(
                $"[AI] Looting tombstone — {totalInTomb} items to recover, " +
                $"tombInv dim={tombInv.GetWidth()}x{tombInv.GetHeight()}, " +
                $"myInv dim={myInv.GetWidth()}x{myInv.GetHeight()} items={myInv.NrOfItems()} " +
                $"empty={myInv.GetEmptySlots()}");

            // Move all items from tombstone to companion inventory
            // AddItem calls Changed() internally; tombstone auto-despawns when NrOfItems()==0
            var items = new System.Collections.Generic.List<ItemDrop.ItemData>(tombInv.GetAllItems());
            int moved = 0;
            int failed = 0;
            foreach (var item in items)
            {
                if (myInv.AddItem(item))
                {
                    tombInv.RemoveItem(item);
                    moved++;
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI]   Recovered: \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack} " +
                        $"→ pos=({item.m_gridPos.x},{item.m_gridPos.y})");
                }
                else
                {
                    failed++;
                    CompanionsPlugin.Log.LogWarning(
                        $"[AI]   Failed to recover: \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack} — " +
                        $"inventory full? myInv items={myInv.NrOfItems()} empty={myInv.GetEmptySlots()}");
                }
            }

            // Repack inventory grid positions sequentially to eliminate gaps.
            // Vanilla AddItem uses TopFirst/BottomFirst sorting which places equipment
            // at row 0 and materials at the bottom row, leaving empty rows in between.
            // This looks like "glitched slots" to the player. Repacking places all items
            // in a clean sequential layout: (0,0), (1,0), (2,0), etc.
            if (moved > 0)
            {
                int w = myInv.GetWidth();
                int idx = 0;
                foreach (var item in myInv.GetAllItems())
                {
                    item.m_gridPos = new Vector2i(idx % w, idx / w);
                    idx++;
                }
                myInv.m_onChanged?.Invoke();
                CompanionsPlugin.Log.LogDebug(
                    $"[AI] Repacked {idx} inventory items to sequential positions (width={w})");
            }

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Tombstone loot complete — recovered {moved}/{totalInTomb} items" +
                (failed > 0 ? $" ({failed} could not fit)" : "") +
                $", {tombInv.NrOfItems()} remaining in tombstone, " +
                $"myInv items={myInv.NrOfItems()} dim={myInv.GetWidth()}x{myInv.GetHeight()}");

            if (_talk != null)
                _talk.Say(moved > 0 ? ModLocalization.Loc("hc_speech_tomb_recovered") : ModLocalization.Loc("hc_speech_tomb_empty"));
        }

        private void ClearTombstoneId()
        {
            var zdo = m_nview?.GetZDO();
            if (zdo != null)
                zdo.Set(CompanionSetup.TombstoneIdHash, 0L);
        }

        /// <summary>
        /// When StayHome is active and companion has no follow target,
        /// periodically re-set patrol to home position. UI freeze and other
        /// systems overwrite patrol, so this keeps the companion anchored.
        /// Also manages m_randomMoveInterval: the prefab sets it to 9999 to
        /// suppress vanilla random movement, but StayHome+Wander and
        /// StayHome+Gather need a normal interval for IdleMovement to work.
        /// </summary>
        private static float WanderMoveInterval => ModConfig.WanderMoveInterval.Value;
        private const float SuppressedMoveInterval = 9999f;

        private bool _returningHome;

        /// <summary>
        /// Returns true if this method is driving movement (caller should skip IdleMovement).
        /// </summary>
        private bool EnforceHomePatrol(float dt)
        {
            // Follow toggle ON overrides StayHome — companion follows player
            if (_setup != null && _setup.GetFollow())
            {
                _returningHome = false;
                if (m_randomMoveRange > 10f)
                    m_randomMoveRange = 4f;
                if (m_randomMoveInterval < 100f)
                    m_randomMoveInterval = SuppressedMoveInterval;
                return false;
            }

            if (_setup == null || !_setup.GetStayHome() || !_setup.HasHomePosition())
            {
                // Not in StayHome — restore defaults
                if (m_randomMoveRange > 10f)
                    m_randomMoveRange = 4f;
                if (m_randomMoveInterval < 100f)
                    m_randomMoveInterval = SuppressedMoveInterval;
                _returningHome = false;
                return false;
            }
            if (m_follow != null) return false; // following something actively

            // Wander toggle or active gather mode: allow random movement around home.
            // Gathering companions wander between scans to discover new resource patches.
            bool shouldWander = _setup.GetWander();
            bool isGathering = _harvest != null && _harvest.IsInGatherMode;

            if (shouldWander || isGathering)
            {
                m_randomMoveRange = CompanionSetup.MaxLeashDistance; // 50m
                // Switch from the suppressed interval to a normal one (once)
                if (m_randomMoveInterval > 10f)
                {
                    m_randomMoveInterval = WanderMoveInterval;
                    ResetRandomMovement(); // flush the old ~9999s timer
                }
                _returningHome = false;
            }
            else
            {
                m_randomMoveRange = 0f; // stay put at home
                if (m_randomMoveInterval < 100f)
                    m_randomMoveInterval = SuppressedMoveInterval;

                // Wander OFF but far from home — walk back directly.
                // RandomMovement with range=0 can't compute a return vector,
                // so we drive movement with MoveTo instead.
                float distFromHome = Utils.DistanceXZ(
                    transform.position, _setup.GetHomePosition());
                if (distFromHome > 3f)
                {
                    if (!_returningHome)
                    {
                        _returningHome = true;
                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Returning home — dist={distFromHome:F1}m");
                    }
                    MoveToPoint(dt, _setup.GetHomePosition(), 2f, distFromHome > 10f);
                    return true; // driving movement — skip IdleMovement
                }
                _returningHome = false;
            }

            _homePatrolTimer -= dt;
            if (_homePatrolTimer > 0f) return false;
            _homePatrolTimer = 2f; // re-set every 2s

            SetPatrolPointAt(_setup.GetHomePosition());
            return false;
        }

        /// <summary>
        /// Set patrol point to a specific world position.
        /// BaseAI.SetPatrolPoint(Vector3) is private, so we write ZDO vars directly.
        /// GetPatrolPoint() refreshes from ZDO every 1s.
        /// </summary>
        internal void SetPatrolPointAt(Vector3 point)
        {
            if (m_nview?.GetZDO() == null) return;
            m_nview.GetZDO().Set(ZDOVars.s_patrol, true);
            m_nview.GetZDO().Set(ZDOVars.s_patrolPoint, point);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Main AI Loop
        // ══════════════════════════════════════════════════════════════════════

        public override bool UpdateAI(float dt)
        {
            if (!base.UpdateAI(dt))
                return false;

            // Sleep system
            UpdateSleep(dt);
            if (IsSleeping())
                return true;

            // Freeze timer — brief hold during cart attach or similar
            if (FreezeTimer > 0f)
            {
                FreezeTimer -= dt;
                if (FreezeTimer <= 0f)
                    CompanionsPlugin.Log.LogDebug($"[AI] FreezeTimer expired — resuming movement");
                return true;
            }

            // Resting (sitting/sleeping) — skip all movement so position stays
            if (_rest != null && _rest.IsResting)
                return true;

            // Ship attachment — snap to seat every frame, suppress all AI
            if (_isOnShip)
            {
                UpdateShipAttach();
                return true;
            }

            // Drowning — damage companion when swimming with no stamina
            UpdateDrowning(dt);

            // Hazard recovery — escape tar pits and water
            if (UpdateHazardRecovery(dt))
                return true; // survival overrides all other AI

            // Periodic blacklist cleanup
            CleanupBlacklist();

            // Proactive water avoidance — if on dry ground heading toward water
            // with insufficient stamina, stop moving and let pathfinding re-route
            if (UpdateWaterAvoidance(dt))
                return true;

            // Auto-pickup — passively collect nearby items
            AutoPickup(dt);

            // Stuck-on-furniture recovery — if stuck on a Bed/Chair collider,
            // nudge off so pathfinding can resume (NavMesh doesn't cover furniture).
            UpdateFurnitureUnstuck(dt);

            // Ground movement stuck recovery — detects when pathfinding fails
            // and forces the companion to strafe around obstacles.
            if (UpdateGroundStuckRecovery(dt))
                return true; // recovery strafe overrides normal AI

            // Proactive jump — detect small terrain step-ups that block movement
            // and jump to clear them before the full stuck system triggers.
            UpdateProactiveJump(dt);

            // Follow-mode teleport — warp companion near player if too far away.
            // Only teleport if Follow toggle is actually ON (belt-and-suspenders check
            // in case m_follow was set by a code path that didn't verify the toggle).
            if (m_follow != null && _setup != null && _setup.GetFollow())
            {
                float distToFollow = Vector3.Distance(transform.position, m_follow.transform.position);
                if (distToFollow > FollowTeleportDist)
                    TeleportToFollowTarget();
            }

            // StayHome hard leash — if companion strays more than 50m from home
            // (e.g. during combat chase), teleport them back and disengage.
            if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition()
                && !_setup.GetFollow())
            {
                float distFromHome = Utils.DistanceXZ(transform.position, _setup.GetHomePosition());
                if (distFromHome > CompanionSetup.MaxLeashDistance)
                {
                    TeleportToHome();
                }
            }

            // Rest navigation — walking to a bed or fire
            if (_rest != null && _rest.IsNavigating)
            {
                Vector3 navTarget = _rest.NavTarget;
                float distToNav = Vector3.Distance(transform.position, navTarget);
                if (distToNav < 2f)
                {
                    _rest.ArriveAtNavTarget();
                }
                else
                {
                    MoveToPoint(dt, navTarget, 1.5f, true);
                }
                return true;
            }

            // Ship boarding — walk to ladder, climb aboard, then sit on chair
            if (_pendingShip != null)
            {
                _pendingShipTimeout -= dt;

                // Ship or chair destroyed while walking?
                if (_pendingShip == null || _pendingShipChair == null ||
                    _pendingShipChair.m_attachPoint == null)
                {
                    CompanionsPlugin.Log.LogDebug("[AI] Ship boarding target lost — cancelling");
                    CancelPendingShipBoard();
                }
                else if (_pendingShipTimeout <= 0f)
                {
                    // Timeout — teleport-attach as fallback
                    CompanionsPlugin.Log.LogDebug(
                        "[AI] Ship boarding timed out — teleporting to chair as fallback");
                    Ship ship = _pendingShip;
                    Chair chair = _pendingShipChair;
                    _pendingShip = null;
                    _pendingShipChair = null;
                    _pendingShipLadder = null;
                    AttachToShip(ship, chair);
                }
                else if (_pendingShipLadder != null)
                {
                    // Walk toward ship, use ladder when close enough.
                    // Use horizontal (XZ) distance because the ladder is on the hull
                    // above water — 3D distance includes the hull height penalty.
                    Vector3 ladderPos = _pendingShipLadder.transform.position;
                    Vector3 toladder = ladderPos - transform.position;
                    float horizDist = new Vector2(toladder.x, toladder.z).magnitude;

                    if (horizDist < LadderBoardRange)
                    {
                        // Close enough horizontally — use the ladder to climb aboard
                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Ship boarding — using ladder (horiz={horizDist:F1}m, " +
                            $"3D={Vector3.Distance(transform.position, ladderPos):F1}m)");

                        // Replicate Ladder.Interact: teleport to deck landing point
                        if (_pendingShipLadder.m_targetPos != null)
                        {
                            transform.position = _pendingShipLadder.m_targetPos.position;
                            transform.rotation = _pendingShipLadder.m_targetPos.rotation;
                            if (_body != null)
                            {
                                _body.position = _pendingShipLadder.m_targetPos.position;
                                _body.velocity = Vector3.zero;
                            }
                        }

                        // Now on deck — sit on the assigned chair
                        Ship ship = _pendingShip;
                        Chair chair = _pendingShipChair;
                        _pendingShip = null;
                        _pendingShipChair = null;
                        _pendingShipLadder = null;
                        AttachToShip(ship, chair);
                    }
                    else
                    {
                        // Pathfind toward the ship center (always on water NavMesh).
                        // We can't path directly to the ladder because it's on the hull
                        // with no NavMesh. Horizontal distance to the ladder drives the
                        // boarding trigger above, so approaching the ship from any angle works.
                        Vector3 navTarget = _pendingShip.transform.position;
                        MoveTo(dt, navTarget, 0.5f, horizDist > 15f);
                    }
                }
                else
                {
                    // No ladder — walk toward ship and board when within range
                    Vector3 shipPos = _pendingShip.transform.position;
                    float distToShip = Vector3.Distance(transform.position, shipPos);

                    if (distToShip < ShipBoardingRange)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Ship boarding — within range ({distToShip:F1}m), attaching to chair");
                        Ship ship = _pendingShip;
                        Chair chair = _pendingShipChair;
                        _pendingShip = null;
                        _pendingShipChair = null;
                        _pendingShipLadder = null;
                        AttachToShip(ship, chair);
                    }
                    else
                    {
                        MoveTo(dt, shipPos, 0.5f, distToShip > 15f);
                    }
                }
                return true;
            }

            // Cart navigation — walking to cart attach point
            if (PendingCartAttach != null)
            {
                _pendingCartTimeout -= dt;
                if (_pendingCartTimeout <= 0f || PendingCartAttach == null)
                {
                    CompanionsPlugin.Log.LogDebug("[AI] Cart navigation timed out — cancelling");
                    CancelPendingCart();
                }
                else
                {
                    Vector3 attachWorldPos = PendingCartAttach.m_attachPoint.position
                        - PendingCartAttach.m_attachOffset;
                    attachWorldPos.y = transform.position.y;
                    float distToAttach = Vector3.Distance(transform.position, attachWorldPos);

                    if (distToAttach < 2f)
                    {
                        // Close enough — snap to exact attach position and interact
                        // Sync both transform and Rigidbody to prevent physics override
                        transform.position = attachWorldPos;
                        if (_body != null)
                        {
                            _body.position = attachWorldPos;
                            _body.velocity = Vector3.zero;
                        }

                        Vector3 toCart = PendingCartAttach.transform.position - transform.position;
                        toCart.y = 0f;
                        if (toCart.sqrMagnitude > 0.01f)
                            transform.rotation = Quaternion.LookRotation(toCart.normalized);

                        FreezeTimer = 1f;
                        SetFollowTarget(PendingCartAttach.gameObject);
                        PendingCartAttach.Interact(PendingCartHumanoid, false, false);

                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Cart navigation arrived — snapped to {attachWorldPos:F2}, calling Interact");

                        PendingCartAttach = null;
                        PendingCartHumanoid = null;
                    }
                    else
                    {
                        MoveTo(dt, attachWorldPos, 1f, true);
                    }
                }
                return true;
            }

            // Move-to-position — walking to a player-directed ground point
            if (PendingMoveTarget.HasValue)
            {
                _pendingMoveTimeout -= dt;
                if (_pendingMoveTimeout <= 0f)
                {
                    CompanionsPlugin.Log.LogDebug("[AI] Move-to timed out — cancelling");
                    CancelMoveTarget();
                }
                else
                {
                    float distToMove = Vector3.Distance(transform.position, PendingMoveTarget.Value);
                    if (distToMove < 2f)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Move-to arrived at {PendingMoveTarget.Value:F1} — resuming follow");
                        CancelMoveTarget();
                    }
                    else
                    {
                        bool runToPoint = distToMove > 10f;
                        MoveToPoint(dt, PendingMoveTarget.Value, 1f, runToPoint);
                    }
                }
                return true;
            }

            // Deposit navigation — walk to chest, open, transfer one-by-one, close
            if (PendingDepositContainer != null)
            {
                _pendingDepositTimeout -= dt;
                if (_pendingDepositTimeout <= 0f || PendingDepositContainer == null)
                {
                    CompanionsPlugin.Log.LogDebug("[AI] Deposit navigation timed out — cancelling");
                    CancelPendingDeposit();
                }
                else if (_depositChestOpen)
                {
                    // Phase 2: chest is open — transfer items one at a time
                    StopMoving();
                    LookAtPoint(PendingDepositContainer.transform.position);

                    _depositWorkTimer -= dt;
                    if (_depositWorkTimer <= 0f && _depositQueue.Count > 0)
                    {
                        _depositWorkTimer = DepositItemInterval;

                        var item = _depositQueue[0];
                        _depositQueue.RemoveAt(0);

                        var chestInv = PendingDepositContainer.GetInventory();
                        var compInv = PendingDepositHumanoid?.GetInventory();
                        if (chestInv != null && compInv != null && compInv.ContainsItem(item))
                        {
                            if (chestInv.AddItem(item))
                            {
                                compInv.RemoveItem(item);
                                _depositCount++;
                            }
                        }
                    }

                    // All items transferred — close chest and finish
                    if (_depositQueue.Count == 0)
                    {
                        PendingDepositContainer.SetInUse(false);
                        _depositChestOpen = false;

                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Deposit complete — {_depositCount} item(s) into \"{PendingDepositContainer.m_name}\"");

                        if (_talk != null)
                            _talk.Say(_depositCount > 0 ? ModLocalization.Loc("hc_speech_deposit_done") : ModLocalization.Loc("hc_speech_deposit_empty"));

                        CancelPendingDeposit();
                    }
                }
                else
                {
                    // Phase 1: walking to chest
                    Vector3 chestPos = PendingDepositContainer.transform.position;
                    float distToChest = Vector3.Distance(transform.position, chestPos);

                    if (distToChest < 2f)
                    {
                        // Arrived — open chest, build transfer queue
                        StopMoving();
                        LookAtPoint(chestPos);

                        PendingDepositContainer.SetInUse(true);
                        _depositChestOpen = true;
                        _depositCount = 0;
                        _depositWorkTimer = DepositItemInterval; // initial delay before first item

                        // Build deposit queue
                        _depositQueue.Clear();
                        var depositHumanoid = PendingDepositHumanoid;
                        if (depositHumanoid != null)
                        {
                            var compInv = depositHumanoid.GetInventory();
                            if (compInv != null)
                            {
                                foreach (var item in compInv.GetAllItems())
                                {
                                    if (!DirectedTargetPatch.ShouldKeep(item, depositHumanoid))
                                        _depositQueue.Add(item);
                                }
                            }
                        }

                        CompanionsPlugin.Log.LogDebug(
                            $"[AI] Deposit opened \"{PendingDepositContainer.m_name}\" — {_depositQueue.Count} items to transfer");

                        // If nothing to deposit, close immediately
                        if (_depositQueue.Count == 0)
                        {
                            PendingDepositContainer.SetInUse(false);
                            _depositChestOpen = false;

                            if (_talk != null)
                                _talk.Say(ModLocalization.Loc("hc_speech_deposit_empty"));

                            CancelPendingDeposit();
                        }
                    }
                    else
                    {
                        bool runToChest = distToChest > 10f;
                        MoveToPoint(dt, chestPos, 1.5f, runToChest);
                    }
                }
                return true;
            }

            // Tombstone recovery — navigate to and loot our tombstone after respawn
            if (UpdateTombstoneRecovery(dt))
                return true;

            // Directed target lock countdown
            if (DirectedTargetLockTimer > 0f)
                DirectedTargetLockTimer -= dt;

            // Read combat stance
            int stance = _setup != null ? _setup.GetCombatStance() : CompanionSetup.StanceBalanced;

            // Passive stance: never target, never attack, just follow
            if (stance == CompanionSetup.StancePassive)
            {
                ClearTargets();
                if (IsAlerted()) SetAlerted(false);
                SuppressAttack = true;

                // Repair/Restock modes also run in passive stance — dispatch FIRST
                // so the state machine advances before the controller-active guard
                var passiveZdo = m_nview?.GetZDO();
                if (passiveZdo != null)
                {
                    int passiveMode = passiveZdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                    if (passiveMode == CompanionSetup.ModeRepairBuildings)
                        UpdateRepairBuildingsMode(dt);
                    else if (passiveMode == CompanionSetup.ModeRestock)
                        UpdateRestockMode(dt);
                }

                if ((_harvest != null && _harvest.IsActive) ||
                    (_repair != null && _repair.IsActive) ||
                    (_smelt != null && _smelt.IsActive) ||
                    (_farm != null && _farm.IsActive) ||
                    (_homestead != null && _homestead.IsActive) ||
                    (_doorHandler != null && _doorHandler.IsActive) ||
                    IsRepairBuildActive ||
                    IsRestockActive)
                    return true;

                if (m_follow != null)
                    FollowWithFormation(m_follow, dt);
                else
                {
                    if (!EnforceHomePatrol(dt))
                        IdleMovement(dt);
                }
                return true;
            }

            // Clear SuppressAttack if we just left Passive stance
            // (Passive sets it true; CombatController only clears it during active combat)
            if (SuppressAttack && m_targetCreature == null)
                SuppressAttack = false;

            // Humanoid ref needed by heal check and combat below
            Humanoid humanoid = m_character as Humanoid;

            // ── Support Dverger healing — prioritize hurt allies over enemies ──
            if (_isDvergerSupport && humanoid != null)
            {
                _healScanTimer -= dt;
                Character prevHealTarget = _healTarget;
                if (_healScanTimer <= 0f)
                {
                    _healScanTimer = HealScanInterval;
                    _healTarget = FindHurtAlly();
                }

                // Invalidate stale target
                if (_healTarget != null)
                {
                    float htMax = _healTarget.GetMaxHealth();
                    if (_healTarget.IsDead() || htMax <= 0f ||
                        _healTarget.GetHealth() / htMax >= HealThreshold)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[CompanionAI:Heal] Target \"{_healTarget.m_name}\" recovered or died — clearing");
                        _healTarget = null;
                    }
                }

                // Log target transitions
                if (_healTarget != prevHealTarget)
                {
                    if (_healTarget != null)
                    {
                        float htMax2 = _healTarget.GetMaxHealth();
                        float htPct = htMax2 > 0f ? _healTarget.GetHealth() / htMax2 * 100f : 0f;
                        CompanionsPlugin.Log.LogDebug(
                            $"[CompanionAI:Heal] New heal target \"{_healTarget.m_name}\" " +
                            $"HP={_healTarget.GetHealth():F0}/{htMax2:F0} ({htPct:F0}%)");
                    }
                    else if (prevHealTarget != null)
                        CompanionsPlugin.Log.LogDebug("[CompanionAI:Heal] No hurt allies in range — resuming combat");
                }

                if (_healTarget != null)
                {
                    // Equip heal staff (FriendHurt weapon) periodically
                    _healEquipTimer -= dt;
                    if (_healEquipTimer <= 0f && !m_character.InAttack())
                    {
                        _healEquipTimer = 1f;
                        var prevWeapon = humanoid.GetCurrentWeapon();
                        humanoid.EquipBestWeapon(null, null, _healTarget, null);
                        var newWeapon = humanoid.GetCurrentWeapon();
                        if (prevWeapon != newWeapon)
                            CompanionsPlugin.Log.LogDebug(
                                $"[CompanionAI:Heal] Weapon switch \"{prevWeapon?.m_shared?.m_name ?? "none"}\" " +
                                $"→ \"{newWeapon?.m_shared?.m_name ?? "none"}\"");
                    }

                    UpdateHealBehavior(humanoid, dt);
                    return true;
                }
                else
                {
                    // No hurt ally — re-equip enemy weapon if needed
                    _healEquipTimer -= dt;
                    if (_healEquipTimer <= 0f && !m_character.InAttack())
                    {
                        _healEquipTimer = 1f;
                        var prevWeapon = humanoid.GetCurrentWeapon();
                        humanoid.EquipBestWeapon(m_targetCreature, m_targetStatic, null, null);
                        var newWeapon = humanoid.GetCurrentWeapon();
                        if (prevWeapon != newWeapon)
                            CompanionsPlugin.Log.LogDebug(
                                $"[CompanionAI:Heal] Weapon switch back \"{prevWeapon?.m_shared?.m_name ?? "none"}\" " +
                                $"→ \"{newWeapon?.m_shared?.m_name ?? "none"}\"");
                    }
                }
            }

            // Target acquisition with companion-specific suppression
            UpdateTarget(humanoid, dt, stance);

            // Debug logging (replaces TargetPatches.UpdateAI_DebugLog)
            UpdateDebugLog(dt);

            // No combat target — follow or idle
            if (m_targetCreature == null && m_targetStatic == null)
            {
                // Repair Buildings / Restock modes — dispatch state machine FIRST
                // so the state machine advances before the controller-active guard
                // (otherwise IsRepairBuildActive/IsRestockActive early-returns before
                // the state machine gets its dt tick → companion freezes)
                var zdo = m_nview?.GetZDO();
                if (zdo != null)
                {
                    int actionMode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                    if (actionMode == CompanionSetup.ModeRepairBuildings)
                        UpdateRepairBuildingsMode(dt);
                    else if (actionMode == CompanionSetup.ModeRestock)
                        UpdateRestockMode(dt);
                }

                // When a controller is actively moving to a target, it owns
                // movement exclusively. Letting Follow() or IdleMovement() run
                // here causes dual-control jitter — Follow's internal stop
                // distance (~3m) cancels the controller's movement commands.
                if ((_harvest != null && _harvest.IsActive) ||
                    (_repair != null && _repair.IsActive) ||
                    (_smelt != null && _smelt.IsActive) ||
                    (_farm != null && _farm.IsActive) ||
                    (_homestead != null && _homestead.IsActive) ||
                    (_doorHandler != null && _doorHandler.IsActive) ||
                    IsRepairBuildActive ||
                    IsRestockActive)
                    return true;

                if (m_follow != null)
                    FollowWithFormation(m_follow, dt);
                else
                {
                    if (!EnforceHomePatrol(dt))
                        IdleMovement(dt);
                }
                return true;
            }

            // Has target — combat movement + attack (clear sneak/walk overrides)
            ClearFollowMovementOverrides();
            if (humanoid != null)
                UpdateCombat(humanoid, dt, stance);

            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Target Acquisition
        //  Replaces MonsterAI.UpdateTarget + TargetPatches
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateTarget(Humanoid humanoid, float dt, int stance = CompanionSetup.StanceBalanced)
        {
            m_updateTargetTimer -= dt;

            // ── UI open → suppress targeting completely ──
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup))
            {
                ThrottledTargetLog("UIOpen");
                ClearTargets();
                return;
            }

            // ── StayHome mode → only fight if physically hit (self-defense) ──
            bool stayHome = _setup != null && _setup.GetStayHome()
                         && _setup.HasHomePosition() && !_setup.GetFollow();
            if (stayHome)
            {
                if (m_timeSinceHurt > 10f)
                {
                    ThrottledTargetLog("StayHome(peaceful)");
                    ClearTargets();
                    if (IsAlerted()) SetAlerted(false);
                    return;
                }
                // Recently hit — allow targeting for self-defense
            }

            // ── Harvest mode → suppress unless enemy nearby (self-defense) ──
            bool gathering = _harvest != null && _harvest.IsInGatherMode;

            if (gathering)
            {
                bool enemyNearby = false;
                Character nearestEnemy = null;
                float nearestDist = float.MaxValue;

                foreach (Character c in Character.GetAllCharacters())
                {
                    if (c == m_character || c.IsDead()) continue;
                    if (c.GetHealth() <= 0f) continue;
                    if (!IsEnemy(c)) continue;
                    float d = Vector3.Distance(transform.position, c.transform.position);
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

                if (!enemyNearby)
                {
                    ThrottledTargetLog("Gathering(safe)");
                    ClearTargets();
                    if (IsAlerted()) SetAlerted(false);
                    return;
                }

                // Enemy nearby — log and allow targeting for self-defense
                _targetLogTimer -= dt;
                if (_targetLogTimer <= 0f)
                {
                    _targetLogTimer = 2f;
                    CompanionsPlugin.Log.LogDebug(
                        $"[CompanionAI] SELF-DEFENSE ALLOW — nearest enemy \"{nearestEnemy?.m_name ?? "?"}\" " +
                        $"at {nearestDist:F1}m — currentTarget=\"{m_targetCreature?.m_name ?? "null"}\"");
                }
            }

            // ── Directed target lock — player pressed hotkey, hold this target ──
            if (DirectedTargetLockTimer > 0f && m_targetCreature != null &&
                !m_targetCreature.IsDead() && m_targetCreature.GetHealth() > 0f)
            {
                // Skip FindEnemy — keep directed target
                m_updateTargetTimer = Mathf.Max(m_updateTargetTimer, 1f);
            }
            // ── Target stickiness — finish current enemy before switching ──
            else if (m_targetCreature != null && !m_targetCreature.IsDead() &&
                     m_targetCreature.GetHealth() > 0f)
            {
                // Already have a valid, living target — don't search for another.
                // This prevents bouncing between multiple enemies. The companion
                // commits to one target until it dies, escapes (give-up timer),
                // or the player directs a new target via hotkey.
            }
            // ── Timer-based target scan — only when we have no target ──
            else if (m_updateTargetTimer <= 0f && !m_character.InAttack())
            {
                bool playerNear = Player.IsPlayerInRange(transform.position, 50f);
                m_updateTargetTimer = playerNear ? UpdateTargetIntervalNear : UpdateTargetIntervalFar;

                // Aggressive: boost view range for wider scan
                float savedRange = m_viewRange;
                if (stance == CompanionSetup.StanceAggressive)
                    m_viewRange = Mathf.Max(m_viewRange, 50f);

                Character enemy = FindEnemy();

                if (stance == CompanionSetup.StanceAggressive)
                    m_viewRange = savedRange;

                if (enemy != null)
                {
                    // Defensive: only engage enemies targeting the companion or the player
                    // (pure self-defense + player protection — never initiate aggression)
                    if (stance == CompanionSetup.StanceDefensive)
                    {
                        bool threatsUs = false;
                        var eAI = enemy.GetBaseAI();
                        if (eAI != null)
                        {
                            var aiTarget = eAI.GetTargetCreature();
                            if (aiTarget != null && (aiTarget == m_character || aiTarget.IsPlayer()))
                                threatsUs = true;
                        }
                        if (!threatsUs)
                            enemy = null;
                    }

                    if (enemy != null)
                    {
                        m_targetCreature = enemy;
                        m_targetStatic = null;
                        if (stance == CompanionSetup.StanceAggressive)
                            SetAlerted(true);
                    }
                }
            }

            // ── Alert range check (leash to follow target / home) ──
            if (m_targetCreature != null)
            {
                // StayHome companions: drop targets beyond 50m from home so they
                // don't chase enemies across the map. The hard teleport in UpdateAI
                // is a safety net; this prevents the chase from starting.
                bool stayHomeLeash = _setup != null && _setup.GetStayHome()
                    && _setup.HasHomePosition() && !_setup.GetFollow();
                if (stayHomeLeash)
                {
                    float enemyDistFromHome = Utils.DistanceXZ(
                        m_targetCreature.transform.position, _setup.GetHomePosition());
                    if (enemyDistFromHome > CompanionSetup.MaxLeashDistance)
                    {
                        m_targetCreature = null;
                    }
                }

                if (m_targetCreature != null && GetPatrolPoint(out var point))
                {
                    if (Vector3.Distance(m_targetCreature.transform.position, point) > AlertRange)
                        m_targetCreature = null;
                }
                else if (m_targetCreature != null && m_follow != null &&
                         Vector3.Distance(m_targetCreature.transform.position,
                             m_follow.transform.position) > AlertRange)
                {
                    m_targetCreature = null;
                }
            }

            // ── Target validation ──
            if (m_targetCreature != null)
            {
                if (m_targetCreature.IsDead())
                    m_targetCreature = null;
                else if (!IsEnemy(m_targetCreature))
                    m_targetCreature = null;
            }

            // ── Sense tracking ──
            bool canHear = false;
            bool canSee = false;
            if (m_targetCreature != null)
            {
                canHear = CanHearTarget(m_targetCreature);
                canSee = CanSeeTarget(m_targetCreature);
                if (canSee || canHear)
                    m_timeSinceSensedTargetCreature = 0f;

                if (m_targetCreature.IsPlayer())
                    m_targetCreature.OnTargeted(canSee || canHear, IsAlerted());

                SetTargetInfo(m_targetCreature.GetZDOID());
            }
            else
            {
                SetTargetInfo(ZDOID.None);
            }

            // ── Give-up timer ──
            m_timeSinceSensedTargetCreature += dt;
            if (IsAlerted() || m_targetCreature != null)
            {
                m_timeSinceAttacking += dt;
                if (m_timeSinceSensedTargetCreature > GiveUpTime ||
                    m_timeSinceAttacking > 60f)
                {
                    SetAlerted(false);
                    m_targetCreature = null;
                    m_targetStatic = null;
                    m_timeSinceAttacking = 0f;
                    m_timeSinceSensedTargetCreature = 0f;
                    m_updateTargetTimer = 5f;
                }
            }
        }

        private float _suppressLogTimer;
        private void ThrottledTargetLog(string reason)
        {
            _suppressLogTimer -= Time.deltaTime;
            if (_suppressLogTimer <= 0f)
            {
                _suppressLogTimer = 3f;
                CompanionsPlugin.Log.LogDebug(
                    $"[CompanionAI] Suppressing targeting — reason={reason}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Combat Movement + Attack
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCombat(Humanoid humanoid, float dt, int stance = CompanionSetup.StanceBalanced)
        {
            if (m_targetCreature == null) return;

            // CombatController owns movement during retreat — don't fight it
            // with approach movement. Only keep the attack check alive so the
            // companion can still swing if an enemy walks into range.
            if (_combat != null && _combat.Phase == CombatController.CombatPhase.Retreat)
            {
                // Still allow attacks on enemies that wander into melee range
                var retreatWeapon = humanoid.GetCurrentWeapon();
                if (retreatWeapon != null)
                {
                    float retDist = Vector3.Distance(m_targetCreature.transform.position, transform.position)
                                    - m_targetCreature.GetRadius();
                    bool retInRange = retDist < retreatWeapon.m_shared.m_aiAttackRange;
                    if (retInRange && CanSeeTarget(m_targetCreature) && IsAlerted()
                        && Time.time >= _attackRetryTime)
                    {
                        LookAt(m_targetCreature.GetTopPoint());
                        if (CanAttackNow(retreatWeapon) && IsLookingAt(m_lastKnownTargetPos,
                                retreatWeapon.m_shared.m_aiAttackMaxAngle,
                                retreatWeapon.m_shared.m_aiInvertAngleCheck))
                        {
                            if (!DoAttack(m_targetCreature))
                                _attackRetryTime = Time.time + 0.5f;
                        }
                    }
                }
                return;
            }

            ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();

            // If holding unarmed weapon or nothing, try to equip a real weapon first
            bool isUnarmed = weapon != null && humanoid.m_unarmedWeapon != null &&
                             weapon == humanoid.m_unarmedWeapon.m_itemData;
            if (weapon == null || isUnarmed)
            {
                if (_setup != null) _setup.SuppressAutoEquip = false;
                humanoid.EquipBestWeapon(m_targetCreature, m_targetStatic, null, null);
                weapon = humanoid.GetCurrentWeapon();
            }

            if (weapon == null)
            {
                // Still no weapon after equip attempt — just follow
                if (m_follow != null) Follow(m_follow, dt);
                else IdleMovement(dt);
                return;
            }

            bool canHear = CanHearTarget(m_targetCreature);
            bool canSee = CanSeeTarget(m_targetCreature);

            if (canHear || canSee)
            {
                m_beenAtLastPos = false;
                m_lastKnownTargetPos = m_targetCreature.transform.position;

                float dist = Vector3.Distance(m_lastKnownTargetPos, transform.position)
                             - m_targetCreature.GetRadius();
                float alertRange = AlertRange * m_targetCreature.GetStealthFactor();

                if (canSee && dist < alertRange)
                    SetAlerted(true);

                float weaponRange = weapon.m_shared.m_aiAttackRange;
                bool inAttackRange = dist < weaponRange;

                // Stop range: keep closing distance past weaponRange to ensure
                // melee swings actually connect. The AI's m_aiAttackRange is
                // center-to-surface, but the melee sphere-cast originates from
                // the weapon bone (offset from center), so stopping at
                // weaponRange often leaves the companion swinging at air.
                float stopRange = Mathf.Max(weaponRange * 0.5f, 1.5f);

                // Hysteresis: once in stop range, require a larger distance
                // before moving again. Prevents oscillation at boundary
                // (which causes visible jitter when stuck at stop range).
                float stopThreshold = _inCombatStopRange
                    ? stopRange + 0.3f
                    : stopRange;
                bool shouldStop = dist < stopThreshold && canSee && IsAlerted();

                if (!shouldStop)
                {
                    _inCombatStopRange = false;

                    // Not close enough to stop — move toward target
                    Vector3 moveTarget = m_lastKnownTargetPos;

                    // Flanking: approach from opposite side of player
                    if (stance != CompanionSetup.StancePassive &&
                        stance != CompanionSetup.StanceDefensive &&
                        m_follow != null && dist > weaponRange * 2f)
                    {
                        float playerToTarget = Vector3.Distance(m_follow.transform.position, m_lastKnownTargetPos);
                        if (playerToTarget < 15f && playerToTarget > 1f)
                        {
                            Vector3 behindTarget = (m_lastKnownTargetPos - m_follow.transform.position).normalized;
                            float flankDist = stance == CompanionSetup.StanceAggressive
                                ? weaponRange * 0.5f
                                : weaponRange;
                            moveTarget = m_lastKnownTargetPos + behindTarget * flankDist;
                        }
                    }

                    // Combat stuck detection: if velocity stays low while trying to
                    // reach the enemy, the path is blocked. Try flanking offsets to
                    // find a way around the obstacle, then disengage if all fail.
                    float combatVel = m_character.GetVelocity().magnitude;
                    if (combatVel < GroundStuckVelThreshold && !m_character.InAttack())
                    {
                        _combatMoveStuckTimer += dt;
                        if (_combatMoveStuckTimer > 1.5f)
                        {
                            _combatMoveStuckTimer = 0f;

                            // Try perpendicular flanking offset (alternating left/right)
                            Vector3 toEnemy = (m_lastKnownTargetPos - transform.position);
                            toEnemy.y = 0f;
                            if (toEnemy.sqrMagnitude > 0.01f)
                            {
                                toEnemy.Normalize();
                                // Alternate left and right on successive stuck detections
                                float flankSign = (_groundStuckAttempts % 2 == 0) ? 1f : -1f;
                                Vector3 perpendicular = new Vector3(-toEnemy.z, 0f, toEnemy.x) * flankSign;
                                moveTarget = transform.position + perpendicular * 4f + toEnemy * 2f;

                                CompanionsPlugin.Log.LogDebug(
                                    $"[AI:Combat] Stuck moving to enemy — flanking " +
                                    $"(side={flankSign:F0}) pos={transform.position:F1} " +
                                    $"target=\"{m_targetCreature?.m_name ?? "?"}\" dist={dist:F1}");
                            }
                        }
                    }
                    else
                    {
                        _combatMoveStuckTimer = 0f;
                    }

                    // Stamina-aware approach: walk when stamina is low to
                    // allow regen. Running at 0 stamina is wasteful and leads
                    // to the retreat-loop (drain > regen → never recover).
                    bool runToTarget = IsAlerted();
                    if (_companionStamina != null && _companionStamina.GetStaminaPercentage() < 0.25f)
                        runToTarget = false;
                    MoveToPoint(dt, moveTarget, 0f, runToTarget);
                }
                else
                {
                    _inCombatStopRange = true;

                    // Close enough — stop and face target
                    StopMoving();
                }

                // Attack if in weapon range, can see, and alerted
                if (inAttackRange && canSee && IsAlerted())
                {
                    LookAt(m_targetCreature.GetTopPoint());

                    // Retry cooldown: when DoAttack fails (e.g. insufficient
                    // stamina), wait before retrying to prevent per-frame
                    // attack spam that causes jitter and log noise.
                    if (Time.time >= _attackRetryTime)
                    {
                        bool canAttack = CanAttackNow(weapon);
                        if (canAttack && IsLookingAt(m_lastKnownTargetPos,
                                weapon.m_shared.m_aiAttackMaxAngle,
                                weapon.m_shared.m_aiInvertAngleCheck))
                        {
                            if (!DoAttack(m_targetCreature))
                                _attackRetryTime = Time.time + 0.5f;
                        }
                    }
                }
            }
            else
            {
                // Lost sight — search last known position
                if (m_beenAtLastPos)
                {
                    RandomMovement(dt, m_lastKnownTargetPos);
                }
                else if (MoveToPoint(dt, m_lastKnownTargetPos, 0f, IsAlerted()))
                {
                    m_beenAtLastPos = true;
                }
            }
        }

        private bool CanAttackNow(ItemDrop.ItemData weapon)
        {
            if (weapon == null) return false;
            bool intervalOk = Time.time - weapon.m_lastAttackTime > weapon.m_shared.m_aiAttackInterval;
            return intervalOk && CanUseAttack(weapon) && !IsTakingOff();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  DoAttack — replaces MonsterAI.DoAttack + CombatPatches.DoAttack_Patch
        // ══════════════════════════════════════════════════════════════════════

        private bool DoAttack(Character target)
        {
            // CombatController blocking — suppress attack
            if (SuppressAttack) return false;

            Humanoid humanoid = m_character as Humanoid;
            if (humanoid == null) return false;

            ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
            if (weapon == null || !CanUseAttack(weapon)) return false;

            // Power attack on staggered target
            bool secondary = target != null && target.IsStaggering()
                             && weapon.HaveSecondaryAttack();

            bool success = m_character.StartAttack(target, secondary);
            if (success)
            {
                m_timeSinceAttacking = 0f;

                if (secondary)
                    CompanionsPlugin.Log.LogDebug(
                        $"[CompanionAI] Power attack on staggered \"{target.m_name}\"");
            }

            return success;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Auto-Pickup (passive item collection)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Passively picks up nearby items, same as the Player's auto-pickup.
        /// Items are pulled toward the companion and picked up when very close.
        /// </summary>
        private void AutoPickup(float dt)
        {
            var humanoid = m_character as Humanoid;
            if (humanoid == null) return;

            var inv = humanoid.GetInventory();
            if (inv == null) return;

            Vector3 center = transform.position + Vector3.up;
            int count = Physics.OverlapSphereNonAlloc(
                center, AutoPickupRange, _autoPickupBuffer, _autoPickupMask);

            for (int i = 0; i < count; i++)
            {
                var col = _autoPickupBuffer[i];
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null || !itemDrop.m_autoPickup) continue;

                var nview = itemDrop.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                if (!itemDrop.CanPickup())
                {
                    itemDrop.RequestOwn();
                    continue;
                }

                if (itemDrop.InTar()) continue;

                itemDrop.Load();
                string itemName = itemDrop.m_itemData?.m_shared?.m_name ?? "?";

                if (!inv.CanAddItem(itemDrop.m_itemData))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[AutoPickup] Skipping \"{itemName}\" — inventory full");
                    continue;
                }

                // Weight check
                float newWeight = inv.GetTotalWeight() + itemDrop.m_itemData.GetWeight();
                if (newWeight > CompanionTierData.MaxCarryWeight)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[AutoPickup] Skipping \"{itemName}\" — would exceed carry weight " +
                        $"({newWeight:F1} > {CompanionTierData.MaxCarryWeight})");
                    continue;
                }

                float dist = Vector3.Distance(itemDrop.transform.position, center);
                if (dist < 0.3f)
                {
                    bool picked = humanoid.Pickup(itemDrop.gameObject, false, false);
                    if (picked)
                        CompanionsPlugin.Log.LogDebug(
                            $"[AutoPickup] Picked up \"{itemName}\" x{itemDrop.m_itemData?.m_stack ?? 1}");
                }
                else
                {
                    // Pull item toward companion
                    Vector3 dir = (center - itemDrop.transform.position).normalized;
                    itemDrop.transform.position += dir * AutoPickupPullSpeed * dt;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Drowning — damage when swimming with no stamina
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateDrowning(float dt)
        {
            if (!m_character.IsSwimming())
            {
                _drownDamageTimer = 0f;
                return;
            }

            if (_companionStamina == null || _companionStamina.Stamina > 0f)
            {
                _drownDamageTimer = 0f;
                return;
            }

            // Accumulate timer — deal damage every 1 second (matches vanilla Player)
            _drownDamageTimer += dt;
            if (_drownDamageTimer > 1f)
            {
                _drownDamageTimer = 0f;

                // 5% of max health per tick, rounded up (same as Player)
                float damage = Mathf.Ceil(m_character.GetMaxHealth() / 20f);
                HitData hitData = new HitData();
                hitData.m_damage.m_damage = damage;
                hitData.m_point = m_character.GetCenterPoint();
                hitData.m_dir = Vector3.down;
                hitData.m_pushForce = 10f;
                hitData.m_hitType = HitData.HitType.Drowning;
                m_character.Damage(hitData);

                // Play drowning effects (sound + splash) at water surface
                if (DrownEffects != null)
                {
                    Vector3 pos = transform.position;
                    pos.y = m_character.GetLiquidLevel();
                    DrownEffects.Create(pos, transform.rotation);
                }

                CompanionsPlugin.Log.LogDebug(
                    $"[AI] Drowning — dealt {damage:F1} damage " +
                    $"(hp={m_character.GetHealth():F1}/{m_character.GetMaxHealth():F1}) " +
                    $"companion=\"{m_character.m_name}\"");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Hazard Recovery — tar pits and water
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Proactive hazard recovery. Detects dangerous conditions and moves toward
        /// solid ground BEFORE the companion dies:
        ///
        /// Triggers:
        /// - In tar (always dangerous)
        /// - Swimming AND stamina below 30% (drowning risk)
        /// - Swimming AND health below 50% (taking heavy damage)
        /// - Swimming for 5+ continuous seconds without a follow target in water
        ///
        /// Uses multi-radius land sampling (5m/10m/20m × 12 directions) to find
        /// the closest shore rather than just the highest nearby point.
        /// </summary>
        private bool UpdateHazardRecovery(float dt)
        {
            bool inTar = m_character.GetSEMan().HaveStatusEffect(s_taredHash);
            bool isSwimming = m_character.IsSwimming();

            // Proactive water escape — trigger BEFORE stamina runs out
            bool waterDanger = false;
            string waterReason = null;
            if (isSwimming && _companionStamina != null)
            {
                float staminaPct = _companionStamina.MaxStamina > 0f
                    ? _companionStamina.Stamina / _companionStamina.MaxStamina : 0f;
                float healthPct = m_character.GetMaxHealth() > 0f
                    ? m_character.GetHealth() / m_character.GetMaxHealth() : 0f;

                if (_companionStamina.Stamina <= 0f)
                {
                    waterDanger = true;
                    waterReason = "DROWNING(no stamina)";
                }
                else if (staminaPct < 0.5f)
                {
                    waterDanger = true;
                    waterReason = $"LOW_STAMINA({staminaPct*100:F0}%)";
                }
                else if (healthPct < 0.5f)
                {
                    waterDanger = true;
                    waterReason = $"LOW_HEALTH({healthPct*100:F0}%)";
                }
                else if (_continuousSwimTimer > 3f)
                {
                    // Swimming too long — check if follow target is also in water
                    bool followInWater = false;
                    if (m_follow != null)
                    {
                        var followChar = m_follow.GetComponent<Character>();
                        if (followChar != null && followChar.IsSwimming())
                            followInWater = true;
                    }
                    if (!followInWater)
                    {
                        waterDanger = true;
                        waterReason = $"SWIM_TIMEOUT({_continuousSwimTimer:F1}s)";
                    }
                }
            }

            if (!inTar && !waterDanger)
            {
                _hazardRecoveryTimer = 0f;
                return false;
            }

            _hazardRecoveryTimer += dt;

            // Log periodically (every 2s)
            if (_hazardRecoveryTimer < 0.1f || (int)(_hazardRecoveryTimer * 10) % 20 == 0)
            {
                string hazard = inTar ? "TAR" : waterReason;
                CompanionsPlugin.Log.LogDebug(
                    $"[AI] Hazard recovery — {hazard} — seeking land " +
                    $"(t={_hazardRecoveryTimer:F1}s swimT={_continuousSwimTimer:F1}s) " +
                    $"companion=\"{m_character.m_name}\"");
            }

            // Disengage combat — survival takes priority
            if (m_targetCreature != null)
            {
                ClearTargets();
                if (_combat != null) _combat.ForceExitCombat();
            }

            // Multi-radius land sampling: 3 radii × 12 directions = 36 samples.
            // Pick the CLOSEST valid land (solid ground above water level).
            Vector3 bestDir = Vector3.zero;
            float bestDist = float.MaxValue;
            Vector3 myPos = transform.position;
            float waterLevel = m_character.GetLiquidLevel();

            float[] radii = { 5f, 10f, 20f };
            for (int r = 0; r < radii.Length; r++)
            {
                float radius = radii[r];
                for (int i = 0; i < 12; i++)
                {
                    float angle = i * 30f;
                    Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                    Vector3 samplePos = myPos + dir * radius;

                    if (ZoneSystem.instance != null)
                    {
                        float h = ZoneSystem.instance.GetSolidHeight(samplePos);
                        // Valid land: solid height above water level (or above 30f as absolute minimum)
                        float minHeight = Mathf.Max(waterLevel, 30f);
                        if (h > minHeight)
                        {
                            float dist = Vector3.Distance(myPos, samplePos);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestDir = dir;
                            }
                        }
                    }
                }
                // If we found land at this radius, don't check further (prefer closest)
                if (bestDir != Vector3.zero) break;
            }

            if (bestDir != Vector3.zero)
            {
                MoveTowards(bestDir, true);
            }
            else if (m_follow != null)
            {
                // Fallback: move toward follow target (player is probably on land)
                Vector3 toFollow = (m_follow.transform.position - myPos).normalized;
                MoveTowards(toFollow, true);
            }

            // Teleport timeout — shorter when actively drowning (taking damage)
            bool activeDrowning = isSwimming && _companionStamina != null
                                  && _companionStamina.Stamina <= 0f;
            float teleportTimeout = activeDrowning ? 6f : 10f;

            if (_hazardRecoveryTimer > teleportTimeout && m_follow != null
                && _setup != null && _setup.GetFollow())
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[AI] Hazard recovery timeout ({teleportTimeout:F0}s) — " +
                    $"teleporting to player. companion=\"{m_character.m_name}\"");
                TeleportToFollowTarget();
                _hazardRecoveryTimer = 0f;
                _continuousSwimTimer = 0f;
            }

            return true; // consumed the AI tick
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Proactive Water Avoidance
        // ══════════════════════════════════════════════════════════════════════

        private float _waterAvoidLogTimer;

        /// <summary>
        /// Prevents the companion from walking into water when stamina is too low
        /// to survive swimming. Samples terrain 3m ahead in movement direction —
        /// if it's below water level and stamina is under 50%, stops movement.
        /// HumanoidAvoidWater pathfinding handles routing around water normally;
        /// this is a safety net for edge cases (direct MoveTowards, physics push).
        /// Returns true if movement was blocked.
        /// </summary>
        private bool UpdateWaterAvoidance(float dt)
        {
            if (m_character.IsSwimming()) return false; // handled by hazard recovery
            if (!m_character.IsOnGround()) return false;
            if (ZoneSystem.instance == null) return false;

            // Only check when actually moving
            Vector3 vel = m_character.GetVelocity();
            if (vel.magnitude < 0.3f) return false;

            // Sample terrain 3m ahead in movement direction
            Vector3 ahead = transform.position + vel.normalized * 3f;
            float solidH = ZoneSystem.instance.GetSolidHeight(ahead);
            if (solidH >= 30f) return false; // dry land ahead — 30f is sea level

            // Water ahead — check stamina
            float staminaPct = _companionStamina != null && _companionStamina.MaxStamina > 0f
                ? _companionStamina.Stamina / _companionStamina.MaxStamina : 1f;

            if (staminaPct < 0.5f)
            {
                StopMoving();
                _waterAvoidLogTimer -= dt;
                if (_waterAvoidLogTimer <= 0f)
                {
                    _waterAvoidLogTimer = 3f;
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI] Water ahead with low stamina ({staminaPct * 100:F0}%) — " +
                        $"refusing to enter. pos={transform.position:F1} solidH={solidH:F1}");
                }
                return true;
            }

            _waterAvoidLogTimer = 0f;
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Follow Teleport
        // ══════════════════════════════════════════════════════════════════════

        private void TeleportToFollowTarget()
        {
            if (m_follow == null) return;
            Vector3 targetPos = m_follow.transform.position;

            // Find a valid nearby position using ground height
            Vector3 spawnPos = targetPos + Vector3.right * 2f;
            for (int i = 0; i < 20; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * 3f;
                Vector3 candidate = targetPos + new Vector3(rnd.x, 0f, rnd.y);
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(candidate, out float height))
                {
                    candidate.y = height;
                    spawnPos = candidate;
                    break;
                }
            }

            transform.position = spawnPos;
            var body = GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = spawnPos;
                body.linearVelocity = Vector3.zero;
            }

            // Sync position to ZDO so other clients see the teleport
            if (m_nview?.GetZDO() != null)
                m_nview.GetZDO().SetPosition(spawnPos);

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Teleported companion to follow target at {spawnPos:F1} " +
                $"(was {FollowTeleportDist:F0}m+ away)");
        }

        /// <summary>
        /// Teleport companion back to home position, clear combat targets,
        /// and restore home patrol. Used when StayHome companion strays too far.
        /// </summary>
        private void TeleportToHome()
        {
            if (_setup == null || !_setup.HasHomePosition()) return;
            Vector3 homePos = _setup.GetHomePosition();

            // Find a valid nearby position
            Vector3 spawnPos = homePos;
            for (int i = 0; i < 20; i++)
            {
                Vector2 rnd = UnityEngine.Random.insideUnitCircle * 3f;
                Vector3 candidate = homePos + new Vector3(rnd.x, 0f, rnd.y);
                if (ZoneSystem.instance != null &&
                    ZoneSystem.instance.FindFloor(candidate, out float height))
                {
                    candidate.y = height;
                    spawnPos = candidate;
                    break;
                }
            }

            transform.position = spawnPos;
            var body = GetComponent<Rigidbody>();
            if (body != null)
            {
                body.position = spawnPos;
                body.linearVelocity = Vector3.zero;
            }

            // Sync position to ZDO so other clients see the teleport
            if (m_nview?.GetZDO() != null)
                m_nview.GetZDO().SetPosition(spawnPos);

            // Disengage from combat
            ClearTargets();
            if (IsAlerted()) SetAlerted(false);

            // Restore home patrol
            SetFollowTarget(null);
            SetPatrolPointAt(homePos);

            CompanionsPlugin.Log.LogInfo(
                $"[AI] StayHome leash — teleported companion back to home at {spawnPos:F1}");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Formation Following
        // ══════════════════════════════════════════════════════════════════════

        private void FollowWithFormation(GameObject target, float dt)
        {
            if (target == null) { IdleMovement(dt); return; }

            // Init formation slot from ZDO if not set
            if (_formationSlot < 0 && _setup != null)
                _formationSlot = _setup.AssignFormationSlot();

            float distToTarget = Vector3.Distance(transform.position, target.transform.position);

            // Detect player's movement state for matching
            var player = target.GetComponent<Player>();
            bool playerSneaking = player != null && player.IsCrouching();
            bool playerWalking  = player != null && player.GetWalk() && !player.IsRunning();
            bool playerRunning  = player != null && player.IsRunning();

            // Too far away → sprint to catch up, no movement matching
            if (distToTarget > FormationCatchupDist || _formationSlot < 0)
            {
                Follow(target, dt);
                ClearFollowMovementOverrides();
                return;
            }

            // Close to target → vanilla follow (stops at ~3m, no formation jitter)
            if (distToTarget < 5f)
            {
                Follow(target, dt);
                ApplyPlayerMovementMatch(playerSneaking, playerWalking, playerRunning);
                return;
            }

            // Compute formation offset relative to player's facing
            Vector3 playerFwd = target.transform.forward;
            Vector3 playerRight = target.transform.right;
            int stance = _setup != null ? _setup.GetCombatStance() : CompanionSetup.StanceBalanced;
            float offsetScale = stance == CompanionSetup.StanceDefensive ? 0.6f : 1f;
            Vector3 offset = GetFormationOffset(_formationSlot, playerFwd, playerRight) * offsetScale;

            Vector3 formationPos = target.transform.position + offset;

            float distToSlot = Vector3.Distance(transform.position, formationPos);

            // Within 2m of formation point → use vanilla follow for smooth behavior
            if (distToSlot < 2f)
            {
                Follow(target, dt);
                ApplyPlayerMovementMatch(playerSneaking, playerWalking, playerRunning);
                return;
            }

            // Move to formation point — match player speed when within formation range
            bool shouldRun;
            if (playerSneaking || playerWalking)
                shouldRun = false;
            else
                shouldRun = playerRunning || distToTarget > 10f;

            MoveToPoint(dt, formationPos, 1f, shouldRun);
            ApplyPlayerMovementMatch(playerSneaking, playerWalking, playerRunning);
        }

        /// <summary>
        /// Override the companion's walk/crouch state to match the player.
        /// Called AFTER Follow()/MoveTo() since those set run/moveDir.
        /// </summary>
        private void ApplyPlayerMovementMatch(bool sneak, bool walk, bool run)
        {
            if (sneak)
            {
                m_character.SetRun(false);
                m_character.SetWalk(true);
                if (_zanim != null) _zanim.SetBool(s_crouching, true);
            }
            else if (walk)
            {
                m_character.SetRun(false);
                m_character.SetWalk(true);
                if (_zanim != null) _zanim.SetBool(s_crouching, false);
            }
            else
            {
                m_character.SetWalk(false);
                if (_zanim != null) _zanim.SetBool(s_crouching, false);
            }
        }

        /// <summary>
        /// Clear walk/crouch overrides when catching up or entering combat.
        /// </summary>
        private void ClearFollowMovementOverrides()
        {
            m_character.SetWalk(false);
            if (_zanim != null) _zanim.SetBool(s_crouching, false);
        }

        private static Vector3 GetFormationOffset(int slot, Vector3 fwd, Vector3 right)
        {
            // Staggered positions behind and beside the player
            switch (slot)
            {
                case 0: return  right * FormationOffset - fwd * 1f;
                case 1: return -right * FormationOffset - fwd * 1f;
                case 2: return  right * (FormationOffset * 0.67f) - fwd * 3f;
                case 3: return -right * (FormationOffset * 0.67f) - fwd * 3f;
                default:
                    // Circular spread for 4+
                    float angle = (slot - 4) * 45f + 135f;
                    float rad = angle * Mathf.Deg2Rad;
                    return (right * Mathf.Cos(rad) + fwd * Mathf.Sin(rad)) * FormationOffset;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Heal Targeting (Support Dverger)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Scans for hurt player or player-owned companions within view range.
        /// Returns the lowest-HP-ratio valid ally, or null if none below threshold.
        /// </summary>
        private Character FindHurtAlly()
        {
            Character best = null;
            float bestRatio = HealThreshold;

            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == m_character || c.IsDead()) continue;

                float maxHp = c.GetMaxHealth();
                if (maxHp <= 0f) continue;

                float ratio = c.GetHealth() / maxHp;
                if (ratio >= HealThreshold) continue;

                float dist = Vector3.Distance(transform.position, c.transform.position);
                if (dist > HealRange) continue;

                // Only heal the player or player-owned companions
                bool isPlayer = c.IsPlayer();
                bool isCompanion = c.GetComponent<CompanionSetup>() != null;
                if (!isPlayer && !isCompanion) continue;

                if (ratio < bestRatio)
                {
                    bestRatio = ratio;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// Moves toward the heal target and uses the heal staff when in range.
        /// </summary>
        private void UpdateHealBehavior(Humanoid humanoid, float dt)
        {
            if (_healTarget == null || _healTarget.IsDead()) return;

            ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
            if (weapon == null) return;

            float dist = Vector3.Distance(transform.position, _healTarget.transform.position)
                         - _healTarget.GetRadius();
            float attackRange = weapon.m_shared.m_aiAttackRange;

            // Throttled state logging
            _healLogTimer -= dt;
            if (_healLogTimer <= 0f)
            {
                _healLogTimer = HealLogInterval;
                CompanionsPlugin.Log.LogDebug(
                    $"[CompanionAI:Heal] Healing \"{_healTarget.m_name}\" — " +
                    $"dist={dist:F1} range={attackRange:F1} " +
                    $"weapon=\"{weapon.m_shared.m_name}\" " +
                    $"targetHP={_healTarget.GetHealth():F0}/{_healTarget.GetMaxHealth():F0}");
            }

            if (dist < attackRange)
            {
                StopMoving();
                LookAt(_healTarget.GetTopPoint());

                if (Time.time >= _attackRetryTime && CanAttackNow(weapon) &&
                    IsLookingAt(_healTarget.transform.position,
                        weapon.m_shared.m_aiAttackMaxAngle,
                        weapon.m_shared.m_aiInvertAngleCheck))
                {
                    if (SuppressAttack) return;

                    bool success = m_character.StartAttack(_healTarget, false);
                    if (success)
                    {
                        m_timeSinceAttacking = 0f;
                        CompanionsPlugin.Log.LogDebug(
                            $"[CompanionAI:Heal] Heal attack fired on \"{_healTarget.m_name}\" " +
                            $"(HP: {_healTarget.GetHealth():F0}/{_healTarget.GetMaxHealth():F0})");
                    }
                    else
                    {
                        _attackRetryTime = Time.time + 0.5f;
                        CompanionsPlugin.Log.LogDebug(
                            $"[CompanionAI:Heal] Heal attack FAILED on \"{_healTarget.m_name}\" — retrying in 0.5s");
                    }
                }
            }
            else
            {
                // Move toward heal target — run if far
                MoveToPoint(dt, _healTarget.transform.position, 0f, dist > 10f);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Damage Response
        // ══════════════════════════════════════════════════════════════════════

        protected override void OnDamaged(float damage, Character attacker)
        {
            base.OnDamaged(damage, attacker);
            Wakeup();
            SetAlerted(true);

            if (attacker != null && m_targetCreature == null &&
                (!attacker.IsPlayer() || !m_character.IsTamed()))
            {
                m_targetCreature = attacker;
                m_lastKnownTargetPos = attacker.transform.position;
                m_beenAtLastPos = false;
                m_targetStatic = null;
            }
        }

        protected override void RPC_OnNearProjectileHit(long sender, Vector3 center,
            float range, ZDOID attackerID)
        {
            if (!m_nview.IsOwner()) return;

            SetAlerted(true);

            GameObject attackerGO = ZNetScene.instance.FindInstance(attackerID);
            if (attackerGO != null)
            {
                Character attacker = attackerGO.GetComponent<Character>();
                if (attacker != null && m_targetCreature == null)
                {
                    m_targetCreature = attacker;
                    m_lastKnownTargetPos = attacker.transform.position;
                    m_beenAtLastPos = false;
                    m_targetStatic = null;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Sleep System (from MonsterAI — verbatim)
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateSleep(float dt)
        {
            Player player = null;
            if (m_wakeupRange > 0f || m_fallAsleepDistance > 0f)
            {
                player = Player.GetClosestPlayer(transform.position, m_wakeupRange);
                if (player != null && (player.InGhostMode() || player.IsDebugFlying()))
                    player = null;
            }

            if (!IsSleeping())
            {
                if (m_fallAsleepDistance > 0f &&
                    (player == null || Vector3.Distance(player.transform.position,
                        transform.position) > m_fallAsleepDistance))
                {
                    Sleep();
                }
                return;
            }

            m_sleepTimer += dt;
            if (m_sleepTimer < m_sleepDelay)
                return;

            // Wake conditions
            if (m_wakeupRange > 0f && player != null)
            {
                Wakeup();
            }
            else if (m_noiseWakeup)
            {
                Player noisePlayer = Player.GetPlayerNoiseRange(
                    transform.position, m_maxNoiseWakeupRange);
                if (noisePlayer != null && !noisePlayer.InGhostMode()
                    && !noisePlayer.IsDebugFlying())
                {
                    Wakeup();
                }
            }
        }

        public override bool IsSleeping() => m_sleeping;

        private void Wakeup()
        {
            if (!IsSleeping()) return;
            if (m_animator != null)
                m_animator.SetBool(s_sleeping, false);
            m_nview.GetZDO().Set(ZDOVars.s_sleeping, false);
            m_wakeupEffects.Create(transform.position, transform.rotation);
            m_sleeping = false;
            m_nview.InvokeRPC(ZNetView.Everybody, "RPC_Wakeup");
        }

        private void Sleep()
        {
            if (IsSleeping()) return;
            SetAlerted(false);
            m_sleepTimer = 0f;
            m_targetCreature = null;
            if (m_animator != null)
                m_animator.SetBool(s_sleeping, true);
            m_nview.GetZDO().Set(ZDOVars.s_sleeping, true);
            m_sleepEffects.Create(transform.position, transform.rotation);
            m_sleeping = true;
            m_nview.InvokeRPC(ZNetView.Everybody, "RPC_Sleep");
        }

        private void RPC_Wakeup(long sender)
        {
            if (!m_nview.GetZDO().IsOwner())
                m_sleeping = false;
        }

        private void RPC_Sleep(long sender)
        {
            if (!m_nview.GetZDO().IsOwner())
                m_sleeping = true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Furniture Unstuck — beds, chairs, etc. block NavMesh pathing
        // ══════════════════════════════════════════════════════════════════════

        private float _furnitureStuckTimer;
        private const float FurnitureStuckThreshold = 3f;

        private void UpdateFurnitureUnstuck(float dt)
        {
            // Only check when not resting and actually trying to move
            if (m_character == null) return;
            float vel = m_character.GetVelocity().magnitude;
            bool wantsToMove = m_follow != null || PendingMoveTarget.HasValue ||
                               PendingCartAttach != null || _pendingShip != null ||
                               (_rest != null && _rest.IsNavigating);

            if (vel < 0.1f && wantsToMove)
                _furnitureStuckTimer += dt;
            else
                _furnitureStuckTimer = 0f;

            if (_furnitureStuckTimer < FurnitureStuckThreshold) return;

            // Check if standing on a bed or other piece furniture via downward raycast
            Vector3 origin = transform.position + Vector3.up * 0.5f;
            int pieceMask = LayerMask.GetMask("piece", "piece_nonsolid");
            if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, 1.5f, pieceMask))
                return;

            var bed = hit.collider.GetComponentInParent<Bed>();
            if (bed == null) return;

            // Found a bed under us — nudge companion off it
            _furnitureStuckTimer = 0f;

            // Pick a direction away from bed center
            Vector3 awayDir = (transform.position - bed.transform.position);
            awayDir.y = 0f;
            if (awayDir.sqrMagnitude < 0.01f)
                awayDir = transform.forward;
            awayDir = awayDir.normalized;

            // Find ground position 2m away from bed
            Vector3 targetPos = bed.transform.position + awayDir * 2.5f;
            int groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");
            if (Physics.Raycast(targetPos + Vector3.up * 5f, Vector3.down, out RaycastHit groundHit, 10f, groundMask))
                targetPos.y = groundHit.point.y;
            else
                targetPos.y = transform.position.y - 0.5f; // step down slightly

            transform.position = targetPos;
            if (_body != null)
            {
                _body.position = targetPos;
                _body.linearVelocity = Vector3.zero;
            }

            CompanionsPlugin.Log.LogDebug(
                $"[CompanionAI] Unstuck from bed \"{bed.name}\" — nudged to {targetPos:F1}");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Proactive Jump — clear small obstacles without full stuck recovery
        // ══════════════════════════════════════════════════════════════════════

        private float _proactiveJumpTimer;
        private const float ProactiveJumpCooldown = 5f;        // min seconds between jumps
        private const float ProactiveJumpVelThreshold = 0.5f;  // velocity below this + wants to move = try jump
        private const float ProactiveJumpBlockedTime = 2.0f;   // seconds of low velocity before jumping

        private float _lowVelAccum;                  // accumulates time spent at low velocity
        private int   _proactiveJumpAttempts;        // consecutive jumps without meaningful progress
        private Vector3 _proactiveJumpStartPos;      // position when jump attempt counter started
        private float _proactiveJumpSuppressTimer;   // when > 0, all proactive jumping suppressed

        /// <summary>
        /// Detects when the companion has low velocity but wants to move (likely
        /// blocked by a small step or rock) and triggers a jump to clear it.
        /// Capped at 3 consecutive attempts before entering a 30s suppression period.
        /// </summary>
        private void UpdateProactiveJump(float dt)
        {
            if (m_character == null) return;
            if (!m_character.IsOnGround()) return;
            if (m_character.IsSwimming()) return;
            if (m_character.InAttack()) return;
            if (_groundStuckRecoveryTimer > 0f) return; // stuck recovery handles movement
            if (_inContextSteerFallback) return;         // context steer has its own stuck handling

            _proactiveJumpTimer -= dt;
            _proactiveJumpSuppressTimer -= dt;

            // Suppressed after too many failed jump attempts
            if (_proactiveJumpSuppressTimer > 0f)
            {
                _lowVelAccum = 0f;
                return;
            }

            // Determine if companion wants to move
            bool wantsToMove = (m_targetCreature != null) ||
                               (m_targetStatic != null) ||
                               PendingMoveTarget.HasValue ||
                               PendingCartAttach != null ||
                               (_rest != null && _rest.IsNavigating) ||
                               (_harvest != null && _harvest.IsActive) ||
                               (_smelt != null && _smelt.IsActive) ||
                               (_farm != null && _farm.IsActive) ||
                               (_repair != null && _repair.IsActive) ||
                               IsRepairBuildActive ||
                               IsRestockActive ||
                               _returningHome;

            // Follow mode: only count as "wants to move" if actually far from target
            if (!wantsToMove && m_follow != null)
            {
                float distToFollow = Vector3.Distance(transform.position, m_follow.transform.position);
                wantsToMove = distToFollow > 5f;
            }

            if (!wantsToMove)
            {
                _lowVelAccum = 0f;
                return;
            }

            // Skip if at home patrol point (intentionally stationary)
            if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition() && !_setup.GetFollow())
            {
                float distHome = Vector3.Distance(transform.position, _setup.GetHomePosition());
                if (distHome < 4f)
                {
                    _lowVelAccum = 0f;
                    return;
                }
            }

            float vel = m_character.GetVelocity().magnitude;
            if (vel < ProactiveJumpVelThreshold)
            {
                _lowVelAccum += dt;
            }
            else
            {
                _lowVelAccum = 0f;
                // Moving well — reset jump attempt counter if we've moved far enough
                if (_proactiveJumpAttempts > 0 &&
                    Vector3.Distance(transform.position, _proactiveJumpStartPos) > 2f)
                {
                    _proactiveJumpAttempts = 0;
                }
            }

            // Low velocity for long enough and cooldown expired — jump
            if (_lowVelAccum >= ProactiveJumpBlockedTime && _proactiveJumpTimer <= 0f)
            {
                _proactiveJumpAttempts++;

                // Too many consecutive jumps without progress — suppress for 30s
                if (_proactiveJumpAttempts >= 3)
                {
                    _proactiveJumpSuppressTimer = 30f;
                    _proactiveJumpAttempts = 0;
                    _lowVelAccum = 0f;
                    return;
                }

                if (_proactiveJumpAttempts == 1)
                    _proactiveJumpStartPos = transform.position;

                m_character.Jump(false);
                _proactiveJumpTimer = ProactiveJumpCooldown;
                _lowVelAccum = 0f;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Ground Movement Stuck Recovery
        //  BaseAI.MoveTo() has NO built-in stuck detection (only MoveAndAvoid
        //  for flying creatures does). When pathfinding fails or the character
        //  hits an unmapped obstacle, they just stop moving forever. This system
        //  detects that condition and forces movement recovery.
        // ══════════════════════════════════════════════════════════════════════

        private float _groundStuckTimer;            // accumulates when vel < threshold and wants to move
        private float _groundStuckRecoveryTimer;    // when > 0, strafing to escape
        private float _groundStuckAngle;            // random strafe angle
        private int   _groundStuckAttempts;         // escalating: 0-2 strafe, 3 jump+strafe, 4+ teleport
        private Vector3 _lastGroundStuckPos;        // position when first stuck detected (for reset)
        private float _continuousSwimTimer;         // time spent swimming (for proactive water escape)
        private float _combatMoveStuckTimer;        // stuck while moving toward combat target
        private float _groundStuckSuppressTimer;    // when > 0, all stuck recovery suppressed

        private const float GroundStuckThreshold = 2.0f;     // seconds of low velocity before stuck
        private const float GroundStuckRecoveryTime = 2.5f;  // seconds of strafe per attempt
        private const float GroundStuckVelThreshold = 0.3f;  // velocity below this = not moving
        private const float GroundStuckResetDist = 3f;       // must move this far to reset attempts
        private const int   GroundStuckMaxStrafe = 3;        // strafe attempts before jump+strafe
        private const int   GroundStuckMaxBeforeTeleport = 5; // attempts before teleporting

        // ── Target blacklist — prevents repeatedly trying unreachable positions ──
        private readonly Dictionary<Vector3Int, float> _targetBlacklist = new Dictionary<Vector3Int, float>();
        private const float BlacklistDuration = 30f;    // seconds before blacklist entry expires
        private const float BlacklistCellSize = 2f;     // grid resolution for position rounding
        private float _blacklistCleanupTimer;

        private Vector3Int ToBlacklistKey(Vector3 pos)
            => new Vector3Int(
                Mathf.FloorToInt(pos.x / BlacklistCellSize),
                Mathf.FloorToInt(pos.y / BlacklistCellSize),
                Mathf.FloorToInt(pos.z / BlacklistCellSize));

        /// <summary>
        /// Marks a world position as unreachable. All controllers should call this
        /// when they give up on a target due to stuck detection.
        /// </summary>
        internal void BlacklistPosition(Vector3 pos)
        {
            var key = ToBlacklistKey(pos);
            _targetBlacklist[key] = Time.time;
            CompanionsPlugin.Log.LogInfo(
                $"[AI:Blacklist] Blacklisted position {pos:F1} (cell={key}) for {BlacklistDuration}s");
        }

        /// <summary>
        /// Returns true if the given position has been blacklisted as unreachable.
        /// Controllers should check this before committing to a target.
        /// </summary>
        internal bool IsPositionBlacklisted(Vector3 pos)
        {
            var key = ToBlacklistKey(pos);
            if (_targetBlacklist.TryGetValue(key, out float timestamp))
            {
                if (Time.time - timestamp < BlacklistDuration) return true;
                _targetBlacklist.Remove(key);
            }
            return false;
        }

        private void CleanupBlacklist()
        {
            _blacklistCleanupTimer += Time.deltaTime;
            if (_blacklistCleanupTimer < 30f) return;
            _blacklistCleanupTimer = 0f;
            if (_targetBlacklist.Count == 0) return;

            float now = Time.time;
            List<Vector3Int> expired = null;
            foreach (var kvp in _targetBlacklist)
            {
                if (now - kvp.Value >= BlacklistDuration)
                {
                    if (expired == null) expired = new List<Vector3Int>();
                    expired.Add(kvp.Key);
                }
            }
            if (expired != null)
            {
                for (int i = 0; i < expired.Count; i++)
                    _targetBlacklist.Remove(expired[i]);
                CompanionsPlugin.Log.LogDebug(
                    $"[AI:Blacklist] Cleanup: removed {expired.Count} expired, " +
                    $"{_targetBlacklist.Count} remain");
            }
        }

        /// <summary>
        /// Detects when the companion is stuck on ground (low velocity but wants to move)
        /// and forces recovery by strafing at random angles, then jumping, then teleporting.
        /// Returns true if recovery movement is active (caller should skip normal AI).
        /// </summary>
        private bool UpdateGroundStuckRecovery(float dt)
        {
            if (m_character == null) return false;

            // Context steer fallback has its own stuck handling (UpdateFallbackStuck).
            // Don't fight it with a second movement override — that causes oscillation.
            if (_inContextSteerFallback)
            {
                _groundStuckTimer = 0f;
                return false;
            }

            // Only trigger stuck detection when MoveToPoint was actively called recently.
            // Controllers have stationary phases (inserting items, repairing, opening chests)
            // where the companion SHOULD be standing still. Without this check, vel=0 during
            // those phases falsely triggers stuck recovery → spinning/jumping.
            if (Time.frameCount - _moveToPointActiveFrame > 2)
            {
                _groundStuckTimer = 0f;
                return false;
            }

            // Suppressed after too many failed attempts (no follow target to teleport to)
            _groundStuckSuppressTimer -= dt;
            if (_groundStuckSuppressTimer > 0f)
            {
                _groundStuckTimer = 0f;
                return false;
            }

            // Track swimming time for proactive water escape
            if (m_character.IsSwimming())
                _continuousSwimTimer += dt;
            else
                _continuousSwimTimer = 0f;

            // Phase 1: Active recovery — strafing to escape
            if (_groundStuckRecoveryTimer > 0f)
            {
                _groundStuckRecoveryTimer -= dt;

                // Compute strafe direction from stuck angle
                Vector3 strafeDir = Quaternion.Euler(0f, _groundStuckAngle, 0f) * transform.forward;
                MoveTowards(strafeDir, true);

                // Jump once at the start of recovery to clear low obstacles (not every frame)
                if (_groundStuckRecoveryTimer > GroundStuckRecoveryTime - 0.1f
                    && m_character.IsOnGround() && !m_character.IsSwimming()
                    && _groundStuckAttempts >= GroundStuckMaxStrafe)
                    m_character.Jump(false);

                if (_groundStuckRecoveryTimer <= 0f)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI:Unstuck] Recovery strafe #{_groundStuckAttempts} complete — " +
                        $"pos={transform.position:F1}");
                }

                return true; // override normal AI
            }

            // Phase 2: Detection — check if stuck
            float vel = m_character.GetVelocity().magnitude;
            bool inAttack = m_character.InAttack();
            bool isResting = _rest != null && _rest.IsResting;
            bool onShip = _isOnShip || _pendingShip != null;
            bool frozen = FreezeTimer > 0f;

            // Determine if the companion SHOULD be moving
            bool wantsToMove = (m_follow != null) ||
                               (m_targetCreature != null) ||
                               (m_targetStatic != null) ||
                               PendingMoveTarget.HasValue ||
                               PendingCartAttach != null ||
                               (_rest != null && _rest.IsNavigating) ||
                               (_harvest != null && _harvest.IsActive) ||
                               (_smelt != null && _smelt.IsActive) ||
                               (_farm != null && _farm.IsActive) ||
                               (_repair != null && _repair.IsActive) ||
                               IsRepairBuildActive ||
                               IsRestockActive ||
                               (_doorHandler != null && _doorHandler.IsActive) ||
                               _returningHome;

            // Don't accumulate stuck time during attacks, rest, ship, freeze, or when stationary by choice
            if (inAttack || isResting || onShip || frozen || !wantsToMove)
            {
                _groundStuckTimer = 0f;
                return false;
            }

            // Also skip if companion is at follow distance (close to player, intentionally stopped)
            if (m_follow != null && m_targetCreature == null)
            {
                float distToFollow = Vector3.Distance(transform.position, m_follow.transform.position);
                if (distToFollow < 5f)
                {
                    _groundStuckTimer = 0f;
                    return false;
                }
            }

            // Check if we've moved far enough from last stuck position to reset attempts
            if (_groundStuckAttempts > 0 &&
                Vector3.Distance(transform.position, _lastGroundStuckPos) > GroundStuckResetDist)
            {
                _groundStuckAttempts = 0;
            }

            // Accumulate or reset stuck timer
            if (vel < GroundStuckVelThreshold)
                _groundStuckTimer += dt;
            else
                _groundStuckTimer = 0f;

            // Not stuck yet
            if (_groundStuckTimer < GroundStuckThreshold)
                return false;

            // ── Stuck detected! ──
            _groundStuckTimer = 0f;
            _groundStuckAttempts++;
            _lastGroundStuckPos = transform.position;

            // Teleport as last resort (follow mode only)
            if (_groundStuckAttempts >= GroundStuckMaxBeforeTeleport)
            {
                if (m_follow != null)
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[AI:Unstuck] {_groundStuckAttempts} failed recovery attempts — " +
                        $"teleporting to follow target. pos={transform.position:F1}");
                    TeleportToFollowTarget();
                    _groundStuckAttempts = 0;
                    return false;
                }

                // No follow target — can't teleport. Suppress stuck recovery for 30s
                // to stop the strafe-jump-strafe spin loop. Controllers handle their
                // own stuck timeouts and will abort if needed.
                CompanionsPlugin.Log.LogWarning(
                    $"[AI:Unstuck] {_groundStuckAttempts} failed recovery attempts — " +
                    $"no follow target, suppressing 30s. pos={transform.position:F1}");
                _groundStuckSuppressTimer = 30f;
                _groundStuckAttempts = 0;
                return false;
            }

            // Strafe recovery: probe multiple directions with raycasts to find
            // the best escape route instead of picking a random angle.
            _groundStuckAngle = FindBestStrafeAngle();
            _groundStuckRecoveryTimer = GroundStuckRecoveryTime;

            CompanionsPlugin.Log.LogInfo(
                $"[AI:Unstuck] Ground stuck detected — attempt #{_groundStuckAttempts} " +
                $"(angle={_groundStuckAngle:F0}° jump={_groundStuckAttempts >= GroundStuckMaxStrafe}) " +
                $"vel={vel:F2} pos={transform.position:F1} " +
                $"target=\"{m_targetCreature?.m_name ?? m_follow?.name ?? "?"}\"");

            return true; // start strafing
        }

        /// <summary>
        /// Probes 8 directions with raycasts and returns the strafe angle
        /// with the most clearance. Prefers directions perpendicular to the
        /// companion→target vector (going AROUND obstacles rather than away).
        /// Falls back to random ±90° if all directions are equally blocked.
        /// </summary>
        private static int _strafeProbeMask;

        private float FindBestStrafeAngle()
        {
            if (_strafeProbeMask == 0)
                _strafeProbeMask = LayerMask.GetMask("Default", "static_solid", "Default_small",
                    "piece", "terrain", "vehicle");

            Vector3 origin = transform.position + Vector3.up * 0.5f;
            float probeRange = 8f;

            // Determine target direction for scoring (prefer going around, not away)
            Vector3 targetDir = transform.forward; // default: use facing direction
            if (m_targetCreature != null)
                targetDir = (m_targetCreature.transform.position - transform.position).normalized;
            else if (m_targetStatic != null)
                targetDir = (m_targetStatic.transform.position - transform.position).normalized;
            else if (m_follow != null)
                targetDir = (m_follow.transform.position - transform.position).normalized;
            else if (_farm != null && _farm.IsActive)
                targetDir = transform.forward;

            // Perpendicular to target direction (for "go around" scoring)
            Vector3 perp = Vector3.Cross(Vector3.up, targetDir).normalized;

            float bestAngle = 90f;
            float bestScore = -1f;

            // Probe 8 directions: ±45, ±90, ±135, 180, and 0 (forward)
            float[] angles = { 90f, -90f, 45f, -45f, 135f, -135f, 180f, 0f };
            for (int i = 0; i < angles.Length; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, angles[i], 0f) * transform.forward;
                float clearance;
                if (Physics.Raycast(origin, dir, out RaycastHit hit, probeRange, _strafeProbeMask))
                    clearance = hit.distance;
                else
                    clearance = probeRange;

                // Score: clearance distance weighted by direction quality
                // - Perpendicular to target line = high score (going AROUND)
                // - Toward target = slight bonus
                // - Away from target = penalized (waste of time)
                float perpAlignment = Mathf.Abs(Vector3.Dot(dir, perp));     // 0-1, higher = more perpendicular
                float targetAlignment = Mathf.Max(0f, Vector3.Dot(dir, targetDir)); // 0-1, higher = toward target
                float awayPenalty = Mathf.Max(0f, -Vector3.Dot(dir, targetDir));    // 0-1, higher = away from target

                float score = clearance * (0.4f + 0.35f * perpAlignment + 0.25f * targetAlignment - 0.15f * awayPenalty);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAngle = angles[i];
                }
            }

            CompanionsPlugin.Log.LogDebug(
                $"[AI:Unstuck] Raycast probe — best angle={bestAngle:F0}° " +
                $"score={bestScore:F1} pos={transform.position:F1}");

            return bestAngle;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Repair Buildings Mode — walk to damaged pieces, equip hammer, repair
        // ══════════════════════════════════════════════════════════════════════

        private enum RepairBuildPhase { Idle, MovingToTarget, Repairing }

        private RepairBuildPhase _rbPhase;
        private float _rbScanTimer;
        private float _rbActionTimer;
        private WearNTear _rbTargetPiece;
        private ItemDrop.ItemData _rbPrevRightItem;
        private bool _rbHammerEquipped;
        private float _rbStuckTimer;
        private float _rbStuckCheckTimer;
        private Vector3 _rbStuckCheckPos;

        private const float RBScanInterval = 10f;
        private const float RBRadius = 50f;
        private const float RBUseDistance = 2.5f;
        private const float RBRepairDelay = 1.2f;
        private const float RBMoveTimeout = 15f;

        internal bool IsRepairBuildActive => _rbPhase != RepairBuildPhase.Idle;

        private void UpdateRepairBuildingsMode(float dt)
        {
            switch (_rbPhase)
            {
                case RepairBuildPhase.Idle:
                    UpdateRBIdle(dt);
                    break;
                case RepairBuildPhase.MovingToTarget:
                    UpdateRBMoving(dt);
                    break;
                case RepairBuildPhase.Repairing:
                    UpdateRBRepairing(dt);
                    break;
            }
        }

        private void UpdateRBIdle(float dt)
        {
            _rbScanTimer -= dt;
            if (_rbScanTimer > 0f) return;
            _rbScanTimer = RBScanInterval;

            var humanoid = GetComponent<Humanoid>();
            var inv = humanoid?.GetInventory();
            if (inv == null) return;

            if (HomesteadController.FindHammerInInventory(inv) == null)
            {
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_repair_no_hammer"), "Repair");
                return;
            }

            // Find closest damaged player-built piece
            var allWNT = WearNTear.GetAllInstances();
            if (allWNT == null) return;

            Vector3 center = transform.position;
            WearNTear bestPiece = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < allWNT.Count; i++)
            {
                var wnt = allWNT[i];
                if (wnt == null) continue;
                var piece = wnt.GetComponent<Piece>();
                if (piece == null || !piece.IsPlacedByPlayer()) continue;
                float dist = Vector3.Distance(center, wnt.transform.position);
                if (dist > RBRadius) continue;
                if (wnt.GetHealthPercentage() >= 1f) continue;
                if (IsPositionBlacklisted(wnt.transform.position)) continue;
                if (dist < bestDist)
                {
                    bestPiece = wnt;
                    bestDist = dist;
                }
            }

            if (bestPiece == null)
            {
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_repair_nothing"), "Repair");
                return;
            }

            _rbTargetPiece = bestPiece;
            _rbStuckTimer = 0f;
            _rbStuckCheckTimer = 0f;
            _rbStuckCheckPos = transform.position;
            _rbPhase = RepairBuildPhase.MovingToTarget;
            CompanionsPlugin.Log.LogInfo($"[AI:RepairBuild] Found damaged piece ({bestDist:F1}m)");
        }

        private void UpdateRBMoving(float dt)
        {
            if (_rbTargetPiece == null) { AbortRepairBuild("target destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _rbTargetPiece.transform.position);

            if (dist <= RBUseDistance)
            {
                StopMoving();
                LookAt(_rbTargetPiece.transform.position);

                // Equip hammer
                var humanoid = GetComponent<Humanoid>();
                var inv = humanoid?.GetInventory();
                _rbHammerEquipped = false;
                if (inv != null)
                {
                    var hammer = HomesteadController.FindHammerInInventory(inv);
                    if (hammer != null)
                    {
                        _rbPrevRightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(humanoid);
                        if (_rbPrevRightItem != null && _rbPrevRightItem != hammer)
                            humanoid.UnequipItem(_rbPrevRightItem, false);
                        humanoid.EquipItem(hammer, true);
                        _rbHammerEquipped = true;
                    }
                }

                _rbActionTimer = RBRepairDelay;
                _rbPhase = RepairBuildPhase.Repairing;
                return;
            }

            MoveToPoint(dt, _rbTargetPiece.transform.position, RBUseDistance, dist > 8f);

            // Stuck detection
            _rbStuckCheckTimer += dt;
            if (_rbStuckCheckTimer >= 1f)
            {
                float moved = Vector3.Distance(transform.position, _rbStuckCheckPos);
                if (moved < 0.5f)
                    _rbStuckTimer += _rbStuckCheckTimer;
                else
                    _rbStuckTimer = 0f;
                _rbStuckCheckPos = transform.position;
                _rbStuckCheckTimer = 0f;
            }
            if (_rbStuckTimer > RBMoveTimeout)
            {
                BlacklistPosition(_rbTargetPiece.transform.position);
                AbortRepairBuild("stuck");
            }
        }

        private void UpdateRBRepairing(float dt)
        {
            if (_rbTargetPiece == null) { AbortRepairBuild("target destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _rbTargetPiece.transform.position);
            if (dist > RBUseDistance * 2f) { AbortRepairBuild("drifted"); return; }

            LookAt(_rbTargetPiece.transform.position);

            _rbActionTimer -= dt;
            if (_rbActionTimer > 0f) return;
            _rbActionTimer = RBRepairDelay;

            // Play hammer swing animation
            var humanoid = GetComponent<Humanoid>();
            var zanim = GetComponent<ZSyncAnimation>();
            var rightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(humanoid);
            if (zanim != null)
            {
                string anim = rightItem?.m_shared?.m_attack?.m_attackAnimation;
                zanim.SetTrigger(!string.IsNullOrEmpty(anim) ? anim : "swing_pickaxe");
            }

            _rbTargetPiece.Repair();

            var piece = _rbTargetPiece.GetComponent<Piece>();
            if (piece?.m_placeEffect != null)
                piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation);

            if (_rbTargetPiece.GetHealthPercentage() >= 1f)
            {
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_repair_buildings_done"), "Repair");
                RestoreRBWeapon();
                _rbTargetPiece = null;
                _rbPhase = RepairBuildPhase.Idle;
                _rbScanTimer = 2f; // quick rescan for next
            }
        }

        private void RestoreRBWeapon()
        {
            if (!_rbHammerEquipped) return;
            _rbHammerEquipped = false;

            var humanoid = GetComponent<Humanoid>();
            var rightItem = (ItemDrop.ItemData)CompanionSetup._rightItemField?.GetValue(humanoid);
            if (rightItem?.m_shared?.m_buildPieces != null)
                humanoid.UnequipItem(rightItem, false);

            if (_rbPrevRightItem != null)
            {
                var inv = humanoid?.GetInventory();
                if (inv != null && inv.ContainsItem(_rbPrevRightItem))
                    humanoid.EquipItem(_rbPrevRightItem, true);
                else
                    GetComponent<CompanionSetup>()?.SyncEquipmentToInventory();
            }
            else
            {
                GetComponent<CompanionSetup>()?.SyncEquipmentToInventory();
            }
            _rbPrevRightItem = null;
        }

        private void AbortRepairBuild(string reason)
        {
            CompanionsPlugin.Log.LogInfo($"[AI:RepairBuild] Abort: {reason}");
            RestoreRBWeapon();
            _rbTargetPiece = null;
            _rbPhase = RepairBuildPhase.Idle;
            _rbScanTimer = RBScanInterval;
        }

        internal void ResetRepairBuildState()
        {
            if (_rbPhase != RepairBuildPhase.Idle)
                AbortRepairBuild("mode changed");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Restock Mode — walk to chests for fuel, then walk to fireplaces
        // ══════════════════════════════════════════════════════════════════════

        private enum RestockPhase { Idle, MovingToChest, TakingFuel, MovingToFire, Refueling }

        private RestockPhase _rsPhase;
        private float _rsScanTimer;
        private float _rsActionTimer;
        private Fireplace _rsTargetFire;
        private Container _rsSupplyChest;
        private string _rsFuelPrefab;
        private int _rsFuelToAdd;
        private bool _rsChestOpened;
        private float _rsStuckTimer;
        private float _rsStuckCheckTimer;
        private Vector3 _rsStuckCheckPos;

        private const float RSScanInterval = 10f;
        private const float RSRadius = 50f;
        private const float RSUseDistance = 2.5f;
        private const float RSFuelThreshold = 0.8f;
        private const float RSFuelAddDelay = 0.8f;
        private const float RSChestOpenDelay = 0.8f;
        private const float RSItemTransferDelay = 0.6f;
        private const float RSMoveTimeout = 15f;

        internal bool IsRestockActive => _rsPhase != RestockPhase.Idle;

        private void UpdateRestockMode(float dt)
        {
            switch (_rsPhase)
            {
                case RestockPhase.Idle:
                    UpdateRSIdle(dt);
                    break;
                case RestockPhase.MovingToChest:
                    UpdateRSMovingToChest(dt);
                    break;
                case RestockPhase.TakingFuel:
                    UpdateRSTakingFuel(dt);
                    break;
                case RestockPhase.MovingToFire:
                    UpdateRSMovingToFire(dt);
                    break;
                case RestockPhase.Refueling:
                    UpdateRSRefueling(dt);
                    break;
            }
        }

        private void UpdateRSIdle(float dt)
        {
            _rsScanTimer -= dt;
            if (_rsScanTimer > 0f) return;
            _rsScanTimer = RSScanInterval;

            var humanoid = GetComponent<Humanoid>();
            var inv = humanoid?.GetInventory();
            if (inv == null) return;

            // Find closest low-fuel fireplace
            Vector3 center = transform.position;
            var tempPieces = new System.Collections.Generic.List<Piece>();
            Piece.GetAllPiecesInRadius(center, RSRadius, tempPieces);

            Fireplace bestFire = null;
            float bestDist = float.MaxValue;

            for (int i = 0; i < tempPieces.Count; i++)
            {
                var fp = tempPieces[i].GetComponent<Fireplace>();
                if (fp == null) continue;
                if (!fp.m_canRefill || fp.m_infiniteFuel || fp.m_fuelItem == null) continue;

                var fpNview = fp.GetComponent<ZNetView>();
                if (fpNview == null || fpNview.GetZDO() == null) continue;

                float currentFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
                float maxFuel = fp.m_maxFuel;
                if (maxFuel <= 0f) continue;
                if (currentFuel / maxFuel >= RSFuelThreshold) continue;
                if (IsPositionBlacklisted(fp.transform.position)) continue;

                float dist = Vector3.Distance(center, fp.transform.position);
                if (dist < bestDist)
                {
                    bestFire = fp;
                    bestDist = dist;
                }
            }

            if (bestFire == null)
            {
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_restock_nothing"), "Action");
                return;
            }

            _rsTargetFire = bestFire;
            _rsFuelPrefab = bestFire.m_fuelItem.gameObject.name;

            var fireNview = bestFire.GetComponent<ZNetView>();
            float fuel = fireNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            _rsFuelToAdd = Mathf.Max(1, (int)(bestFire.m_maxFuel - fuel));

            // Check if we already have fuel in inventory
            int carried = HomesteadController.CountItemInInventory(inv, _rsFuelPrefab);

            if (carried > 0)
            {
                // Go straight to fire
                _rsFuelToAdd = Mathf.Min(_rsFuelToAdd, carried);
                ResetRSStuck();
                _rsPhase = RestockPhase.MovingToFire;
                CompanionsPlugin.Log.LogInfo($"[AI:Restock] Carrying {carried}x fuel — heading to fire");
                return;
            }

            // Find a chest with fuel
            var chestPieces = new System.Collections.Generic.List<Piece>();
            Piece.GetAllPiecesInRadius(center, RSRadius, chestPieces);

            Container bestChest = null;
            float bestChestDist = float.MaxValue;

            for (int c = 0; c < chestPieces.Count; c++)
            {
                var container = chestPieces[c].GetComponent<Container>();
                if (container == null) continue;
                if (container.gameObject == gameObject) continue;
                if (container.IsInUse()) continue;
                var chestInv = container.GetInventory();
                if (chestInv == null) continue;
                if (HomesteadController.FindItemByPrefab(chestInv, _rsFuelPrefab) == null) continue;
                if (IsPositionBlacklisted(container.transform.position)) continue;

                float cDist = Vector3.Distance(center, container.transform.position);
                if (cDist < bestChestDist)
                {
                    bestChest = container;
                    bestChestDist = cDist;
                }
            }

            if (bestChest == null)
            {
                CompanionsPlugin.Log.LogInfo($"[AI:Restock] No chest with \"{_rsFuelPrefab}\" — skipping");
                _rsTargetFire = null;
                return;
            }

            _rsSupplyChest = bestChest;
            ResetRSStuck();
            _rsPhase = RestockPhase.MovingToChest;
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_homestead_refuel"), "Action");
            CompanionsPlugin.Log.LogInfo($"[AI:Restock] Fetching fuel from chest ({bestChestDist:F1}m)");
        }

        private void UpdateRSMovingToChest(float dt)
        {
            if (_rsSupplyChest == null) { AbortRestock("chest destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _rsSupplyChest.transform.position);

            if (dist <= RSUseDistance)
            {
                StopMoving();
                LookAt(_rsSupplyChest.transform.position);
                _rsChestOpened = false;
                _rsPhase = RestockPhase.TakingFuel;
                return;
            }

            MoveToPoint(dt, _rsSupplyChest.transform.position, RSUseDistance, dist > 8f);
            UpdateRSStuck(dt, "chest", dist);
        }

        private void UpdateRSTakingFuel(float dt)
        {
            if (_rsSupplyChest == null) { AbortRestock("chest destroyed"); return; }

            // Step 1: Open chest
            if (!_rsChestOpened)
            {
                _rsSupplyChest.SetInUse(true);
                _rsChestOpened = true;
                _rsActionTimer = RSChestOpenDelay;
                var zanim = GetComponent<ZSyncAnimation>();
                if (zanim != null) zanim.SetTrigger("interact");
                return;
            }

            _rsActionTimer -= dt;
            if (_rsActionTimer > 0f) return;
            _rsActionTimer = RSItemTransferDelay;

            var chestInv = _rsSupplyChest.GetInventory();
            var humanoid = GetComponent<Humanoid>();
            var companionInv = humanoid?.GetInventory();
            if (chestInv == null || companionInv == null)
            {
                CloseRSChest();
                AbortRestock("inventory null");
                return;
            }

            // Take one fuel item per tick
            var item = HomesteadController.FindItemByPrefab(chestInv, _rsFuelPrefab);
            if (item == null || _rsFuelToAdd <= 0)
            {
                CloseRSChest();
                int carried = HomesteadController.CountItemInInventory(companionInv, _rsFuelPrefab);
                if (carried == 0) { AbortRestock("no fuel taken"); return; }

                _rsFuelToAdd = Mathf.Min(_rsFuelToAdd, carried);
                ResetRSStuck();
                _rsPhase = RestockPhase.MovingToFire;
                return;
            }

            if (HomesteadController.TransferOne(chestInv, companionInv, item))
            {
                _rsFuelToAdd--;
            }
            else
            {
                // Inventory full
                CloseRSChest();
                int carried = HomesteadController.CountItemInInventory(companionInv, _rsFuelPrefab);
                if (carried > 0)
                {
                    _rsFuelToAdd = carried;
                    ResetRSStuck();
                    _rsPhase = RestockPhase.MovingToFire;
                }
                else
                {
                    AbortRestock("inventory full");
                }
            }
        }

        private void UpdateRSMovingToFire(float dt)
        {
            if (_rsTargetFire == null) { AbortRestock("fire destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _rsTargetFire.transform.position);

            if (dist <= RSUseDistance)
            {
                StopMoving();
                LookAt(_rsTargetFire.transform.position);
                _rsActionTimer = RSFuelAddDelay;
                _rsPhase = RestockPhase.Refueling;
                return;
            }

            MoveToPoint(dt, _rsTargetFire.transform.position, RSUseDistance, dist > 8f);
            UpdateRSStuck(dt, "fire", dist);
        }

        private void UpdateRSRefueling(float dt)
        {
            if (_rsTargetFire == null) { AbortRestock("fire destroyed"); return; }

            float dist = Vector3.Distance(transform.position, _rsTargetFire.transform.position);
            if (dist > RSUseDistance * 2f) { AbortRestock("drifted"); return; }

            LookAt(_rsTargetFire.transform.position);

            _rsActionTimer -= dt;
            if (_rsActionTimer > 0f) return;
            _rsActionTimer = RSFuelAddDelay;

            var fpNview = _rsTargetFire.GetComponent<ZNetView>();
            if (fpNview == null || fpNview.GetZDO() == null) { AbortRestock("fire nview lost"); return; }

            float currentFuel = fpNview.GetZDO().GetFloat(ZDOVars.s_fuel);
            if (currentFuel >= _rsTargetFire.m_maxFuel)
            {
                FinishRestock();
                return;
            }

            var humanoid = GetComponent<Humanoid>();
            var inv = humanoid?.GetInventory();
            if (inv == null || !HomesteadController.ConsumeOneFromInventory(inv, _rsFuelPrefab))
            {
                FinishRestock();
                return;
            }

            var zanim = GetComponent<ZSyncAnimation>();
            if (zanim != null) zanim.SetTrigger("interact");
            fpNview.InvokeRPC("RPC_AddFuel");
            _rsFuelToAdd--;

            if (_rsFuelToAdd <= 0)
                FinishRestock();
        }

        private void FinishRestock()
        {
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_restock_done"), "Action");
            _rsTargetFire = null;
            _rsSupplyChest = null;
            _rsFuelPrefab = null;
            _rsPhase = RestockPhase.Idle;
            _rsScanTimer = 2f; // quick rescan
        }

        private void AbortRestock(string reason)
        {
            CompanionsPlugin.Log.LogInfo($"[AI:Restock] Abort: {reason}");
            CloseRSChest();
            _rsTargetFire = null;
            _rsSupplyChest = null;
            _rsFuelPrefab = null;
            _rsPhase = RestockPhase.Idle;
            _rsScanTimer = RSScanInterval;
        }

        private void CloseRSChest()
        {
            if (_rsChestOpened && _rsSupplyChest != null)
                _rsSupplyChest.SetInUse(false);
            _rsChestOpened = false;
        }

        private void ResetRSStuck()
        {
            _rsStuckTimer = 0f;
            _rsStuckCheckTimer = 0f;
            _rsStuckCheckPos = transform.position;
        }

        private void UpdateRSStuck(float dt, string target, float dist)
        {
            _rsStuckCheckTimer += dt;
            if (_rsStuckCheckTimer >= 1f)
            {
                float moved = Vector3.Distance(transform.position, _rsStuckCheckPos);
                if (moved < 0.5f)
                    _rsStuckTimer += _rsStuckCheckTimer;
                else
                    _rsStuckTimer = 0f;
                _rsStuckCheckPos = transform.position;
                _rsStuckCheckTimer = 0f;
            }
            if (_rsStuckTimer > RSMoveTimeout)
            {
                Vector3 targetPos = target == "chest"
                    ? (_rsSupplyChest != null ? _rsSupplyChest.transform.position : transform.position)
                    : (_rsTargetFire != null ? _rsTargetFire.transform.position : transform.position);
                BlacklistPosition(targetPos);
                AbortRestock($"stuck moving to {target} ({dist:F1}m)");
            }
        }

        internal void ResetRestockState()
        {
            if (_rsPhase != RestockPhase.Idle)
                AbortRestock("mode changed");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Debug Logging (replaces TargetPatches.UpdateAI_DebugLog)
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateDebugLog(float dt)
        {
            // Stuck detection
            float moved = Vector3.Distance(transform.position, _lastDebugPos);
            _lastDebugPos = transform.position;

            var follow = m_follow;
            float distToFollow = follow != null
                ? Vector3.Distance(transform.position, follow.transform.position) : -1f;
            bool atFollowDist = follow != null && distToFollow < 5f;
            bool inAttack = m_character != null && m_character.InAttack();

            bool harvesting = _harvest != null && _harvest.IsInGatherMode;
            bool repairing = _repair != null && _repair.IsActive;
            // Use IsSmeltMode (ZDO assignment) rather than IsActive (processing phase)
            // to suppress stuck detection during idle gaps between scan cycles.
            bool smelting = _smelt != null && (_smelt.IsActive || _smelt.IsSmeltMode());
            bool farming = _farm != null && (_farm.IsActive || _farm.IsFarmMode());
            bool homesteading = _homestead != null && _homestead.IsActive;
            bool handlingDoor = _doorHandler != null && _doorHandler.IsActive;

            // StayHome with wander OFF: companion is intentionally stationary
            // at home — don't flag as stuck. Also suppress during return-home.
            // Follow OFF + StayHome OFF: companion is idle with no movement target.
            bool stayingHome = _setup != null && _setup.GetStayHome()
                && !_setup.GetWander() && !harvesting && !smelting && !farming;
            bool followOffIdle = _setup != null && !_setup.GetFollow()
                && !_setup.GetStayHome() && !harvesting && !smelting && !farming;
            bool intentionallyStationary = stayingHome || _returningHome || followOffIdle;

            if (moved < 0.1f && !inAttack && !atFollowDist && !harvesting
                && !repairing && !smelting && !farming && !homesteading && !handlingDoor && !intentionallyStationary)
                _stuckDetectTimer += dt;
            else
                _stuckDetectTimer = 0f;

            // ── One-shot: path result transition logging ──
            bool pathOk = FoundPath();
            if (pathOk != _lastPathOk)
            {
                if (!pathOk)
                    CompanionsPlugin.Log.LogWarning(
                        $"[AI:Path] Path LOST — pos={transform.position:F1} " +
                        $"target=\"{m_targetCreature?.m_name ?? "null"}\" " +
                        $"follow=\"{follow?.name ?? "null"}\"({distToFollow:F1}) " +
                        $"phase={GetAIPhase()}");
                else
                    CompanionsPlugin.Log.LogInfo(
                        $"[AI:Path] Path FOUND — pos={transform.position:F1} " +
                        $"phase={GetAIPhase()}");
                _lastPathOk = pathOk;
            }

            // ── Stuck warning (detailed diagnostic) ──
            if (_stuckDetectTimer > StuckThreshold)
            {
                _debugLogTimer -= dt;
                if (_debugLogTimer <= 0f)
                {
                    _debugLogTimer = 2f;
                    float distToTarget = m_targetCreature != null
                        ? Vector3.Distance(transform.position, m_targetCreature.transform.position)
                        : -1f;

                    bool onGround = m_character != null && m_character.IsOnGround();
                    bool canMove = m_character != null && m_character.CanMove();

                    bool stayHome = _setup != null && _setup.GetStayHome();
                    bool hasHome = _setup != null && _setup.HasHomePosition();
                    bool hasPatrol = false;
                    Vector3 patrolPt = Vector3.zero;
                    if (GetPatrolPoint(out patrolPt)) hasPatrol = true;

                    CompanionsPlugin.Log.LogWarning(
                        $"[AI:STUCK] {_stuckDetectTimer:F1}s — " +
                        $"phase={GetAIPhase()} " +
                        $"target=\"{m_targetCreature?.m_name ?? "null"}\" targetDist={distToTarget:F1} " +
                        $"follow=\"{follow?.name ?? "null"}\" followDist={distToFollow:F1} " +
                        $"inAttack={inAttack} isAlerted={IsAlerted()} " +
                        $"charging={IsCharging()} pos={transform.position:F1} " +
                        $"onGround={onGround} canMove={canMove} pathOK={pathOk} " +
                        $"swimming={m_character?.IsSwimming() ?? false} swimTimer={_continuousSwimTimer:F1} " +
                        $"groundStuck={_groundStuckTimer:F1} stuckAttempts={_groundStuckAttempts} " +
                        $"stuckRecovery={_groundStuckRecoveryTimer:F1} " +
                        $"stayHome={stayHome} hasHome={hasHome} " +
                        $"patrol={hasPatrol} patrolPt={patrolPt:F1} " +
                        $"wanderRange={m_randomMoveRange:F0}");
                }
                return;
            }

            // ── Periodic state dump ──
            // Faster interval when ground stuck timer is accumulating (actively struggling)
            float logInterval = _groundStuckTimer > 0.5f ? 1f : DebugLogInterval;
            _debugLogTimer -= dt;
            if (_debugLogTimer <= 0f)
            {
                _debugLogTimer = logInterval;

                float vel = m_character?.GetVelocity().magnitude ?? 0f;
                float distToTarget = m_targetCreature != null
                    ? Vector3.Distance(transform.position, m_targetCreature.transform.position) : -1f;
                var weapon = (m_character as Humanoid)?.GetCurrentWeapon();
                var combat = _combat;

                int curStance = _setup != null ? _setup.GetCombatStance() : CompanionSetup.StanceBalanced;
                string stanceName;
                switch (curStance)
                {
                    case CompanionSetup.StanceAggressive: stanceName = "Aggressive"; break;
                    case CompanionSetup.StanceDefensive:  stanceName = "Defensive"; break;
                    case CompanionSetup.StancePassive:    stanceName = "Passive"; break;
                    case CompanionSetup.StanceMelee:      stanceName = "Melee"; break;
                    case CompanionSetup.StanceRanged:     stanceName = "Ranged"; break;
                    default:                              stanceName = "Balanced"; break;
                }

                CompanionsPlugin.Log.LogDebug(
                    $"[AI:State] phase={GetAIPhase()} " +
                    $"target=\"{m_targetCreature?.m_name ?? "null"}\"({distToTarget:F1}) " +
                    $"follow=\"{follow?.name ?? "null"}\"({distToFollow:F1}) " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                    $"combat={combat?.Phase} stance={stanceName} " +
                    $"vel={vel:F1} moved={moved:F2} pathOK={pathOk} " +
                    $"onGround={m_character?.IsOnGround() ?? false} " +
                    $"swimming={m_character?.IsSwimming() ?? false} swimTimer={_continuousSwimTimer:F1} " +
                    $"groundStuck={_groundStuckTimer:F1} stuckAttempts={_groundStuckAttempts} " +
                    $"combatStuck={_combatMoveStuckTimer:F1} " +
                    $"alerted={IsAlerted()} suppress={SuppressAttack} " +
                    $"pos={transform.position:F1}");
            }
        }

        /// <summary>
        /// Identifies the current AI phase for logging — what the companion is currently doing.
        /// </summary>
        private string GetAIPhase()
        {
            if (_groundStuckRecoveryTimer > 0f) return "StuckRecovery";
            if (m_targetCreature != null || m_targetStatic != null) return "Combat";
            if (_harvest != null && _harvest.IsActive) return "Harvest";
            if (_smelt != null && _smelt.IsActive) return "Smelt";
            if (_farm != null && _farm.IsActive) return "Farm";
            if (_repair != null && _repair.IsActive) return "Repair";
            if (IsRepairBuildActive) return $"RepairBuild:{_rbPhase}";
            if (IsRestockActive) return $"Restock:{_rsPhase}";
            if (_homestead != null && _homestead.IsActive) return "Homestead";
            if (_doorHandler != null && _doorHandler.IsActive) return "Door";
            if (_rest != null && _rest.IsResting) return "Resting";
            if (_rest != null && _rest.IsNavigating) return "RestNav";
            if (_returningHome) return "ReturnHome";
            if (m_follow != null) return "Follow";
            if (_setup != null && _setup.GetStayHome()) return "StayHome";
            return "Idle";
        }
    }
}
