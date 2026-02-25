using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Handles resource gathering for companions.
    /// Action modes: 1 = Gather Wood, 2 = Gather Stone, 3 = Gather Ore.
    /// Each mode targets only its specific resource type, equips the right tool,
    /// navigates to the resource, and attacks it to harvest.
    /// </summary>
    public class CompanionHarvest : MonoBehaviour
    {
        private enum HarvestState { Idle, Moving, Attacking, CollectingDrops }

        internal enum ResourceType { None, Wood, Stone, Ore }

        private enum BlacklistReason { Unreachable, Oscillation, PathFailed, ToolTier }

        private struct BlacklistEntry
        {
            public float ExpireTime;
            public int FailCount;
            public BlacklistReason Reason;
        }

        // ── References ────────────────────────────────────────────────────
        private ZNetView       _nview;
        private Humanoid       _humanoid;
        private MonsterAI      _ai;
        private CompanionSetup _setup;
        private CompanionAI    _companionAI;
        private CompanionTalk  _talk;
        private Character      _character;

        // ── State ─────────────────────────────────────────────────────────
        private HarvestState _state = HarvestState.Idle;
        private GameObject   _currentTarget;
        private ResourceType _currentResourceType;
        private int          _lastMode = int.MinValue;

        // Paused target (enemy interrupt — resume after combat)
        private GameObject   _pausedTarget;
        private ResourceType _pausedResourceType;

        // Timers and counters
        private float _scanTimer;
        private float _attackCooldown;
        private float _targetLockTimer;
        private float _dropCollectTimer;
        private float _dropCollectPickupTimer;
        private int   _dropCollectEmptyChecks;

        // Stuck escalation (PositionTracker-based)
        private float _harvestStuckTimer;
        private int   _harvestStuckTier;
        private bool  _offsetLeft;

        // Pre-attack validation
        private int _losFailCount;

        // Positions
        private Vector3 _lastTargetPos;
        private Vector3 _dropCollectCenter;

        // Warnings (one-shot per cycle)
        private bool _toolWarned;
        private bool _invFullWarned;
        private bool _noToolSpoken;
        private bool _noResourceSpoken;

        private int        _itemMask = -1;
        private GameObject _waypoint;

        private readonly Dictionary<int, BlacklistEntry> _blacklist =
            new Dictionary<int, BlacklistEntry>();

        // ── Properties ────────────────────────────────────────────────────

        /// <summary>True when actively navigating to or attacking a resource.</summary>
        internal bool IsActivelyHarvesting =>
            _state == HarvestState.Moving || _state == HarvestState.Attacking;

        /// <summary>True when collecting dropped items after destroying a resource.</summary>
        internal bool IsCollectingDrops => _state == HarvestState.CollectingDrops;

        // ── Constants ─────────────────────────────────────────────────────
        private const float ScanInterval      = 0.5f;
        private const float ScanRangeClose    = 20f;
        private const float ScanRangeFar      = 50f;
        private const float AttackRange       = 2.6f;
        private const float AttackCooldown    = 1.5f;
        private const float MaxPlayerDistance  = CompanionSetup.MaxLeashDistance;
        private const float PlayerResourceRange = CompanionSetup.MaxLeashDistance;
        private const float TargetLockDuration = 3f;
        private const float LowDurabilityPct  = 0.1f;

        private const float PreferredSurfaceDistance = 1.0f;
        private const float MinSurfaceDistance       = 0.65f;
        private const float MaxSurfaceDistance       = 1.35f;
        private const float MinTargetRadius          = 0.45f;
        private const float MaxTargetRadius          = 1.2f;
        private const float MaxDownwardAttackOffset  = 1.2f;
        private const float MaxUpwardAttackOffset    = 2.0f;

        private const float DropCollectSearchRadius   = 6f;
        private const float DropCollectMoveRange      = 1.8f;
        private const float DropCollectPickupInterval = 0.1f;
        private const float DropCollectMaxDuration    = 6f;
        private const float DropCollectRangeCap       = 10f;
        private const int   DropCollectEmptyCheckGoal = 3;
        private const int   MaxLOSFails               = 3;

        private const float BlacklistUnreachable = 15f;
        private const float BlacklistOscillation = 20f;
        private const float BlacklistPathFailed  = 10f;

        // ══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview       = GetComponent<ZNetView>();
            _humanoid    = GetComponent<Humanoid>();
            _ai          = GetComponent<MonsterAI>();
            _setup       = GetComponent<CompanionSetup>();
            _companionAI = GetComponent<CompanionAI>();
            _talk        = GetComponent<CompanionTalk>();
            _character   = GetComponent<Character>();
            _itemMask    = LayerMask.GetMask("item");
            if (_itemMask == 0) _itemMask = -1;
            _lastTargetPos     = transform.position;
            _dropCollectCenter = transform.position;

            _waypoint = new GameObject("HC_HarvestWaypoint");
            Object.DontDestroyOnLoad(_waypoint);
        }

        private void OnDestroy()
        {
            if (_waypoint != null) Object.Destroy(_waypoint);
            if (_setup != null) _setup.SuppressAutoEquip = false;
        }

        private void FixedUpdate()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode != _lastMode)
            {
                _lastMode = mode;
                NotifyActionModeChanged();
            }

            if (mode < CompanionSetup.ModeGatherWood || mode > CompanionSetup.ModeGatherOre)
            {
                if (_state != HarvestState.Idle) StopHarvesting();
                return;
            }

            if (_character != null && (_character.IsDead() || _character.InAttack()))
                return;

            if (HasEnemyNearby())
            {
                if (_state != HarvestState.Idle) PauseForEnemy();
                return;
            }

            if (Player.m_localPlayer != null)
            {
                float playerDist = Vector3.Distance(
                    transform.position, Player.m_localPlayer.transform.position);
                if (playerDist > MaxPlayerDistance)
                {
                    if (_state != HarvestState.Idle) StopHarvesting();
                    return;
                }
            }

            if (_targetLockTimer > 0f) _targetLockTimer -= Time.deltaTime;

            switch (_state)
            {
                case HarvestState.Idle:           UpdateIdle(mode);          break;
                case HarvestState.Moving:          UpdateMoving();            break;
                case HarvestState.Attacking:        UpdateAttacking();         break;
                case HarvestState.CollectingDrops:  UpdateCollectingDrops();   break;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  State Management
        // ══════════════════════════════════════════════════════════════════

        private void EnterState(HarvestState newState)
        {
            ExitState(_state);
            _state = newState;

            switch (newState)
            {
                case HarvestState.Idle:
                    _scanTimer = 0f;
                    break;
                case HarvestState.Moving:
                    _harvestStuckTimer = 0f;
                    _harvestStuckTier  = 0;
                    break;
                case HarvestState.Attacking:
                    _attackCooldown = 0f;
                    _losFailCount  = 0;
                    break;
                case HarvestState.CollectingDrops:
                    _dropCollectTimer       = 0f;
                    _dropCollectPickupTimer = 0f;
                    _dropCollectEmptyChecks = 0;
                    break;
            }
        }

        private void ExitState(HarvestState oldState)
        {
            switch (oldState)
            {
                case HarvestState.Moving:
                case HarvestState.CollectingDrops:
                    _ai?.StopMoving();
                    break;
            }
        }

        private void ResetHarvestState(bool clearTarget)
        {
            ExitState(_state);

            if (clearTarget)
            {
                _currentTarget       = null;
                _currentResourceType = ResourceType.None;
                _pausedTarget        = null;
                _pausedResourceType  = ResourceType.None;
            }

            _attackCooldown         = 0f;
            _harvestStuckTimer      = 0f;
            _harvestStuckTier       = 0;
            _losFailCount           = 0;
            _targetLockTimer        = 0f;
            _dropCollectTimer       = 0f;
            _dropCollectPickupTimer = 0f;
            _dropCollectEmptyChecks = 0;
            _lastTargetPos          = transform.position;
            _dropCollectCenter      = transform.position;
            _state                  = HarvestState.Idle;
            _scanTimer              = ScanInterval;

            if (_setup != null) _setup.SuppressAutoEquip = false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  External API
        // ══════════════════════════════════════════════════════════════════

        private void StopHarvesting()
        {
            ResetHarvestState(true);
            _invFullWarned = false;

            if (_ai != null)
            {
                int mode = _nview?.GetZDO()?.GetInt(
                    CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                    ?? CompanionSetup.ModeFollow;
                if (mode == CompanionSetup.ModeStay)
                {
                    _ai.SetFollowTarget(null);
                    _ai.SetPatrolPoint();
                }
                else if (Player.m_localPlayer != null)
                {
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                }
            }
        }

        private void PauseForEnemy()
        {
            if (_currentTarget != null && IsTargetValid(_currentTarget))
            {
                _pausedTarget       = _currentTarget;
                _pausedResourceType = _currentResourceType;
            }

            if (_setup != null) _setup.SuppressAutoEquip = false;
            ExitState(_state);
            _state     = HarvestState.Idle;
            _scanTimer = 0.5f;
        }

        internal void AbortForLeash(GameObject playerTarget)
        {
            ResetHarvestState(true);

            if (_ai == null) return;
            _ai.StopMoving();
            if (playerTarget != null)
                _ai.SetFollowTarget(playerTarget);
        }

        internal void NotifyActionModeChanged()
        {
            ResetHarvestState(true);
            _invFullWarned    = false;
            _toolWarned       = false;
            _noToolSpoken     = false;
            _noResourceSpoken = false;

            int mode = _nview?.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
            ApplyToolPreferenceForMode(mode);
            ClearBlacklistByReason(BlacklistReason.ToolTier);

            if (_ai == null) return;
            if (mode == CompanionSetup.ModeStay)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPoint();
            }
            else if (Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  State: Idle
        // ══════════════════════════════════════════════════════════════════

        private void UpdateIdle(int mode)
        {
            _scanTimer += Time.deltaTime;
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            CleanBlacklist();

            // Resume paused target if still valid
            if (_pausedTarget != null)
            {
                if (IsTargetValid(_pausedTarget))
                {
                    var pausedTool = FindBestTool(_pausedResourceType);
                    if (pausedTool != null)
                    {
                        _currentTarget       = _pausedTarget;
                        _currentResourceType = _pausedResourceType;
                        _pausedTarget = null;
                        EquipToolForHarvest(pausedTool);
                        NavigateToTarget(_currentTarget);
                        EnterState(HarvestState.Moving);
                        return;
                    }
                }
                _pausedTarget = null;
            }

            if (StartDropCollection(_dropCollectCenter))
                return;

            var targetType = ModeToResourceType(mode);
            if (targetType == ResourceType.None) return;

            var inv = _humanoid?.GetInventory();
            if (inv != null && !HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                return;
            }
            _invFullWarned = false;

            var tool = FindBestTool(targetType);
            if (tool == null)
            {
                if (!_noToolSpoken)
                {
                    _noToolSpoken = true;
                    string toolName = targetType == ResourceType.Wood ? "an axe" : "a pickaxe";
                    _talk?.Say($"I need {toolName}!");
                }
                return;
            }

            if (tool.m_shared.m_useDurability)
            {
                if (tool.m_durability <= 0f) { _talk?.Say("My tool broke!"); return; }
                float maxDura = tool.GetMaxDurability();
                if (maxDura > 0f && tool.m_durability / maxDura < LowDurabilityPct && !_toolWarned)
                {
                    _toolWarned = true;
                    _talk?.Say("My tool is about to break.");
                }
            }

            // Layered scan: close range first, then far
            Vector3 scanCenter = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position : transform.position;

            var (target, type) = FindBestResource(targetType, scanCenter, ScanRangeClose);
            if (target == null)
                (target, type) = FindBestResource(targetType, scanCenter, ScanRangeFar);

            if (target == null)
            {
                if (!_noResourceSpoken)
                {
                    _noResourceSpoken = true;
                    string resName = targetType == ResourceType.Wood ? "trees"
                        : targetType == ResourceType.Stone ? "rocks" : "ore deposits";
                    _talk?.Say($"I can't find any {resName} nearby.");
                }
                return;
            }

            _noToolSpoken     = false;
            _noResourceSpoken = false;

            _currentTarget       = target;
            _currentResourceType = type;
            _targetLockTimer     = TargetLockDuration;

            EquipToolForHarvest(tool);
            NavigateToTarget(target);
            EnterState(HarvestState.Moving);
        }

        // ══════════════════════════════════════════════════════════════════
        //  State: Moving
        // ══════════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
            if (!IsTargetValid())
            {
                HandleTargetLost();
                return;
            }

            if (_ai != null && _ai.GetFollowTarget() != _waypoint)
                _ai.SetFollowTarget(_waypoint);

            if (!TryGetInteractionPoint(_currentTarget,
                    out Vector3 targetCenter, out Vector3 standPoint,
                    out float minDist, out float maxDist))
            {
                HandleTargetLost();
                return;
            }

            _lastTargetPos = targetCenter;

            // Keep waypoint synced unless stuck escalation repositioned it
            if (_harvestStuckTier < 2)
                _waypoint.transform.position = standPoint;

            float flatDist = HorizontalDistance(transform.position, targetCenter);

            if (flatDist >= minDist && flatDist <= maxDist &&
                !IsTooFarBelowForSafeSwing(targetCenter))
            {
                _ai?.SetFollowTarget(null);
                _ai?.StopMoving();
                EnterState(HarvestState.Attacking);
                return;
            }

            // Stuck escalation via PositionTracker
            if (UpdateHarvestStuck(Time.deltaTime, targetCenter, standPoint))
            {
                if (!TryChainToNextTarget())
                    StopHarvesting();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  State: Attacking
        // ══════════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            if (!IsTargetValid())
            {
                HandleTargetLost();
                return;
            }

            _ai?.StopMoving();

            if (!TryGetInteractionPoint(_currentTarget,
                    out Vector3 targetCenter, out Vector3 standPoint,
                    out float minDist, out float maxDist))
            {
                HandleTargetLost();
                return;
            }

            _lastTargetPos = targetCenter;
            float flatDist = HorizontalDistance(transform.position, targetCenter);

            if (flatDist < minDist || flatDist > maxDist ||
                IsTooFarBelowForSafeSwing(targetCenter))
            {
                _waypoint.transform.position = standPoint;
                _ai.SetFollowTarget(_waypoint);
                EnterState(HarvestState.Moving);
                return;
            }

            _attackCooldown -= Time.deltaTime;
            if (_attackCooldown > 0f) return;

            // Pre-attack validation: LOS + height
            if (!ValidateAttackPosition(targetCenter))
            {
                _losFailCount++;
                if (_losFailCount >= MaxLOSFails)
                {
                    BlacklistTarget(_currentTarget, BlacklistReason.Unreachable);
                    if (!TryChainToNextTarget()) StopHarvesting();
                    return;
                }
                _waypoint.transform.position = standPoint;
                _ai.SetFollowTarget(_waypoint);
                EnterState(HarvestState.Moving);
                return;
            }
            _losFailCount = 0;

            var tool = GetEquippedTool();
            var inv  = _humanoid?.GetInventory();
            if (tool == null || inv == null || !inv.ContainsItem(tool))
            {
                StopHarvesting();
                return;
            }
            if (tool.m_shared.m_useDurability && tool.m_durability <= 0f)
            {
                _talk?.Say("My tool broke!");
                StopHarvesting();
                return;
            }

            if (!HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                StopHarvesting();
                return;
            }

            // Face the target
            Vector3 dir = targetCenter - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);
            else
            {
                _waypoint.transform.position = standPoint;
                _ai.SetFollowTarget(_waypoint);
                EnterState(HarvestState.Moving);
                _attackCooldown = 0.15f;
                return;
            }

            if (_humanoid.StartAttack(null, false))
                _attackCooldown = AttackCooldown;
            else
                _attackCooldown = 0.2f;
        }

        // ══════════════════════════════════════════════════════════════════
        //  State: Collecting Drops
        // ══════════════════════════════════════════════════════════════════

        private void UpdateCollectingDrops()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) { StopHarvesting(); return; }

            if (!HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                StopHarvesting();
                return;
            }

            _dropCollectTimer       += Time.deltaTime;
            _dropCollectPickupTimer -= Time.deltaTime;

            if (_dropCollectTimer > DropCollectMaxDuration)
            {
                _dropCollectCenter = transform.position;
                if (!TryChainToNextTarget()) StopHarvesting();
                return;
            }

            bool blockedByCapacity = false;
            int  pendingCount      = 0;
            float nearestDist      = float.MaxValue;
            var nearest = FindNearestDropCandidate(
                _dropCollectCenter, inv,
                ref blockedByCapacity, ref pendingCount, ref nearestDist);

            if (pendingCount <= 0)
            {
                _dropCollectEmptyChecks++;
                if (_dropCollectEmptyChecks >= DropCollectEmptyCheckGoal)
                {
                    _dropCollectCenter = transform.position;
                    if (!TryChainToNextTarget()) StopHarvesting();
                }
                return;
            }
            _dropCollectEmptyChecks = 0;

            if (blockedByCapacity && nearest == null)
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                StopHarvesting();
                return;
            }

            if (nearest == null) return;

            // LOS check: skip drops blocked by terrain
            Vector3 from = transform.position + Vector3.up * 0.5f;
            if (Physics.Linecast(from, nearest.transform.position, out RaycastHit hit))
            {
                if (!hit.collider.transform.IsChildOf(nearest.transform) &&
                    hit.collider.transform != nearest.transform)
                    return; // blocked — wait for timeout
            }

            _waypoint.transform.position = nearest.transform.position;
            if (_ai != null && _ai.GetFollowTarget() != _waypoint)
                _ai.SetFollowTarget(_waypoint);

            float dist = Vector3.Distance(from, nearest.transform.position);
            if (dist > DropCollectMoveRange) return;

            _ai?.StopMoving();
            if (_dropCollectPickupTimer > 0f) return;
            _dropCollectPickupTimer = DropCollectPickupInterval;

            if (!nearest.CanPickup()) { nearest.RequestOwn(); return; }
            _humanoid.Pickup(nearest.gameObject);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Target Management
        // ══════════════════════════════════════════════════════════════════

        private void HandleTargetLost()
        {
            Vector3 lastPos = _currentTarget != null
                ? _currentTarget.transform.position : _lastTargetPos;
            _lastTargetPos = lastPos;

            if (StartDropCollection(lastPos)) return;
            if (TryChainToNextTarget()) return;
            StopHarvesting();
        }

        private void NavigateToTarget(GameObject target)
        {
            if (!TryGetInteractionPoint(target,
                    out _, out Vector3 standPoint, out _, out _))
                standPoint = target.transform.position;
            _lastTargetPos               = target.transform.position;
            _waypoint.transform.position = standPoint;
            _ai.SetFollowTarget(_waypoint);
        }

        private bool TryChainToNextTarget()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;

            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            var targetType = ModeToResourceType(mode);
            if (targetType == ResourceType.None) return false;

            Vector3 scanCenter = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position : transform.position;

            var (target, type) = FindBestResource(targetType, scanCenter, ScanRangeClose);
            if (target == null)
                (target, type) = FindBestResource(targetType, scanCenter, ScanRangeFar);
            if (target == null) return false;

            var tool = FindBestTool(type);
            if (tool == null) return false;

            _currentTarget       = target;
            _currentResourceType = type;
            _targetLockTimer     = TargetLockDuration;

            EquipToolForHarvest(tool);
            NavigateToTarget(target);
            EnterState(HarvestState.Moving);
            return true;
        }

        private bool StartDropCollection(Vector3 center)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;

            if (!HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                return false;
            }

            bool  blockedByCapacity = false;
            int   pendingCount      = 0;
            float nearestDist       = float.MaxValue;
            var nearest = FindNearestDropCandidate(
                center, inv, ref blockedByCapacity, ref pendingCount, ref nearestDist);
            if (pendingCount <= 0) return false;

            _dropCollectCenter   = center;
            _currentTarget       = null;
            _currentResourceType = ResourceType.None;

            EnterState(HarvestState.CollectingDrops);

            Vector3 moveTo = nearest != null ? nearest.transform.position : center;
            _waypoint.transform.position = moveTo;
            _ai?.SetFollowTarget(_waypoint);
            return true;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Mode mapping
        // ══════════════════════════════════════════════════════════════════

        private static ResourceType ModeToResourceType(int mode)
        {
            switch (mode)
            {
                case CompanionSetup.ModeGatherWood:  return ResourceType.Wood;
                case CompanionSetup.ModeGatherStone: return ResourceType.Stone;
                case CompanionSetup.ModeGatherOre:   return ResourceType.Ore;
                default: return ResourceType.None;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Validation
        // ══════════════════════════════════════════════════════════════════

        private bool IsTargetValid() => IsTargetValid(_currentTarget);

        private bool IsTargetValid(GameObject target)
        {
            if (target == null || !target || !target.activeInHierarchy) return false;

            var nv = target.GetComponent<ZNetView>();
            if (nv != null && nv.GetZDO() == null) return false;

            var destr = target.GetComponent<Destructible>();
            if (destr != null)
            {
                var dNv = destr.GetComponent<ZNetView>();
                if (dNv != null && dNv.GetZDO() != null)
                {
                    float hp = dNv.GetZDO().GetFloat(ZDOVars.s_health, destr.m_health);
                    if (hp <= 0f) return false;
                }
            }
            return true;
        }

        private bool HasEnemyNearby()
        {
            return _companionAI != null &&
                   _companionAI.NearestEnemy != null &&
                   _companionAI.NearestEnemyDist < 15f;
        }

        private bool ValidateAttackPosition(Vector3 targetCenter)
        {
            float yDiff = transform.position.y - targetCenter.y;
            if (yDiff < -MaxDownwardAttackOffset || yDiff > MaxUpwardAttackOffset)
                return false;

            Vector3 eye = transform.position + Vector3.up * 1.5f;
            if (Physics.Linecast(eye, targetCenter, out RaycastHit hit))
            {
                if (_currentTarget != null &&
                    !hit.collider.transform.IsChildOf(_currentTarget.transform) &&
                    hit.collider.transform != _currentTarget.transform)
                    return false;
            }
            return true;
        }

        private static bool HasInventoryCapacity(Inventory inv)
        {
            if (inv == null) return false;
            if (inv.HaveEmptySlot()) return true;
            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_stack < item.m_shared.m_maxStackSize) return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Stuck Escalation (PositionTracker-based)
        // ══════════════════════════════════════════════════════════════════

        /// <returns>true if target was blacklisted (caller should abandon)</returns>
        private bool UpdateHarvestStuck(float dt, Vector3 targetCenter, Vector3 standPoint)
        {
            if (_companionAI == null) return false;

            if (_companionAI.Tracker.IsOscillating(0.5f, 4f))
            {
                BlacklistTarget(_currentTarget, BlacklistReason.Oscillation);
                return true;
            }

            float moved = _companionAI.Tracker.DistanceOverWindow(2f);
            if (moved > 0.5f)
            {
                _harvestStuckTimer = 0f;
                _harvestStuckTier  = 0;
                return false;
            }

            _harvestStuckTimer += dt;

            // Tier 4: 7.5s → blacklist and abandon
            if (_harvestStuckTimer >= 7.5f && _harvestStuckTier < 5)
            {
                BlacklistTarget(_currentTarget, BlacklistReason.Unreachable);
                return true;
            }

            // Tier 3: 6s → jump
            if (_harvestStuckTimer >= 6f && _harvestStuckTier < 4)
            {
                _harvestStuckTier = 4;
                if (_character != null) _character.Jump(false);
                return false;
            }

            // Tier 2: 5s → opposite perpendicular offset
            if (_harvestStuckTimer >= 5f && _harvestStuckTier < 3)
            {
                _harvestStuckTier = 3;
                ApplyPerpendicularOffset(targetCenter, standPoint, !_offsetLeft);
                return false;
            }

            // Tier 1: 3.5s → perpendicular offset
            if (_harvestStuckTimer >= 3.5f && _harvestStuckTier < 2)
            {
                _harvestStuckTier = 2;
                _offsetLeft = Random.value > 0.5f;
                ApplyPerpendicularOffset(targetCenter, standPoint, _offsetLeft);
                return false;
            }

            // Tier 0: 1.5s → try door
            if (_harvestStuckTimer >= 1.5f && _harvestStuckTier < 1)
            {
                _harvestStuckTier = 1;
                _companionAI.TryOpenNearbyDoor();
                return false;
            }

            return false;
        }

        private void ApplyPerpendicularOffset(
            Vector3 targetCenter, Vector3 standPoint, bool leftSide)
        {
            Vector3 toTarget = targetCenter - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude < 0.001f) toTarget = transform.forward;
            toTarget.Normalize();
            Vector3 perp = Vector3.Cross(toTarget, Vector3.up).normalized;
            if (perp.sqrMagnitude < 0.01f) perp = transform.right;
            float side = leftSide ? 1f : -1f;
            _waypoint.transform.position = standPoint + perp * side * 3f;
            _ai?.SetFollowTarget(_waypoint);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Target Selection — Scored with layered scan
        // ══════════════════════════════════════════════════════════════════

        private (GameObject, ResourceType) FindBestResource(
            ResourceType filter, Vector3 center, float scanRange)
        {
            var colliders = Physics.OverlapSphere(center, scanRange);

            GameObject   best     = null;
            ResourceType bestType = ResourceType.None;
            float        bestScore = float.MinValue;

            GameObject   fallback     = null;
            ResourceType fallbackType = ResourceType.None;
            float        fallbackScore = float.MinValue;

            var seen = new HashSet<int>();

            foreach (var col in colliders)
            {
                if (col == null) continue;

                var (resGo, resType) = ClassifyResource(col.gameObject);
                if (resGo == null || resType == ResourceType.None) continue;
                if (resType != filter) continue;
                if (filter == ResourceType.Wood && !IsWoodGatherTarget(resGo)) continue;

                int id = resGo.GetInstanceID();
                if (!seen.Add(id)) continue;
                if (IsBlacklisted(id)) continue;

                if (Player.m_localPlayer != null)
                {
                    float distToPlayer = Vector3.Distance(
                        Player.m_localPlayer.transform.position, resGo.transform.position);
                    if (distToPlayer > PlayerResourceRange) continue;
                }

                var tool = FindBestTool(resType);
                if (tool == null) continue;
                if (tool.m_shared.m_toolTier < GetMinToolTier(resGo)) continue;

                float dist = Vector3.Distance(transform.position, resGo.transform.position);
                float yDiff = Mathf.Abs(transform.position.y - resGo.transform.position.y);
                float playerDist = Player.m_localPlayer != null
                    ? Vector3.Distance(
                        Player.m_localPlayer.transform.position, resGo.transform.position)
                    : dist;

                // Score: distance + height penalty + player proximity
                float score = 1f - Mathf.Clamp01(dist / scanRange);
                score -= yDiff * 0.08f;
                if (playerDist < 20f)
                    score += 0.15f * (1f - playerDist / 20f);

                // Track fallback (best by base score, no path requirement)
                if (score > fallbackScore)
                {
                    fallback      = resGo;
                    fallbackType  = resType;
                    fallbackScore = score;
                }

                // Primary: require pathfinding
                if (Pathfinding.instance != null &&
                    !Pathfinding.instance.HavePath(
                        transform.position, resGo.transform.position,
                        Pathfinding.AgentType.Humanoid))
                    continue;

                float pathScore = score + 0.3f;
                if (pathScore > bestScore)
                {
                    best      = resGo;
                    bestType  = resType;
                    bestScore = pathScore;
                }
            }

            if (best == null && fallback != null)
                return (fallback, fallbackType);
            return (best, bestType);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Positioning — Terrain-Aware Stand Points (8 directions)
        // ══════════════════════════════════════════════════════════════════

        private bool TryGetInteractionPoint(
            GameObject target,
            out Vector3 targetCenter, out Vector3 standPoint,
            out float minDist, out float maxDist)
        {
            targetCenter = transform.position;
            standPoint   = transform.position;
            minDist      = 1.2f;
            maxDist      = AttackRange;

            if (target == null) return false;

            if (!TryGetTargetBounds(target, out Bounds bounds))
                bounds = new Bounds(target.transform.position, new Vector3(1f, 2f, 1f));

            targetCenter = bounds.center;

            float radius = Mathf.Clamp(
                Mathf.Max(bounds.extents.x, bounds.extents.z),
                MinTargetRadius, MaxTargetRadius);
            minDist = Mathf.Clamp(radius + MinSurfaceDistance, 1.0f, AttackRange - 0.15f);
            maxDist = Mathf.Clamp(radius + MaxSurfaceDistance, minDist + 0.2f, AttackRange);
            float preferredDist = Mathf.Clamp(
                radius + PreferredSurfaceDistance, minDist, maxDist);

            // Current approach direction
            Vector3 currentApproach = transform.position - targetCenter;
            currentApproach.y = 0f;
            if (currentApproach.sqrMagnitude < 0.001f)
            {
                Vector3 fallbackDir = targetCenter - (Player.m_localPlayer != null
                    ? Player.m_localPlayer.transform.position
                    : transform.position);
                fallbackDir.y = 0f;
                currentApproach = fallbackDir.sqrMagnitude > 0.001f
                    ? fallbackDir : transform.forward;
            }
            currentApproach.Normalize();

            // Sample 8 directions around the target
            Vector3 bestPoint = targetCenter + currentApproach * preferredDist;
            bestPoint.y = transform.position.y;
            float bestScore = float.MinValue;
            bool anyValid = false;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;
                Vector3 candidatePos = targetCenter + dir * preferredDist;
                float score = 0f;

                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetSolidHeight(candidatePos, out groundHeight))
                    {
                        float heightDiff = Mathf.Abs(groundHeight - transform.position.y);
                        score -= heightDiff * 0.5f;
                        candidatePos.y = groundHeight;
                        anyValid = true;
                    }
                    else
                    {
                        score -= 5f;
                    }
                }
                else
                {
                    candidatePos.y = transform.position.y;
                    anyValid = true;
                }

                score += Vector3.Dot(dir, currentApproach) * 0.3f;

                Vector3 eyePos = candidatePos + Vector3.up * 1.5f;
                if (!Physics.Linecast(eyePos, targetCenter))
                    score += 0.4f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = candidatePos;
                }
            }

            if (!anyValid)
            {
                bestPoint = targetCenter + currentApproach * preferredDist;
                bestPoint.y = transform.position.y;
            }

            standPoint = bestPoint;
            return true;
        }

        private static bool TryGetTargetBounds(GameObject target, out Bounds bounds)
        {
            bounds = default;
            if (target == null) return false;

            bool found = false;
            var colliders = target.GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
            {
                if (col == null || !col.enabled) continue;
                if (!found) { bounds = col.bounds; found = true; }
                else bounds.Encapsulate(col.bounds);
            }

            if (found) return true;

            var renderers = target.GetComponentsInChildren<Renderer>(true);
            foreach (var renderer in renderers)
            {
                if (renderer == null || !renderer.enabled) continue;
                if (!found) { bounds = renderer.bounds; found = true; }
                else bounds.Encapsulate(renderer.bounds);
            }
            return found;
        }

        private bool IsTooFarBelowForSafeSwing(Vector3 targetCenter)
        {
            return (transform.position.y - targetCenter.y) > MaxDownwardAttackOffset;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Drop Candidates
        // ══════════════════════════════════════════════════════════════════

        private ItemDrop FindNearestDropCandidate(
            Vector3 center, Inventory inv,
            ref bool blockedByCapacity, ref int pendingCount,
            ref float nearestDist)
        {
            ItemDrop nearest = null;
            Vector3 scanCenter = center + Vector3.up;
            var colliders = Physics.OverlapSphere(
                scanCenter, DropCollectSearchRadius, _itemMask);

            for (int i = 0; i < colliders.Length; i++)
            {
                var col = colliders[i];
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null) continue;

                var itemNview = itemDrop.GetComponent<ZNetView>();
                if (itemNview == null || !itemNview.IsValid()) continue;
                if (itemDrop.m_itemData == null) continue;

                // Range cap: ignore drops far from harvest site
                float distFromCenter = Vector3.Distance(center, itemDrop.transform.position);
                if (distFromCenter > DropCollectRangeCap) continue;

                pendingCount++;

                if (!itemDrop.CanPickup())
                {
                    itemDrop.RequestOwn();
                    continue;
                }

                bool canAdd = inv.CanAddItem(itemDrop.m_itemData);
                bool canCarry = itemDrop.m_itemData.GetWeight() +
                    inv.GetTotalWeight() <= CompanionTierData.MaxCarryWeight;
                if (!canAdd || !canCarry)
                {
                    blockedByCapacity = true;
                    continue;
                }

                float dist = Vector3.Distance(
                    transform.position + Vector3.up, itemDrop.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest     = itemDrop;
                }
            }
            return nearest;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Resource Classification
        // ══════════════════════════════════════════════════════════════════

        private static bool IsWoodGatherTarget(GameObject go)
        {
            if (go == null) return false;
            return go.GetComponent<TreeBase>() != null ||
                   go.GetComponent<TreeLog>() != null;
        }

        internal static (GameObject, ResourceType) ClassifyResource(GameObject go)
        {
            var tree = go.GetComponentInParent<TreeBase>();
            if (tree != null)
            {
                var nv = tree.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (tree.gameObject, ResourceType.Wood);
            }

            var log = go.GetComponentInParent<TreeLog>();
            if (log != null)
            {
                var nv = log.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (log.gameObject, ResourceType.Wood);
            }

            var rock = go.GetComponentInParent<MineRock>();
            if (rock != null)
            {
                var nv = rock.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (rock.gameObject, ResourceType.Ore);
            }

            var rock5 = go.GetComponentInParent<MineRock5>();
            if (rock5 != null)
            {
                var nv = rock5.GetComponent<ZNetView>();
                if (nv != null && nv.GetZDO() != null)
                    return (rock5.gameObject, ResourceType.Ore);
            }

            var destr = go.GetComponentInParent<Destructible>();
            if (destr != null)
            {
                var nv = destr.GetComponent<ZNetView>();
                if (nv == null || nv.GetZDO() == null)
                    return (null, ResourceType.None);

                if (destr.m_damages.m_chop != HitData.DamageModifier.Immune &&
                    destr.m_damages.m_chop != HitData.DamageModifier.Ignore)
                    return (destr.gameObject, ResourceType.Wood);

                if (destr.m_damages.m_pickaxe != HitData.DamageModifier.Immune &&
                    destr.m_damages.m_pickaxe != HitData.DamageModifier.Ignore)
                    return (destr.gameObject, ResourceType.Stone);
            }

            return (null, ResourceType.None);
        }

        private static int GetMinToolTier(GameObject go)
        {
            var tree = go.GetComponent<TreeBase>();
            if (tree != null) return tree.m_minToolTier;
            var log = go.GetComponent<TreeLog>();
            if (log != null) return log.m_minToolTier;
            var rock = go.GetComponent<MineRock>();
            if (rock != null) return rock.m_minToolTier;
            var rock5 = go.GetComponent<MineRock5>();
            if (rock5 != null) return rock5.m_minToolTier;
            var destr = go.GetComponent<Destructible>();
            if (destr != null) return destr.m_minToolTier;
            return 999;
        }

        // ══════════════════════════════════════════════════════════════════
        //  Tool Management
        // ══════════════════════════════════════════════════════════════════

        private ItemDrop.ItemData FindBestTool(ResourceType type)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            ItemDrop.ItemData best = null;
            float bestDmg = 0f;

            foreach (var item in inv.GetAllItems())
            {
                if (item.m_shared.m_useDurability && item.m_durability <= 0f)
                    continue;

                float relevant = type == ResourceType.Wood
                    ? item.m_shared.m_damages.m_chop
                    : item.m_shared.m_damages.m_pickaxe;

                if (relevant > bestDmg)
                {
                    best    = item;
                    bestDmg = relevant;
                }
            }
            return best;
        }

        private ItemDrop.ItemData GetEquippedTool()
        {
            if (_setup == null) return null;
            return _setup.GetEquipSlot(CompanionSetup._rightItemField);
        }

        private void EquipToolForHarvest(ItemDrop.ItemData tool)
        {
            if (_setup == null || _humanoid == null) return;
            _setup.SuppressAutoEquip = true;

            var curRight = _setup.GetEquipSlot(CompanionSetup._rightItemField);
            if (curRight != null && curRight != tool)
                _humanoid.UnequipItem(curRight, false);

            var toolType = tool.m_shared.m_itemType;
            bool is2H = toolType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                        toolType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            if (is2H)
            {
                var curLeft = _setup.GetEquipSlot(CompanionSetup._leftItemField);
                if (curLeft != null)
                    _humanoid.UnequipItem(curLeft, false);
            }

            if (curRight != tool) _toolWarned = false;
            if (!_humanoid.IsItemEquiped(tool))
                _humanoid.EquipItem(tool, true);
        }

        private void ApplyToolPreferenceForMode(int mode)
        {
            if (_setup == null) return;

            var targetType = ModeToResourceType(mode);
            if (targetType == ResourceType.None)
            {
                _setup.SuppressAutoEquip = false;
                _setup.SyncEquipmentToInventory();
                return;
            }

            var tool = FindBestTool(targetType);
            if (tool == null)
            {
                _setup.SuppressAutoEquip = false;
                return;
            }

            _currentResourceType = targetType;
            EquipToolForHarvest(tool);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Blacklist Management
        // ══════════════════════════════════════════════════════════════════

        private void BlacklistTarget(GameObject target, BlacklistReason reason)
        {
            if (target == null) return;
            int id = target.GetInstanceID();

            float baseDuration;
            switch (reason)
            {
                case BlacklistReason.Oscillation: baseDuration = BlacklistOscillation; break;
                case BlacklistReason.PathFailed:  baseDuration = BlacklistPathFailed;  break;
                case BlacklistReason.ToolTier:    baseDuration = float.MaxValue;       break;
                default:                          baseDuration = BlacklistUnreachable;  break;
            }

            int failCount = 1;
            if (_blacklist.TryGetValue(id, out var existing))
                failCount = existing.FailCount + 1;

            float duration = failCount >= 3 ? baseDuration * 2f : baseDuration;

            _blacklist[id] = new BlacklistEntry
            {
                ExpireTime = Time.time + duration,
                FailCount  = failCount,
                Reason     = reason
            };
        }

        private bool IsBlacklisted(int id)
        {
            if (!_blacklist.TryGetValue(id, out var entry)) return false;
            if (Time.time > entry.ExpireTime)
            {
                _blacklist.Remove(id);
                return false;
            }
            return true;
        }

        private void CleanBlacklist()
        {
            var expired = new List<int>();
            float now = Time.time;
            foreach (var kv in _blacklist)
            {
                if (now > kv.Value.ExpireTime)
                    expired.Add(kv.Key);
            }
            foreach (var k in expired)
                _blacklist.Remove(k);
        }

        private void ClearBlacklistByReason(BlacklistReason reason)
        {
            var toRemove = new List<int>();
            foreach (var kv in _blacklist)
            {
                if (kv.Value.Reason == reason)
                    toRemove.Add(kv.Key);
            }
            foreach (var k in toRemove)
                _blacklist.Remove(k);
        }
    }
}
