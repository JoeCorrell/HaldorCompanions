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
        private const float TombstoneScanInterval = 5f;
        private const float TombstoneNavTimeoutMax = 120f;
        private const float TombstoneLootRange = 3f;

        // ══════════════════════════════════════════════════════════════════════
        //  Auto-Pickup (passive item collection like the Player)
        // ══════════════════════════════════════════════════════════════════════

        private int _autoPickupMask;
        private readonly Collider[] _autoPickupBuffer = new Collider[32];
        private const float AutoPickupRange = 2f;
        private const float AutoPickupPullSpeed = 15f;

        // ══════════════════════════════════════════════════════════════════════
        //  Drowning (damage when swimming with no stamina)
        // ══════════════════════════════════════════════════════════════════════

        internal static EffectList DrownEffects;   // copied from Player prefab
        private CompanionStamina _companionStamina;
        private float _drownDamageTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Constants
        // ══════════════════════════════════════════════════════════════════════

        private const float GiveUpTime = 30f;
        private const float UpdateTargetIntervalNear = 2f;
        private const float UpdateTargetIntervalFar = 6f;
        private const float SelfDefenseRange = 10f;
        private const float AlertRange = 9999f;

        // ══════════════════════════════════════════════════════════════════════
        //  Debug Logging
        // ══════════════════════════════════════════════════════════════════════

        private float _debugLogTimer;
        private float _stuckDetectTimer;
        private Vector3 _lastDebugPos;
        private float _targetLogTimer;

        private const float DebugLogInterval = 3f;
        private const float StuckThreshold = 5f;
        private const float FollowTeleportDist = 50f;

        // ── StayHome patrol enforcement ─────────────────────────────────────
        private float _homePatrolTimer;

        // ══════════════════════════════════════════════════════════════════════
        //  Formation Following
        // ══════════════════════════════════════════════════════════════════════

        private int _formationSlot = -1;
        private const float FormationOffset = 3f;
        private const float FormationCatchupDist = 15f;

        // ══════════════════════════════════════════════════════════════════════
        //  Cached Components (avoid GetComponent every frame)
        // ══════════════════════════════════════════════════════════════════════

        private CompanionSetup _setup;
        private HarvestController _harvest;
        private CombatController _combat;
        private RepairController _repair;
        private SmeltController _smelt;
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

        internal bool MoveToPoint(float dt, Vector3 point, float dist, bool run)
            => MoveTo(dt, point, dist, run);

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

                // Log navigation progress periodically
                _tombstoneScanTimer -= dt;
                if (_tombstoneScanTimer <= 0f)
                {
                    _tombstoneScanTimer = TombstoneScanInterval;
                    CompanionsPlugin.Log.LogDebug(
                        $"[AI] Navigating to tombstone — dist={dist:F1}m timeout={_tombstoneNavTimeout:F0}s");
                }

                // Run to tombstone
                bool run = dist > 10f;
                MoveTo(dt, tombPos, TombstoneLootRange - 0.5f, run);
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
                $"[AI] Looting tombstone — {totalInTomb} items to recover");

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
                        $"[AI]   Recovered: \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack}");
                }
                else
                {
                    failed++;
                    CompanionsPlugin.Log.LogWarning(
                        $"[AI]   Failed to recover: \"{item.m_shared?.m_name ?? "?"}\" x{item.m_stack} — inventory full?");
                }
            }

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Tombstone loot complete — recovered {moved}/{totalInTomb} items" +
                (failed > 0 ? $" ({failed} could not fit)" : "") +
                $", {tombInv.NrOfItems()} remaining in tombstone");

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
        private const float WanderMoveInterval = 5f;
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
                    MoveTo(dt, _setup.GetHomePosition(), 2f, distFromHome > 10f);
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

            // Auto-pickup — passively collect nearby items
            AutoPickup(dt);

            // Stuck-on-furniture recovery — if stuck on a Bed/Chair collider,
            // nudge off so pathfinding can resume (NavMesh doesn't cover furniture).
            UpdateFurnitureUnstuck(dt);

            // Follow-mode teleport — warp companion near player if too far away
            if (m_follow != null)
            {
                float distToFollow = Vector3.Distance(transform.position, m_follow.transform.position);
                if (distToFollow > FollowTeleportDist)
                    TeleportToFollowTarget();
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
                    MoveTo(dt, navTarget, 1.5f, true);
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
                        MoveTo(dt, PendingMoveTarget.Value, 1f, runToPoint);
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
                        MoveTo(dt, chestPos, 1.5f, runToChest);
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

                if ((_harvest != null && _harvest.IsActive) ||
                    (_repair != null && _repair.IsActive) ||
                    (_smelt != null && _smelt.IsActive) ||
                    (_homestead != null && _homestead.IsActive) ||
                    (_doorHandler != null && _doorHandler.IsActive))
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
                // When a controller is actively moving to a target, it owns
                // movement exclusively. Letting Follow() or IdleMovement() run
                // here causes dual-control jitter — Follow's internal stop
                // distance (~3m) cancels the controller's movement commands.
                if ((_harvest != null && _harvest.IsActive) ||
                    (_repair != null && _repair.IsActive) ||
                    (_smelt != null && _smelt.IsActive) ||
                    (_homestead != null && _homestead.IsActive) ||
                    (_doorHandler != null && _doorHandler.IsActive))
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

            // Has target — combat movement + attack
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
                    // Defensive: only engage enemies within 5m OR targeting the player
                    if (stance == CompanionSetup.StanceDefensive)
                    {
                        float eDist = Vector3.Distance(transform.position, enemy.transform.position);
                        bool targetsPlayer = false;
                        var eAI = enemy.GetBaseAI();
                        if (eAI != null)
                        {
                            var aiTarget = eAI.GetTargetCreature();
                            if (aiTarget != null && aiTarget.IsPlayer())
                                targetsPlayer = true;
                        }
                        if (eDist > 5f && !targetsPlayer)
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

            // ── Alert range check (leash to follow target) ──
            if (m_targetCreature != null)
            {
                if (GetPatrolPoint(out var point))
                {
                    if (Vector3.Distance(m_targetCreature.transform.position, point) > AlertRange)
                        m_targetCreature = null;
                }
                else if (m_follow != null &&
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

                    MoveTo(dt, moveTarget, 0f, IsAlerted());
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
                else if (MoveTo(dt, m_lastKnownTargetPos, 0f, IsAlerted()))
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

            CompanionsPlugin.Log.LogInfo(
                $"[AI] Teleported companion to follow target at {spawnPos:F1} " +
                $"(was {FollowTeleportDist:F0}m+ away)");
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

            // Too far away → vanilla follow to catch up first
            if (distToTarget > FormationCatchupDist || _formationSlot < 0)
            {
                Follow(target, dt);
                return;
            }

            // Close to target → vanilla follow (stops at ~3m, no formation jitter)
            if (distToTarget < 5f)
            {
                Follow(target, dt);
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
                return;
            }

            // Move to formation point — only run when far from player
            bool shouldRun = distToTarget > 10f;
            MoveTo(dt, formationPos, 1f, shouldRun);
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
                MoveTo(dt, _healTarget.transform.position, 0f, dist > 10f);
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
            bool homesteading = _homestead != null && _homestead.IsActive;
            bool handlingDoor = _doorHandler != null && _doorHandler.IsActive;

            // StayHome with wander OFF: companion is intentionally stationary
            // at home — don't flag as stuck. Also suppress during return-home.
            // Follow OFF + StayHome OFF: companion is idle with no movement target.
            bool stayingHome = _setup != null && _setup.GetStayHome()
                && !_setup.GetWander() && !harvesting && !smelting;
            bool followOffIdle = _setup != null && !_setup.GetFollow()
                && !_setup.GetStayHome() && !harvesting && !smelting;
            bool intentionallyStationary = stayingHome || _returningHome || followOffIdle;

            if (moved < 0.1f && !inAttack && !atFollowDist && !harvesting
                && !repairing && !smelting && !homesteading && !handlingDoor && !intentionallyStationary)
                _stuckDetectTimer += dt;
            else
                _stuckDetectTimer = 0f;

            if (_stuckDetectTimer > StuckThreshold)
            {
                _debugLogTimer -= dt;
                if (_debugLogTimer <= 0f)
                {
                    _debugLogTimer = 2f;
                    float distToTarget = m_targetCreature != null
                        ? Vector3.Distance(transform.position, m_targetCreature.transform.position)
                        : -1f;

                    // Diagnostic: check why movement is failing
                    bool onGround = m_character != null && m_character.IsOnGround();
                    bool canMove = m_character != null && m_character.CanMove();
                    bool pathResult = FoundPath();

                    bool stayHome = _setup != null && _setup.GetStayHome();
                    bool hasHome = _setup != null && _setup.HasHomePosition();
                    bool hasPatrol = false;
                    Vector3 patrolPt = Vector3.zero;
                    if (GetPatrolPoint(out patrolPt)) hasPatrol = true;

                    CompanionsPlugin.Log.LogWarning(
                        $"[CompanionAI] STUCK {_stuckDetectTimer:F1}s — " +
                        $"target=\"{m_targetCreature?.m_name ?? "null"}\" targetDist={distToTarget:F1} " +
                        $"follow=\"{follow?.name ?? "null"}\" followDist={distToFollow:F1} " +
                        $"inAttack={inAttack} isAlerted={IsAlerted()} " +
                        $"charging={IsCharging()} pos={transform.position:F1} " +
                        $"onGround={onGround} canMove={canMove} pathOK={pathResult} " +
                        $"stayHome={stayHome} hasHome={hasHome} " +
                        $"patrol={hasPatrol} patrolPt={patrolPt:F1} " +
                        $"wanderRange={m_randomMoveRange:F0}");
                }
                return;
            }

            // Periodic state dump
            _debugLogTimer -= dt;
            if (_debugLogTimer <= 0f)
            {
                _debugLogTimer = DebugLogInterval;

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
                    $"[CompanionAI] target=\"{m_targetCreature?.m_name ?? "null"}\"({distToTarget:F1}) " +
                    $"follow=\"{follow?.name ?? "null"}\"({distToFollow:F1}) " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                    $"combat={combat?.Phase} stance={stanceName} " +
                    $"alerted={IsAlerted()} suppress={SuppressAttack} " +
                    $"vel={m_character?.GetVelocity().magnitude ?? 0f:F1} " +
                    $"moved={moved:F2}");
            }
        }
    }
}
