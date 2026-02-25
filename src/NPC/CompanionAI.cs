using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Combat AI and shared infrastructure for companions. Runs alongside MonsterAI and handles:
    /// - Position tracking (sliding window for stuck detection across all systems)
    /// - Follow zone state (hysteresis-based following)
    /// - Cached enemy scanning (0.25s refresh, used by CompanionHarvest + combat)
    /// - Combat state tracking (InCombat flag with 5s exit delay)
    /// - Blocking and parrying incoming melee attacks
    /// - Dodge rolling to evade attacks
    /// - Proactive door opening when moving slowly
    /// - Follow stuck escalation (door → jump → teleport)
    /// - Stamina-gated combat (via CompanionStamina + Harmony bridge)
    /// - Auto pickup of nearby ground items
    /// - Furnace/kiln fueling when in Stay mode
    /// - Encumbered movement + animation sync
    /// - Idle facing when near player
    /// MonsterAI still handles target selection, weapon equipping, and attack initiation.
    /// </summary>
    public class CompanionAI : MonoBehaviour
    {
        // ══════════════════════════════════════════════════════════════════════
        //  Inner Types
        // ══════════════════════════════════════════════════════════════════════

        internal enum FollowZone { Inner, Comfort, CatchUp, Sprint }
        internal enum StuckAction { None, TryDoor, Jump, Teleport }

        /// <summary>
        /// Sliding-window position tracker. Records position samples at fixed intervals
        /// and provides movement analysis (distance, speed, oscillation detection).
        /// Used by Follow_Patch and CompanionHarvest for stuck detection.
        /// Ring buffer of 20 samples at 0.25s intervals = 5s window.
        /// </summary>
        internal class PositionTracker
        {
            private struct Sample
            {
                public Vector3 Position;
                public float Time;
            }

            private readonly Sample[] _buffer;
            private int _head;
            private int _count;
            private float _sampleTimer;
            private const float SampleRate = 0.25f;

            public PositionTracker(int capacity = 20)
            {
                _buffer = new Sample[capacity];
            }

            /// <summary>Call from Update() to record samples at SampleRate intervals.</summary>
            public void Update(Vector3 position, float time, float dt)
            {
                _sampleTimer -= dt;
                if (_sampleTimer > 0f) return;
                _sampleTimer = SampleRate;
                Record(position, time);
            }

            public void Record(Vector3 position, float time)
            {
                _buffer[_head] = new Sample { Position = position, Time = time };
                _head = (_head + 1) % _buffer.Length;
                if (_count < _buffer.Length) _count++;
            }

            /// <summary>Total distance traveled in the last N seconds.</summary>
            public float DistanceOverWindow(float seconds)
            {
                if (_count < 2) return 0f;
                float now = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length].Time;
                float cutoff = now - seconds;

                float total = 0f;
                int prevIdx = -1;
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                    if (_buffer[idx].Time < cutoff) continue;
                    if (prevIdx >= 0)
                        total += Vector3.Distance(_buffer[prevIdx].Position, _buffer[idx].Position);
                    prevIdx = idx;
                }
                return total;
            }

            /// <summary>Mean velocity over the last N seconds.</summary>
            public float AverageSpeed(float seconds)
            {
                if (_count < 2) return 0f;
                return DistanceOverWindow(seconds) / Mathf.Max(seconds, 0.01f);
            }

            /// <summary>
            /// True if the companion has revisited the same area (within radius)
            /// 3+ times within the window, indicating oscillation.
            /// </summary>
            public bool IsOscillating(float radius, float window)
            {
                if (_count < 6) return false;
                float now = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length].Time;
                float cutoff = now - window;

                float radiusSq = radius * radius;
                int windowStart = -1;
                int windowCount = 0;

                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                    if (_buffer[idx].Time >= cutoff)
                    {
                        if (windowStart < 0) windowStart = i;
                        windowCount++;
                    }
                }

                if (windowCount < 6) return false;

                for (int i = windowStart; i < windowStart + windowCount; i++)
                {
                    int idxI = (_head - _count + i + _buffer.Length) % _buffer.Length;
                    int revisits = 0;
                    for (int j = windowStart; j < windowStart + windowCount; j++)
                    {
                        if (i == j) continue;
                        int idxJ = (_head - _count + j + _buffer.Length) % _buffer.Length;
                        float dx = _buffer[idxI].Position.x - _buffer[idxJ].Position.x;
                        float dz = _buffer[idxI].Position.z - _buffer[idxJ].Position.z;
                        if (dx * dx + dz * dz < radiusSq)
                            revisits++;
                    }
                    if (revisits >= 3) return true;
                }
                return false;
            }

            /// <summary>Clear all samples. Call on teleport or mode change.</summary>
            public void Reset()
            {
                _count = 0;
                _head = 0;
                _sampleTimer = 0f;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Reflection
        // ══════════════════════════════════════════════════════════════════════

        private static readonly FieldInfo _blockingField =
            AccessTools.Field(typeof(Character), "m_blocking");

        private static readonly FieldInfo _leftItemField =
            AccessTools.Field(typeof(Humanoid), "m_leftItem");

        // ══════════════════════════════════════════════════════════════════════
        //  References
        // ══════════════════════════════════════════════════════════════════════

        private Character        _character;
        private Humanoid         _humanoid;
        private MonsterAI        _ai;
        private ZNetView         _nview;
        private CompanionStamina _stamina;
        private ZSyncAnimation   _zanim;
        private Rigidbody        _body;

        // ══════════════════════════════════════════════════════════════════════
        //  Shared Infrastructure
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Position tracker for stuck detection across all systems.</summary>
        internal PositionTracker Tracker { get; private set; }

        /// <summary>Current follow zone for hysteresis-based following.</summary>
        internal FollowZone CurrentFollowZone;

        /// <summary>Cached nearest enemy character (refreshed every 0.25s).</summary>
        internal Character NearestEnemy { get; private set; }

        /// <summary>Distance to cached nearest enemy.</summary>
        internal float NearestEnemyDist { get; private set; } = float.MaxValue;

        /// <summary>True when in combat (blocking, dodging, or enemies within 10m). 5s exit delay.</summary>
        internal bool InCombat { get; private set; }

        private float _enemyScanTimer;
        private float _combatTimer;
        private float _followStuckTimer;
        private int   _followStuckTier;

        private const float EnemyScanInterval = 0.25f;
        private const float CombatExitDelay   = 5f;
        private const float CombatRange       = 10f;

        // ══════════════════════════════════════════════════════════════════════
        //  Blocking state
        // ══════════════════════════════════════════════════════════════════════

        private bool      _isBlocking;
        private float     _blockHoldTimer;
        private float     _blockCooldown;
        private float     _blockDelayTimer;
        private Character _blockTarget;

        private const float BlockHoldDuration    = 0.5f;
        private const float BlockCooldownTime    = 1.5f;
        private const float BlockReactDelay      = 0.12f;
        private const float BlockDetectRange     = 6f;
        private const float BlockChanceClose     = 0.9f;  // at 1.5m
        private const float BlockChanceFar       = 0.5f;  // at 6m

        // ══════════════════════════════════════════════════════════════════════
        //  Dodge state
        // ══════════════════════════════════════════════════════════════════════

        private bool  _inDodge;
        private float _dodgeTimer;
        private float _dodgeCooldown;

        private const float DodgeChance          = 0.15f;
        private const float DodgeChanceNoShield  = 0.35f;
        private const float DodgeCooldownTime    = 2.5f;
        private const float DodgeStaminaCost     = 15f;
        private const float DodgeDuration        = 0.6f;

        // ══════════════════════════════════════════════════════════════════════
        //  Idle facing
        // ══════════════════════════════════════════════════════════════════════

        private float _idleTimer;
        private const float IdleFaceDelay = 1.5f;

        // ══════════════════════════════════════════════════════════════════════
        //  Encumbrance
        // ══════════════════════════════════════════════════════════════════════

        private float _origWalkSpeed;
        private float _origRunSpeed;
        private bool  _speedsStored;
        private bool  _isEncumbered;

        // ══════════════════════════════════════════════════════════════════════
        //  Auto pickup
        // ══════════════════════════════════════════════════════════════════════

        private bool  _autoPickupEnabled;
        private float _pickupTimer;
        private int   _autoPickupMask = -1;

        private const float AutoPickupRange    = 2f;
        private const float AutoPickupInterval = 0.25f;

        // ══════════════════════════════════════════════════════════════════════
        //  Door interaction
        // ══════════════════════════════════════════════════════════════════════

        private float _doorCooldown;
        private float _doorScanTimer;

        private const float DoorCooldownTime     = 2f;
        private const float DoorScanInterval     = 1f;
        private const float DoorVelocityThreshold = 0.3f;

        // ══════════════════════════════════════════════════════════════════════
        //  Furnace fueling
        // ══════════════════════════════════════════════════════════════════════

        private float _fuelTimer;
        private const float FuelCheckInterval = 2f;
        private const float FuelRange         = 3f;

        // ══════════════════════════════════════════════════════════════════════
        //  Properties
        // ══════════════════════════════════════════════════════════════════════

        public bool IsEncumbered => _isEncumbered;

        public bool AutoPickupEnabled
        {
            get => _autoPickupEnabled;
            set
            {
                _autoPickupEnabled = value;
                if (_nview != null && _nview.GetZDO() != null)
                    _nview.GetZDO().Set(CompanionSetup.AutoPickupHash, value);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _character = GetComponent<Character>();
            _humanoid  = GetComponent<Humanoid>();
            _ai        = GetComponent<MonsterAI>();
            _nview     = GetComponent<ZNetView>();
            _stamina   = GetComponent<CompanionStamina>();
            _zanim     = GetComponent<ZSyncAnimation>();
            _body      = GetComponent<Rigidbody>();

            Tracker = new PositionTracker(20);
        }

        private void Start()
        {
            if (_nview != null && _nview.GetZDO() != null)
                _autoPickupEnabled = _nview.GetZDO().GetBool(CompanionSetup.AutoPickupHash, false);

            _autoPickupMask = LayerMask.GetMask("item");
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            if (!_speedsStored)
            {
                _origWalkSpeed = _character.m_walkSpeed;
                _origRunSpeed  = _character.m_runSpeed;
                _speedsStored  = true;
            }

            float dt = Time.deltaTime;

            // Record position for stuck detection (shared across all systems)
            Tracker.Update(transform.position, Time.time, dt);

            // Shared infrastructure updates
            UpdateEnemyCache(dt);
            UpdateCombatState(dt);
            UpdateProactiveDoors(dt);

            UpdateEncumbrance();

            _blockCooldown -= dt;
            _dodgeCooldown -= dt;
            _doorCooldown  -= dt;

            if (_inDodge)
            {
                _dodgeTimer -= dt;
                if (_dodgeTimer <= 0f)
                    _inDodge = false;
                return;
            }

            UpdateBlocking(dt);
            UpdateIdleFacing(dt);
            UpdateAutoPickup(dt);
            UpdateFueling(dt);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Shared Infrastructure — Enemy Cache
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateEnemyCache(float dt)
        {
            _enemyScanTimer -= dt;
            if (_enemyScanTimer > 0f) return;
            _enemyScanTimer = EnemyScanInterval;

            NearestEnemy = null;
            NearestEnemyDist = float.MaxValue;

            var all = Character.GetAllCharacters();
            for (int i = 0; i < all.Count; i++)
            {
                var c = all[i];
                if (c == _character || c == null || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;

                float dist = Vector3.Distance(transform.position, c.transform.position);
                if (dist < NearestEnemyDist)
                {
                    NearestEnemy = c;
                    NearestEnemyDist = dist;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Shared Infrastructure — Combat State
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCombatState(float dt)
        {
            bool combatActive = _isBlocking || _inDodge ||
                               (NearestEnemy != null && NearestEnemyDist < CombatRange);

            if (combatActive)
            {
                InCombat = true;
                _combatTimer = CombatExitDelay;
            }
            else
            {
                _combatTimer -= dt;
                if (_combatTimer <= 0f)
                    InCombat = false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Shared Infrastructure — Proactive Door Opening
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Continuous door check: when velocity is low but companion is trying to move,
        /// open nearby doors proactively. Works during following, gathering, and combat.
        /// </summary>
        private void UpdateProactiveDoors(float dt)
        {
            _doorScanTimer -= dt;
            if (_doorScanTimer > 0f) return;
            _doorScanTimer = DoorScanInterval;

            if (_character == null) return;
            float speed = _character.GetVelocity().magnitude;
            if (speed > DoorVelocityThreshold) return;

            // Only open doors when we're supposed to be moving somewhere
            bool isMoving = _character.GetMoveDir().sqrMagnitude > 0.01f;
            if (!isMoving) return;

            TryOpenNearbyDoor();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Shared Infrastructure — Follow Stuck Escalation
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Evaluate follow stuck state using PositionTracker. Called from Follow_Patch.
        /// Handles doors and jumps internally; returns Teleport for Follow_Patch to handle.
        /// Escalation: 1.5s → door, 2.5s → jump, 4s → teleport.
        /// </summary>
        internal StuckAction UpdateFollowStuck(float dt, float dist)
        {
            // Not stuck if close to target or moving well
            if (dist < 2f)
            {
                _followStuckTimer = 0f;
                _followStuckTier = 0;
                return StuckAction.None;
            }

            float moved = Tracker.DistanceOverWindow(2f);
            if (moved > 1f)
            {
                _followStuckTimer = 0f;
                _followStuckTier = 0;
                return StuckAction.None;
            }

            _followStuckTimer += dt;

            // Tier 2: 4s → teleport (returned to caller)
            if (_followStuckTimer >= 4f && _followStuckTier < 3)
            {
                _followStuckTimer = 0f;
                _followStuckTier = 0;
                return StuckAction.Teleport;
            }

            // Tier 1: 2.5s → jump
            if (_followStuckTimer >= 2.5f && _followStuckTier < 2)
            {
                _followStuckTier = 2;
                if (_character != null) _character.Jump(false);
                return StuckAction.Jump;
            }

            // Tier 0: 1.5s → try door
            if (_followStuckTimer >= 1.5f && _followStuckTier < 1)
            {
                _followStuckTier = 1;
                TryOpenNearbyDoor();
                return StuckAction.TryDoor;
            }

            return StuckAction.None;
        }

        /// <summary>
        /// Called after a teleport to reset all tracking state.
        /// </summary>
        internal void OnTeleported()
        {
            Tracker.Reset();
            CurrentFollowZone = FollowZone.Comfort;
            _followStuckTimer = 0f;
            _followStuckTier = 0;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Blocking / Parrying / Dodging
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateBlocking(float dt)
        {
            if (_character.InAttack() || _character.IsStaggering())
            {
                if (_isBlocking) ReleaseBlock();
                return;
            }

            if (_isBlocking)
            {
                _blockHoldTimer -= dt;
                if (_blockHoldTimer <= 0f)
                    ReleaseBlock();
                else
                    FaceBlockTarget();
                return;
            }

            if (_blockTarget != null)
            {
                _blockDelayTimer -= dt;
                if (_blockDelayTimer <= 0f)
                {
                    StartBlock();
                    _blockTarget = null;
                }
                else
                {
                    FaceBlockTarget();
                }
                return;
            }

            if (_blockCooldown > 0f) return;

            var attacker = FindAttackingEnemy();
            if (attacker == null) return;

            bool hasShield = HasBlocker();
            float roll = Random.value;

            if (hasShield)
            {
                // Distance-scaled block chance: closer enemies → higher chance
                float blockChance = Mathf.Lerp(BlockChanceFar, BlockChanceClose,
                    Mathf.InverseLerp(BlockDetectRange, 1.5f, NearestEnemyDist));

                if (roll < blockChance)
                {
                    _blockTarget     = attacker;
                    _blockDelayTimer = BlockReactDelay;
                }
                else if (roll < blockChance + DodgeChance)
                {
                    if (_dodgeCooldown <= 0f)
                        StartDodge(attacker);
                    else
                        _blockCooldown = BlockCooldownTime * 0.5f;
                }
                else
                {
                    _blockCooldown = BlockCooldownTime * 0.5f;
                }
            }
            else
            {
                // No shield: higher dodge chance to compensate
                if (roll < DodgeChanceNoShield)
                {
                    if (_dodgeCooldown <= 0f)
                        StartDodge(attacker);
                    else
                        _blockCooldown = BlockCooldownTime * 0.5f;
                }
                else
                {
                    _blockCooldown = BlockCooldownTime * 0.5f;
                }
            }
        }

        private void StartBlock()
        {
            if (_blockingField == null) return;
            _isBlocking     = true;
            _blockHoldTimer = BlockHoldDuration;
            _blockCooldown  = BlockCooldownTime;
            _blockingField.SetValue(_character, true);
        }

        private void ReleaseBlock()
        {
            if (_blockingField == null) return;
            _isBlocking  = false;
            _blockTarget = null;
            _blockingField.SetValue(_character, false);
        }

        private void FaceBlockTarget()
        {
            if (_blockTarget == null || !_blockTarget) return;
            Vector3 dir = _blockTarget.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Dodge
        // ══════════════════════════════════════════════════════════════════════

        private void StartDodge(Character attacker)
        {
            if (_zanim == null) return;
            if (_stamina != null && !_stamina.UseStamina(DodgeStaminaCost)) return;

            Vector3 toAttacker = attacker.transform.position - transform.position;
            toAttacker.y = 0f;
            toAttacker.Normalize();

            // Generate 3 candidate dodge directions
            Vector3 left = Vector3.Cross(toAttacker, Vector3.up).normalized;
            Vector3 right = -left;
            Vector3 backward = -toAttacker;

            Vector3 myPos = transform.position;
            Vector3 playerDir = Vector3.zero;
            if (Player.m_localPlayer != null)
            {
                playerDir = Player.m_localPlayer.transform.position - myPos;
                playerDir.y = 0f;
                playerDir.Normalize();
            }

            // Score each candidate by terrain flatness and player visibility
            Vector3 bestDir = left;
            float bestScore = float.MinValue;
            bool anyGroundValid = false;

            Vector3[] candidates = { left, right, backward };
            foreach (var dir in candidates)
            {
                float score = 0f;
                Vector3 candidatePos = myPos + dir * 2f;

                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetSolidHeight(candidatePos, out groundHeight))
                    {
                        float heightDiff = Mathf.Abs(groundHeight - myPos.y);
                        score -= heightDiff * 2f;
                        anyGroundValid = true;
                    }
                    else
                    {
                        score -= 10f;
                    }
                }

                // Prefer directions that keep the player in view
                if (playerDir.sqrMagnitude > 0.01f)
                    score += Vector3.Dot(dir, playerDir) * 0.5f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }

            // Fallback to random perpendicular if ground checks all failed
            if (!anyGroundValid)
                bestDir = Random.value > 0.5f ? left : right;

            if (bestDir.sqrMagnitude < 0.01f)
                bestDir = transform.right;
            bestDir.Normalize();

            transform.rotation = Quaternion.LookRotation(bestDir);
            if (_body != null)
                _body.rotation = transform.rotation;

            _zanim.SetTrigger("dodge");

            _inDodge       = true;
            _dodgeTimer    = DodgeDuration;
            _dodgeCooldown = DodgeCooldownTime;
            _blockCooldown = BlockCooldownTime;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Encumbrance
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateEncumbrance()
        {
            if (_humanoid == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            bool overweight = inv.GetTotalWeight() > CompanionTierData.MaxCarryWeight;
            if (overweight && !_isEncumbered)
            {
                _isEncumbered = true;
                _character.m_walkSpeed = _origWalkSpeed * 0.5f;
                _character.m_runSpeed  = _origWalkSpeed * 0.5f;
                if (_zanim != null) _zanim.SetBool("encumbered", true);
            }
            else if (!overweight && _isEncumbered)
            {
                _isEncumbered = false;
                _character.m_walkSpeed = _origWalkSpeed;
                _character.m_runSpeed  = _origRunSpeed;
                if (_zanim != null) _zanim.SetBool("encumbered", false);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Auto Pickup
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateAutoPickup(float dt)
        {
            if (!_autoPickupEnabled || _humanoid == null) return;

            _pickupTimer -= dt;
            if (_pickupTimer > 0f) return;
            _pickupTimer = AutoPickupInterval;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            Vector3 center = transform.position + Vector3.up;
            var colliders = Physics.OverlapSphere(center, AutoPickupRange, _autoPickupMask);

            foreach (var col in colliders)
            {
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null || !itemDrop.m_autoPickup) continue;

                var itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;

                if (!itemDrop.CanPickup())
                {
                    itemDrop.RequestOwn();
                    continue;
                }

                if (!inv.CanAddItem(itemDrop.m_itemData)) continue;

                float itemWeight = itemDrop.m_itemData.GetWeight();
                if (itemWeight + inv.GetTotalWeight() > CompanionTierData.MaxCarryWeight)
                    continue;

                float dist = Vector3.Distance(itemDrop.transform.position, center);
                if (dist < AutoPickupRange)
                    _humanoid.Pickup(itemDrop.gameObject);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Door Interaction
        // ══════════════════════════════════════════════════════════════════════

        internal void TryOpenNearbyDoor()
        {
            if (_humanoid == null) return;
            if (_doorCooldown > 0f) return;

            var colliders = Physics.OverlapSphere(transform.position, 2.5f);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                var door = col.GetComponentInParent<Door>();
                if (door == null) continue;

                bool result = door.Interact(_humanoid, false, false);
                if (result)
                {
                    _doorCooldown = DoorCooldownTime;
                    if (_zanim != null) _zanim.SetTrigger("interact");
                    return;
                }
            }

            _doorCooldown = DoorCooldownTime * 0.5f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Furnace / Kiln Fueling (Stay mode only)
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateFueling(float dt)
        {
            if (_nview == null || _humanoid == null) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;
            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode != CompanionSetup.ModeStay) return;

            _fuelTimer -= dt;
            if (_fuelTimer > 0f) return;
            _fuelTimer = FuelCheckInterval;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            // Fuel smelters (furnaces, kilns)
            foreach (var smelter in FindObjectsByType<Smelter>(FindObjectsSortMode.None))
            {
                if (smelter == null) continue;
                float dist = Vector3.Distance(transform.position, smelter.transform.position);
                if (dist > FuelRange) continue;

                var smelterNview = smelter.GetComponent<ZNetView>();
                if (smelterNview == null || !smelterNview.IsValid()) continue;

                var smelterZdo = smelterNview.GetZDO();

                // Add fuel
                float fuel = smelterZdo.GetFloat(ZDOVars.s_fuel);
                if (smelter.m_fuelItem != null && fuel < smelter.m_maxFuel - 1)
                {
                    string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
                    var fuelItem = inv.GetItem(fuelName);
                    if (fuelItem != null)
                    {
                        inv.RemoveOneItem(fuelItem);
                        smelterNview.InvokeRPC("RPC_AddFuel");
                        return;
                    }
                }

                // Add ore
                int queued = smelterZdo.GetInt(ZDOVars.s_queued);
                if (queued < smelter.m_maxOre)
                {
                    foreach (var conversion in smelter.m_conversion)
                    {
                        if (conversion.m_from == null) continue;
                        string oreName = conversion.m_from.m_itemData.m_shared.m_name;
                        var oreItem = inv.GetItem(oreName);
                        if (oreItem != null)
                        {
                            inv.RemoveOneItem(oreItem);
                            smelterNview.InvokeRPC("RPC_AddOre",
                                conversion.m_from.gameObject.name);
                            return;
                        }
                    }
                }
            }

            // Fuel fireplaces
            foreach (var fireplace in FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (fireplace == null || fireplace.m_fuelItem == null) continue;
                float dist = Vector3.Distance(transform.position, fireplace.transform.position);
                if (dist > FuelRange) continue;

                var fpNview = fireplace.GetComponent<ZNetView>();
                if (fpNview == null || !fpNview.IsValid()) continue;

                string fpFuelName = fireplace.m_fuelItem.m_itemData.m_shared.m_name;
                var fpFuelItem = inv.GetItem(fpFuelName);
                if (fpFuelItem != null)
                {
                    bool result = fireplace.Interact(_humanoid, false, false);
                    if (result) return;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private bool HasBlocker()
        {
            if (_humanoid == null) return false;
            var left = _leftItemField?.GetValue(_humanoid) as ItemDrop.ItemData;
            if (left != null && left.m_shared.m_blockPower > 0f) return true;
            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon != null && weapon.m_shared.m_blockPower > 0f) return true;
            return false;
        }

        /// <summary>
        /// Find the nearest enemy that is currently attacking within block range.
        /// Uses the cached NearestEnemy for efficiency — checks InAttack() in real-time.
        /// </summary>
        private Character FindAttackingEnemy()
        {
            if (NearestEnemy == null || NearestEnemy.IsDead()) return null;
            if (NearestEnemyDist > BlockDetectRange) return null;
            if (!NearestEnemy.InAttack()) return null;
            return NearestEnemy;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Idle facing — match player direction when standing still
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdleFacing(float dt)
        {
            if (_ai == null) return;
            var follow = _ai.GetFollowTarget();
            if (follow == null) return;

            float dist = Vector3.Distance(transform.position, follow.transform.position);
            var moveDir = _character.GetMoveDir();
            bool isMoving = moveDir.sqrMagnitude > 0.01f;

            if (isMoving || dist > 3f) { _idleTimer = 0f; return; }
            if (_character.InAttack() || _isBlocking) return;

            _idleTimer += dt;
            if (_idleTimer < IdleFaceDelay) return;

            Vector3 playerForward = follow.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return;

            Quaternion target = Quaternion.LookRotation(playerForward);
            transform.rotation = Quaternion.Slerp(transform.rotation, target, dt * 2f);
        }
    }
}
