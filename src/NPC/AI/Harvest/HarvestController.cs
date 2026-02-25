using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Main harvest state machine. Replaces CompanionHarvest as the MonoBehaviour.
    /// States: Idle → Moving → Attacking → CollectingDrops.
    /// Bug #2 fix: Checks CombatBrain.IsRetreating to pause during retreat.
    /// </summary>
    public class HarvestController : MonoBehaviour
    {
        private enum HarvestState { Idle, Moving, Attacking, CollectingDrops }

        // ── References ────────────────────────────────────────────────
        private ZNetView     _nview;
        private Humanoid     _humanoid;
        private MonsterAI    _ai;
        private CompanionSetup _setup;
        private CompanionBrain _brain;
        private CompanionStamina _stamina;
        private CompanionTalk _talk;
        private Character    _character;

        // ── Subsystems ────────────────────────────────────────────────
        private HarvestBlacklist   _blacklist;
        private ResourceScanner    _scanner;
        private HarvestToolManager _tools;
        private HarvestNavigation  _nav;
        private DropCollector      _drops;

        // ── State ─────────────────────────────────────────────────────
        private HarvestState _state = HarvestState.Idle;
        private GameObject   _currentTarget;
        private ResourceType _currentResourceType;
        private int          _lastMode = int.MinValue;

        // Paused target (enemy interrupt)
        private GameObject   _pausedTarget;
        private ResourceType _pausedResourceType;

        // Timers
        private float _scanTimer;
        private float _attackCooldown;
        private float _targetLockTimer;
        private float _waypointUpdateTimer;

        // Pre-attack validation
        private int _losFailCount;

        // Cached navigation (Moving state)
        private Vector3 _cachedStandPoint;
        private Vector3 _cachedTargetCenter;
        private float   _cachedMinDist;
        private float   _cachedMaxDist;
        private bool    _navCacheDirty = true;

        // Cached navigation (Attacking state) — throttled to avoid per-frame raycasts
        private float   _atkNavTimer;
        private Vector3 _atkCenter;
        private Vector3 _atkStand;
        private float   _atkMinDist;
        private float   _atkMaxDist;
        private bool    _atkNavValid;

        // Positions
        private Vector3 _lastTargetPos;
        private Vector3 _dropCollectCenter;

        // Warnings
        private bool _invFullWarned;
        private bool _noToolSpoken;
        private bool _noResourceSpoken;
        private float _resourceDiagCooldown;

        private GameObject _waypoint;

        // ── Constants ─────────────────────────────────────────────────
        private const float ScanInterval       = 0.5f;
        private const float ScanRangeClose     = 20f;
        private const float ScanRangeFar       = 50f;
        private const float AttackRange        = 2.6f;
        private const float AttackCooldown     = 1.5f;
        private const float HarvestStaminaCost = 6f;
        private const float MaxPlayerDistance   = CompanionSetup.MaxLeashDistance;
        private const float TargetLockDuration  = 3f;
        private const int   MaxLOSFails         = 3;
        private const float WaypointUpdateRate  = 0.5f;  // seconds between waypoint recalcs

        // ── Properties ────────────────────────────────────────────────
        internal bool IsActivelyHarvesting =>
            _state == HarvestState.Moving || _state == HarvestState.Attacking;
        internal bool IsCollectingDrops => _state == HarvestState.CollectingDrops;

        internal static ResourceType ModeToResourceType(int mode)
        {
            switch (mode)
            {
                case CompanionSetup.ModeGatherWood:  return ResourceType.Wood;
                case CompanionSetup.ModeGatherStone: return ResourceType.Stone;
                case CompanionSetup.ModeGatherOre:   return ResourceType.Ore;
                default: return ResourceType.None;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _humanoid  = GetComponent<Humanoid>();
            _ai        = GetComponent<MonsterAI>();
            _setup     = GetComponent<CompanionSetup>();
            _brain     = GetComponent<CompanionBrain>();
            _stamina   = GetComponent<CompanionStamina>();
            _talk      = GetComponent<CompanionTalk>();
            _character = GetComponent<Character>();

            _lastTargetPos     = transform.position;
            _dropCollectCenter = transform.position;

            _waypoint = new GameObject("HC_HarvestWaypoint");
            Object.DontDestroyOnLoad(_waypoint);

            _blacklist = new HarvestBlacklist();
            _scanner   = new ResourceScanner(transform, _blacklist);
            _tools     = new HarvestToolManager(_humanoid, _setup);
            _nav       = new HarvestNavigation(_character, transform,
                             _brain?.Tracker, _brain?.Doors);
            _drops     = new DropCollector(_humanoid, transform);
        }

        private void Start()
        {
            _drops.Init();
        }

        private void OnDestroy()
        {
            if (_waypoint != null) Object.Destroy(_waypoint);
            if (_setup != null) _setup.SuppressAutoEquip = false;
        }

        private void Update()
        {
            if (_nview == null) return;
            var zdo = _nview.GetZDO();
            if (zdo == null) return;

            if (!_nview.IsOwner())
            {
                var player = Player.m_localPlayer;
                string ownerId = zdo.GetString(CompanionSetup.OwnerHash, "");
                string localId = player != null ? player.GetPlayerID().ToString() : "";
                if (player != null && ownerId == localId)
                    _nview.ClaimOwnership();
                if (!_nview.IsOwner()) return;
            }

            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode != _lastMode)
            {
                int oldMode = _lastMode;
                _lastMode = mode;
                _brain?.OnActionModeChanged(oldMode, mode);
                NotifyActionModeChanged();
            }

            if (mode < CompanionSetup.ModeGatherWood || mode > CompanionSetup.ModeGatherOre)
            {
                if (_state != HarvestState.Idle) StopHarvesting();
                return;
            }

            if (_character != null)
            {
                if (_character.IsDead()) return;
                if (_character.InAttack()) return;
            }

            // Bug #2 fix: pause during retreat
            if (_brain != null && _brain.CombatBrain != null && _brain.CombatBrain.IsRetreating)
            {
                if (_state != HarvestState.Idle) PauseForEnemy();
                return;
            }

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
                case HarvestState.Idle:            UpdateIdle(mode);         break;
                case HarvestState.Moving:          UpdateMoving();           break;
                case HarvestState.Attacking:       UpdateAttacking();        break;
                case HarvestState.CollectingDrops: UpdateCollectingDrops();  break;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  State Management
        // ══════════════════════════════════════════════════════════════

        private void EnterState(HarvestState newState)
        {
            ExitState(_state);
            _state = newState;

            switch (newState)
            {
                case HarvestState.Idle:     _scanTimer = 0f; break;
                case HarvestState.Moving:   _nav.ResetStuck(); _navCacheDirty = true; _waypointUpdateTimer = 0f; break;
                case HarvestState.Attacking:
                    _attackCooldown = 0f;
                    _losFailCount = 0;
                    _atkNavTimer = 0f;
                    _atkNavValid = false;
                    // Park waypoint at companion's feet so MonsterAI Follow holds position
                    // (setting null would let MonsterAI idle-wander since we removed per-frame StopMoving)
                    _waypoint.transform.position = transform.position;
                    _ai?.SetFollowTarget(_waypoint);
                    _ai?.StopMoving();
                    break;
                case HarvestState.CollectingDrops:
                    _drops.ResetState(_dropCollectCenter); break;
            }
        }

        private void ExitState(HarvestState oldState)
        {
            if (oldState == HarvestState.Moving || oldState == HarvestState.CollectingDrops)
                _ai?.StopMoving();
        }

        private void ResetHarvestState(bool clearTarget)
        {
            ExitState(_state);
            if (clearTarget)
            {
                _currentTarget = null;
                _currentResourceType = ResourceType.None;
                _pausedTarget = null;
                _pausedResourceType = ResourceType.None;
            }
            _attackCooldown = 0f;
            _losFailCount = 0;
            _targetLockTimer = 0f;
            _lastTargetPos = transform.position;
            _dropCollectCenter = transform.position;
            _state = HarvestState.Idle;
            _scanTimer = ScanInterval;
            if (_setup != null) _setup.SuppressAutoEquip = false;
        }

        // ══════════════════════════════════════════════════════════════
        //  External API
        // ══════════════════════════════════════════════════════════════

        private void StopHarvesting()
        {
            ResetHarvestState(true);
            _invFullWarned = false;
            _tools.RestoreCombatLoadout();

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
                    _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
            }
        }

        private void PauseForEnemy()
        {
            if (_currentTarget != null && IsTargetValid(_currentTarget))
            {
                _pausedTarget = _currentTarget;
                _pausedResourceType = _currentResourceType;
            }
            _tools.RestoreCombatLoadout();
            ExitState(_state);
            _state = HarvestState.Idle;
            _scanTimer = 0.5f;
        }

        internal void AbortForLeash(GameObject playerTarget)
        {
            ResetHarvestState(true);
            _tools.RestoreCombatLoadout();
            if (_ai == null) return;
            _ai.StopMoving();
            if (playerTarget != null) _ai.SetFollowTarget(playerTarget);
        }

        internal void NotifyActionModeChanged()
        {
            ResetHarvestState(true);
            _invFullWarned = false;
            _noToolSpoken = false;
            _noResourceSpoken = false;
            _tools.ResetWarnings();

            int mode = _nview?.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
            _tools.ApplyToolPreferenceForMode(mode);
            _blacklist.ClearByReason(BlacklistReason.ToolTier);

            if (_ai == null) return;
            if (mode == CompanionSetup.ModeStay)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPoint();
            }
            else if (Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        // ══════════════════════════════════════════════════════════════
        //  State: Idle
        // ══════════════════════════════════════════════════════════════

        private void UpdateIdle(int mode)
        {
            _scanTimer += Time.deltaTime;
            if (_scanTimer < ScanInterval) return;
            _scanTimer = 0f;

            _blacklist.Clean();

            // Resume paused target
            if (_pausedTarget != null)
            {
                if (IsTargetValid(_pausedTarget))
                {
                    var pausedTool = _tools.FindBestTool(_pausedResourceType);
                    if (pausedTool != null)
                    {
                        _currentTarget = _pausedTarget;
                        _currentResourceType = _pausedResourceType;
                        _pausedTarget = null;
                        _tools.EquipForHarvest(pausedTool);
                        NavigateToTarget(_currentTarget);
                        EnterState(HarvestState.Moving);
                        return;
                    }
                }
                _pausedTarget = null;
            }

            if (StartDropCollection(_dropCollectCenter)) return;

            var targetType = ModeToResourceType(mode);
            if (targetType == ResourceType.None) return;

            var inv = _humanoid?.GetInventory();
            if (inv != null && !DropCollector.HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                return;
            }
            _invFullWarned = false;

            var tool = _tools.FindBestTool(targetType);
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

            if (tool.m_shared.m_useDurability && tool.m_durability <= 0f)
            {
                _talk?.Say("My tool broke!");
                return;
            }
            if (_tools.CheckToolWarning(tool))
                _talk?.Say("My tool is about to break.");

            bool tierBlocked;
            var (target, type) = _scanner.FindBestForMode(
                targetType, ScanRangeClose, ScanRangeFar, tool,
                (rt, tier) => _tools.FindBestTool(rt, tier),
                out tierBlocked);

            // Recovery: clear stale blacklists and retry
            if (target == null && !tierBlocked && _blacklist.Count > 0)
            {
                _blacklist.ClearByReason(BlacklistReason.Unreachable);
                _blacklist.ClearByReason(BlacklistReason.Oscillation);
                _blacklist.ClearByReason(BlacklistReason.PathFailed);
                (target, type) = _scanner.FindBestForMode(
                    targetType, ScanRangeClose, ScanRangeFar, tool,
                    (rt, tier) => _tools.FindBestTool(rt, tier),
                    out tierBlocked);
            }

            if (target == null)
            {
                if (!_noResourceSpoken)
                {
                    _noResourceSpoken = true;
                    if (tierBlocked)
                    {
                        string tn = targetType == ResourceType.Wood ? "axe" : "pickaxe";
                        _talk?.Say($"I need a stronger {tn} for these resources.");
                    }
                    else
                    {
                        string rn = targetType == ResourceType.Wood ? "trees"
                            : targetType == ResourceType.Stone ? "rocks" : "ore deposits";
                        _talk?.Say($"I can't find any {rn} nearby.");
                    }
                }
                LogNoResource(targetType, tool);
                return;
            }

            _noToolSpoken = false;
            _noResourceSpoken = false;

            _currentTarget = target;
            _currentResourceType = type;
            _targetLockTimer = TargetLockDuration;

            _tools.EquipForHarvest(tool);
            NavigateToTarget(target);
            EnterState(HarvestState.Moving);
        }

        // ══════════════════════════════════════════════════════════════
        //  State: Moving
        // ══════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
            if (!IsTargetValid(_currentTarget))
            {
                HandleTargetLost();
                return;
            }

            if (_ai != null && _ai.GetFollowTarget() != _waypoint)
                _ai.SetFollowTarget(_waypoint);

            // Throttle waypoint recalculation — the #1 cause of oscillation.
            // Vanilla Valheim throttles target updates to 2-6s; we use 0.5s
            // for responsiveness while avoiding per-frame jitter.
            _waypointUpdateTimer += Time.deltaTime;
            if (_navCacheDirty || _waypointUpdateTimer >= WaypointUpdateRate)
            {
                _waypointUpdateTimer = 0f;
                _navCacheDirty = false;

                if (!_nav.TryGetInteractionPoint(_currentTarget,
                        out _cachedTargetCenter, out _cachedStandPoint,
                        out _cachedMinDist, out _cachedMaxDist))
                {
                    HandleTargetLost();
                    return;
                }
                _lastTargetPos = _cachedTargetCenter;
                _waypoint.transform.position = _cachedStandPoint;
            }

            float flatDist = HarvestNavigation.HorizontalDistance(
                transform.position, _cachedTargetCenter);

            if (flatDist >= _cachedMinDist && flatDist <= _cachedMaxDist &&
                !_nav.IsTooFarBelow(_cachedTargetCenter))
            {
                _ai?.SetFollowTarget(null);
                _ai?.StopMoving();
                EnterState(HarvestState.Attacking);
                return;
            }

            if (_nav.UpdateStuck(Time.deltaTime, _cachedTargetCenter, _cachedStandPoint,
                    _ai, _waypoint,
                    reason => {
                        _blacklist.AddTarget(_currentTarget, reason);
                        _navCacheDirty = true;
                    }))
            {
                if (!TryChainToNextTarget()) StopHarvesting();
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  State: Attacking
        // ══════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            if (!IsTargetValid(_currentTarget))
            {
                HandleTargetLost();
                return;
            }

            // Throttled nav recalculation (was running 16+ raycasts every FixedUpdate tick)
            _atkNavTimer -= Time.deltaTime;
            if (_atkNavTimer <= 0f || !_atkNavValid)
            {
                _atkNavTimer = 0.5f;
                if (!_nav.TryGetInteractionPoint(_currentTarget,
                        out _atkCenter, out _atkStand,
                        out _atkMinDist, out _atkMaxDist))
                {
                    HandleTargetLost();
                    return;
                }
                _atkNavValid = true;
                _lastTargetPos = _atkCenter;
            }

            float flatDist = HarvestNavigation.HorizontalDistance(
                transform.position, _atkCenter);

            // Hysteresis: wider tolerance prevents Moving↔Attacking oscillation (the shaking)
            if (flatDist < _atkMinDist - 0.4f ||
                flatDist > _atkMaxDist + 0.6f ||
                _nav.IsTooFarBelow(_atkCenter))
            {
                _waypoint.transform.position = _atkStand;
                _ai.SetFollowTarget(_waypoint);
                EnterState(HarvestState.Moving);
                return;
            }

            _attackCooldown -= Time.deltaTime;
            if (_attackCooldown > 0f) return;

            Transform targetRoot = _currentTarget != null ? _currentTarget.transform : null;
            if (!_nav.ValidateAttackPosition(_atkCenter, targetRoot))
            {
                _losFailCount++;
                if (_losFailCount >= MaxLOSFails)
                {
                    _blacklist.AddTarget(_currentTarget, BlacklistReason.Unreachable);
                    if (!TryChainToNextTarget()) StopHarvesting();
                    return;
                }
                _waypoint.transform.position = _atkStand;
                _ai.SetFollowTarget(_waypoint);
                EnterState(HarvestState.Moving);
                return;
            }
            _losFailCount = 0;

            var tool = _tools.GetEquippedTool();
            var inv = _humanoid?.GetInventory();
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

            if (_stamina != null && !_stamina.UseStamina(HarvestStaminaCost))
            {
                _attackCooldown = 0.35f;
                return;
            }

            if (!DropCollector.HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                StopHarvesting();
                return;
            }

            // Face target
            Vector3 dir = _atkCenter - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir.normalized);
            else
            {
                _waypoint.transform.position = _atkStand;
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

        // ══════════════════════════════════════════════════════════════
        //  State: Collecting Drops
        // ══════════════════════════════════════════════════════════════

        private void UpdateCollectingDrops()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) { StopHarvesting(); return; }

            if (!DropCollector.HasInventoryCapacity(inv))
            {
                if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                StopHarvesting();
                return;
            }

            bool inventoryFull;
            ItemDrop nearest;
            if (!_drops.Update(Time.deltaTime, inv, _ai, _waypoint,
                               out inventoryFull, out nearest))
            {
                if (inventoryFull)
                {
                    if (!_invFullWarned) { _invFullWarned = true; _talk?.Say("I'm full!"); }
                    StopHarvesting();
                }
                else
                {
                    _dropCollectCenter = transform.position;
                    if (!TryChainToNextTarget()) StopHarvesting();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  Target Management
        // ══════════════════════════════════════════════════════════════

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
            if (!_nav.TryGetInteractionPoint(target, out _, out Vector3 standPoint, out _, out _))
                standPoint = target.transform.position;
            _lastTargetPos = target.transform.position;
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

            var tool = _tools.FindBestTool(targetType);
            if (tool == null) return false;

            var (target, type) = _scanner.FindBestForMode(
                targetType, ScanRangeClose, ScanRangeFar, tool,
                (rt, tier) => _tools.FindBestTool(rt, tier),
                out _);
            if (target == null) return false;

            _currentTarget = target;
            _currentResourceType = type;
            _targetLockTimer = TargetLockDuration;

            _tools.EquipForHarvest(tool);
            NavigateToTarget(target);
            EnterState(HarvestState.Moving);
            return true;
        }

        private bool StartDropCollection(Vector3 center)
        {
            if (!_drops.HasDropsNear(center)) return false;

            _dropCollectCenter = center;
            _currentTarget = null;
            _currentResourceType = ResourceType.None;

            EnterState(HarvestState.CollectingDrops);

            bool blocked = false;
            int pending = 0;
            float nearestDist = float.MaxValue;
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;

            var nearest = _drops.FindNearest(center, inv, ref blocked, ref pending, ref nearestDist);
            Vector3 moveTo = nearest != null ? nearest.transform.position : center;
            _waypoint.transform.position = moveTo;
            _ai?.SetFollowTarget(_waypoint);
            return true;
        }

        // ══════════════════════════════════════════════════════════════
        //  Validation
        // ══════════════════════════════════════════════════════════════

        private static bool IsTargetValid(GameObject target)
        {
            if (target == null || !target || !target.activeInHierarchy) return false;

            var nv = target.GetComponent<ZNetView>();
            if (nv != null && !nv.IsValid() && !IsHarvestResource(target))
                return false;

            var destr = target.GetComponent<Destructible>();
            if (destr != null)
            {
                var dNv = destr.GetComponent<ZNetView>();
                if (dNv != null && dNv.IsValid() && dNv.GetZDO() != null)
                {
                    float hp = dNv.GetZDO().GetFloat(ZDOVars.s_health, destr.m_health);
                    if (hp <= 0f) return false;
                }
            }
            return true;
        }

        private static bool IsHarvestResource(GameObject target)
        {
            if (target == null) return false;
            return target.GetComponent<TreeBase>() != null ||
                   target.GetComponent<TreeLog>() != null ||
                   target.GetComponent<MineRock>() != null ||
                   target.GetComponent<MineRock5>() != null ||
                   target.GetComponent<Destructible>() != null;
        }

        private bool HasEnemyNearby()
        {
            return _brain != null &&
                   _brain.Enemies != null &&
                   _brain.Enemies.NearestEnemy != null &&
                   _brain.Enemies.NearestEnemyDist < 15f;
        }

        private void LogNoResource(ResourceType targetType, ItemDrop.ItemData tool)
        {
            if (Time.time < _resourceDiagCooldown) return;
            _resourceDiagCooldown = Time.time + 10f;

            string toolName = tool?.m_shared?.m_name ?? "<none>";
            int toolTier = tool?.m_shared != null ? tool.m_shared.m_toolTier : -1;
            float relevantDmg = ResourceClassifier.GetRelevantToolDamage(tool, targetType);
            float playerDist = Player.m_localPlayer != null
                ? Vector3.Distance(transform.position, Player.m_localPlayer.transform.position)
                : -1f;

            CompanionsPlugin.Log.LogInfo(
                $"[HarvestController] No target. mode={targetType} tool={toolName} " +
                $"tier={toolTier} dmg={relevantDmg:F1} pos={transform.position} " +
                $"playerDist={playerDist:F1} blacklist={_blacklist.Count}");
        }
    }
}
