using System;
using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Simple harvest AI for companions.
    /// Scans for trees/rocks, equips the right tool, walks over via
    /// BaseAI.MoveTo, then swings using Humanoid.StartAttack.
    ///
    /// Uses VikingNPC-style pattern:
    ///   MoveTo(dt, target, attackRange) → StopMoving → LookAt → StartAttack
    /// </summary>
    public class HarvestController : MonoBehaviour
    {
        private enum HarvestState { Idle, Moving, Attacking, CollectingDrops }

        // ── Components ──────────────────────────────────────────────────────
        private ZNetView         _nview;
        private CompanionAI      _ai;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionSetup   _setup;
        private DoorHandler      _doorHandler;
        private CompanionStamina _stamina;
        private CompanionTalk    _talk;

        // ── State ───────────────────────────────────────────────────────────
        private HarvestState _state = State_Idle;
        private GameObject   _target;
        private float        _scanTimer;
        private float        _attackTimer;
        private float        _moveTimer;
        private bool         _toolEquipped;
        private string       _targetType;     // "TreeLog", "Stump", "TreeBase", "MineRock5", etc.
        private int          _swingCount;      // consecutive successful attack swings on current target
        private int          _totalAttempts;   // total attack attempts (success + failure) on current target
        private int          _consecutiveFailures; // consecutive StartAttack failures (not animation-blocked)
        private bool         _reapproach;      // true = return to Moving used tighter approach distance
        private bool         _shouldRun;       // run decision from UpdateMoving — LateUpdate respects this
        private Vector3      _lockedMoveTarget;   // cached target pos for current Moving phase
        private bool         _moveTargetLocked;   // when true, use _lockedMoveTarget not live ClosestPoint
        private Vector3      _attackNodePos;       // MineRock5: position of active node when Attacking started
        private Vector3      _lockedAttackFaceTarget; // stable facing point for rock targets during Attacking
        private bool         _attackFaceLocked;
        private int          _claimedTargetId;     // GetInstanceID() of currently claimed target, or 0

        // Shared across all HarvestController instances — prevents multiple companions targeting same resource
        private static readonly HashSet<int> s_claimedTargets = new HashSet<int>();
        private ItemDrop.ItemData _pendingToolReequip; // deferred equip when EquipItem fails mid-animation

        // ── Directed harvest (one-shot from hotkey, works outside gather mode) ─
        private GameObject   _directedTarget;
        private int          _directedMode;       // ModeGatherWood or ModeGatherStone
        private bool         _isDirectedHarvest;

        // ── Blacklist — tracks unreachable targets to prevent infinite stuck loops ──
        private readonly Dictionary<int, float> _blacklist = new Dictionary<int, float>();
        private static float BlacklistDuration => ModConfig.HarvestBlacklistDuration.Value;

        // ── Forage filter — cached from config ──
        private static HashSet<string> _forageFilter;
        private static string          _forageFilterRaw;
        private static bool            _forageAll = true;

        private const HarvestState State_Idle           = HarvestState.Idle;
        private const HarvestState State_Moving         = HarvestState.Moving;
        private const HarvestState State_Attacking      = HarvestState.Attacking;
        private const HarvestState State_CollectingDrops = HarvestState.CollectingDrops;

        // ── Config ──────────────────────────────────────────────────────────
        private static float ScanInterval   => ModConfig.HarvestScanInterval.Value;
        private static float ScanRadius     => ModConfig.HarvestScanRadius.Value;
        private static float AttackInterval => ModConfig.HarvestAttackInterval.Value;
        private const float AttackRetry    = 0.25f;
        private const float MoveTimeout    = 10f;
        private const float ArrivalSlack   = 0.5f;
        private const int   WhiffRetryMax  = 20;   // abandon after this many SUCCESSFUL swings without destroy
        private const int   TotalAttemptMax = 30;  // abandon after this many total attempts (incl. failures)

        // Drop collection config
        private static float DropScanRadius  => ModConfig.HarvestDropScanRadius.Value;
        private const float DropPickupRange = 3.0f;  // must be >= BaseAI.Follow() stop distance (~3m)
        private const float DropTimeout     = 10f;   // max time in CollectingDrops before giving up
        private const float DropScanDelay   = 0.5f;  // brief delay after destroy before scanning (drops need time to spawn)

        // Weight limit — stop gathering when near max carry weight
        private static float OverweightThreshold => ModConfig.HarvestOverweightThreshold.Value;
        private static readonly int s_pickedHash = "picked".GetStableHashCode();
        private float _overweightMsgTimer;
        private Container _cachedChest;
        private float     _chestScanTimer;

        // ── Per-instance scan buffer (thread-safe with multiple companions) ─
        private readonly Collider[]  _scanBuffer = new Collider[1024];
        private readonly Collider[]  _dropBuffer = new Collider[128];
        private readonly HashSet<int> _seenIds   = new HashSet<int>();
        // Two-pass tree scan: track best standing tree as fallback
        private GameObject _bestTree;
        private float      _bestTreeDist;

        // ── Drop collection state ─────────────────────────────────────────
        private Vector3    _lastDestroyPos;
        private GameObject _currentDrop;
        private float      _dropTimer;
        private float      _dropScanDelayTimer;
        private int        _dropsPickedUp;
        private int        _itemLayerMask;

        // ── Logging ────────────────────────────────────────────────────────
        private string _tag;   // per-companion log prefix
        private float _moveLogTimer;
        private float _heartbeatTimer;
        private const float MoveLogInterval = 1f;
        private const float HeartbeatInterval = 5f;

        // ── Public state ────────────────────────────────────────────────────

        /// <summary>True when companion is in any gather action mode (wood/stone/ore).</summary>
        public bool IsInGatherMode { get; private set; }

        /// <summary>True when actively moving to or attacking a resource.</summary>
        public bool IsActive => _state != State_Idle;

        /// <summary>True when navigating to a resource target (not yet attacking/collecting).</summary>
        internal bool IsMoving => _state == State_Moving;

        /// <summary>True when in the Attacking state (swinging at a resource). Used by CompanionAI
        /// to suppress proactive jump between attack swings.</summary>
        internal bool IsAttacking => _state == State_Attacking;

        /// <summary>Current harvest state name for external diagnostics.</summary>
        internal string CurrentStateName => _state.ToString();

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _ai        = GetComponent<CompanionAI>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup       = GetComponent<CompanionSetup>();
            _doorHandler = GetComponent<DoorHandler>();
            _stamina     = GetComponent<CompanionStamina>();
            _talk        = GetComponent<CompanionTalk>();

            // Build a per-companion tag: "[Harvest#1234]" using instance ID
            int id = GetInstanceID();
            _tag = $"[Harvest#{id & 0xFFFF}]";

            // Layer mask for item drops — same as Player.m_autoPickupMask
            _itemLayerMask = LayerMask.GetMask("item");

            Log($"Awake — nview={_nview != null} ai={_ai != null} " +
                $"humanoid={_humanoid != null} character={_character != null} " +
                $"setup={_setup != null} instanceId={id}");
        }

        private void OnDestroy()
        {
            UnclaimTarget();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;

            // Update tag with companion name if available (may be set after Awake)
            if (_character != null && !_tag.Contains("|"))
            {
                string name = _character.m_name;
                if (!string.IsNullOrEmpty(name) && name != "HC_Companion" && name != "HC_Dverger")
                    _tag = $"[Harvest#{GetInstanceID() & 0xFFFF}|{name}]";
            }

            int mode = GetMode();
            bool gatherMode = (mode >= CompanionSetup.ModeGatherWood
                            && mode <= CompanionSetup.ModeGatherOre)
                           || mode == CompanionSetup.ModeForage;

            if (!gatherMode && !_isDirectedHarvest)
            {
                if (IsInGatherMode) ExitGatherMode();
                return;
            }

            if (gatherMode && !IsInGatherMode) EnterGatherMode();

            // Weight check — stop gathering if overweight
            if (IsOverweight())
            {
                // Already walking to a chest for deposit — don't re-trigger
                if (_ai != null && _ai.PendingDepositContainer != null)
                    return;

                // StayHome: try to find a chest and deposit instead of walking to player
                if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition())
                {
                    var chest = FindNearestChest();
                    if (chest != null)
                    {
                        LogWarn($"OVERWEIGHT+StayHome — depositing to chest \"{chest.m_name}\" " +
                            $"dist={Vector3.Distance(transform.position, chest.transform.position):F1}");
                        ResetToIdle();
                        _ai?.SetPendingDeposit(chest, _humanoid);
                        // Don't exit gather mode — after deposit completes,
                        // HarvestController resumes scanning naturally
                        return;
                    }
                    else
                    {
                        if (_talk != null && _overweightMsgTimer <= 0f)
                        {
                            _talk.Say(ModLocalization.Loc("hc_speech_no_chest"), "Overweight");
                            _overweightMsgTimer = 15f;
                        }
                        LogWarn($"OVERWEIGHT+StayHome — no chest found, stopping gather");
                        ExitGatherMode();
                        return;
                    }
                }

                if (_talk != null && _overweightMsgTimer <= 0f)
                {
                    _talk.Say(ModLocalization.Loc("hc_speech_overweight"), "Overweight");
                    _overweightMsgTimer = 15f;
                }
                LogWarn($"OVERWEIGHT — weight={GetCurrentWeight():F1} >= {OverweightThreshold}, " +
                    $"stopping gather, reverting to follow");
                ExitGatherMode();

                // Force mode back to Follow via ZDO
                if (_nview != null && _nview.GetZDO() != null)
                    _nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                return;
            }
            _overweightMsgTimer -= Time.deltaTime;
            _chestScanTimer     -= Time.deltaTime;

            // Pause harvesting while combat AI is active — it owns movement
            if (_ai != null && _ai.IsInCombat)
                return;

            // Pause harvesting while UI is open — companion should stand still
            if (IsCompanionUIOpen())
            {
                if (_state != State_Idle) _ai?.StopMoving();
                return;
            }

            // Heartbeat — full state dump every 5s
            _heartbeatTimer -= Time.deltaTime;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = HeartbeatInterval;
                LogHeartbeat();
            }

            switch (_state)
            {
                case State_Idle:           UpdateIdle(mode);        break;
                case State_Moving:         UpdateMoving();          break;
                case State_Attacking:      UpdateAttacking();       break;
                case State_CollectingDrops: UpdateCollectingDrops(); break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Heartbeat — comprehensive state dump for debugging
        // ══════════════════════════════════════════════════════════════════════

        private void LogHeartbeat()
        {
            var vel = _character?.GetVelocity() ?? Vector3.zero;
            var followTarget = _ai?.GetFollowTarget();
            var rightItem = ReflectionHelper.GetRightItem(_humanoid);
            var leftItem  = ReflectionHelper.GetLeftItem(_humanoid);
            var creature  = _ai != null ? _ai.m_targetCreature : null;

            bool inAttack = _character is Humanoid h ? h.InAttack() : false;

            string dropInfo = _state == State_CollectingDrops
                ? $" dropTarget=\"{(_currentDrop != null ? _currentDrop.name : "scanning")}\" dropsPickedUp={_dropsPickedUp} dropTimer={_dropTimer:F1}"
                : "";

            Log($"♥ HEARTBEAT state={_state} " +
                $"target=\"{(_target != null ? _target.name : "null")}\" " +
                $"pos={transform.position:F1} vel={vel.magnitude:F1} " +
                $"followTarget=\"{followTarget?.name ?? "null"}\" " +
                $"right=\"{rightItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"left=\"{leftItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"inAttack={inAttack} " +
                $"toolEquipped={_toolEquipped} suppress={_setup?.SuppressAutoEquip ?? false} " +
                $"scanTimer={_scanTimer:F1} attackTimer={_attackTimer:F1} " +
                $"moveTimer={_moveTimer:F1} " +
                $"combatTarget=\"{creature?.m_name ?? "null"}\"" +
                dropInfo);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Called by UI or CancelAll when action mode changes.</summary>
        public void NotifyActionModeChanged()
        {
            int mode = GetMode();
            bool isGather = (mode >= CompanionSetup.ModeGatherWood
                          && mode <= CompanionSetup.ModeGatherOre)
                         || mode == CompanionSetup.ModeForage;

            Log($"NotifyActionModeChanged — new mode={mode} isGather={isGather} " +
                $"(wasGather={IsInGatherMode}, state={_state})");

            CancelDirectedTarget();

            // Immediately exit gather mode when switching away — don't defer
            // to the next Update() frame. This ensures hold-to-cancel (CancelAll)
            // restores the combat loadout and stops pathfinding right away.
            if (!isGather && IsInGatherMode)
                ExitGatherMode();
            else
                ResetToIdle();
        }

        /// <summary>
        /// Direct the companion to harvest a specific target (one-shot from hotkey).
        /// Works regardless of current action mode — companion will harvest the
        /// target, collect drops, then return to its previous behavior.
        /// </summary>
        public void SetDirectedTarget(GameObject target)
        {
            if (target == null) return;

            // Determine tool type from target components
            int harvestMode = DetermineHarvestModeStatic(target);
            if (harvestMode < 0)
            {
                Log($"SetDirectedTarget REJECTED — can't determine tool for \"{target.name}\"");
                return;
            }

            // Check if the companion's best tool can actually damage this target
            int requiredTier = GetMinToolTier(target);
            int bestTier = GetBestToolTier(harvestMode);
            if (requiredTier > bestTier)
            {
                Log($"SetDirectedTarget REJECTED — \"{target.name}\" requires tier {requiredTier}, " +
                    $"best tool is tier {bestTier}");

                string companionName = "Companion";
                if (_nview?.GetZDO() != null)
                {
                    string custom = _nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                    if (!string.IsNullOrEmpty(custom)) companionName = custom;
                }
                Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                    ModLocalization.LocFmt("hc_msg_tools_weak", companionName));
                return;
            }

            _directedTarget = target;
            _directedMode = harvestMode;
            _isDirectedHarvest = true;

            // Force into Moving state toward this target
            _target = target;
            _targetType = ClassifyTargetType(target, harvestMode);
            _state = State_Moving;
            _moveTimer = 0f;
            _moveTargetLocked = false;
            _swingCount = 0;
            _totalAttempts = 0;
            ClaimTarget();
            _scanTimer = ScanInterval; // don't auto-scan while directed

            // Equip the right tool
            var tool = FindBestTool(harvestMode);
            if (tool != null)
                EquipTool(tool);

            Log($"SetDirectedTarget — \"{target.name}\" type={_targetType} " +
                $"mode={harvestMode} tool=\"{tool?.m_shared?.m_name ?? "NONE"}\"");
        }

        /// <summary>Cancel any directed harvest in progress.</summary>
        public void CancelDirectedTarget()
        {
            if (!_isDirectedHarvest) return;

            Log("CancelDirectedTarget — clearing directed harvest");
            _directedTarget = null;
            _isDirectedHarvest = false;

            if (!IsInGatherMode)
            {
                ResetToIdle();
                RestoreLoadout();
            }
        }

        /// <summary>
        /// Determines whether a target needs an axe (Wood) or pickaxe (Stone/Ore).
        /// Returns ModeGatherWood, ModeGatherStone, or -1 if not harvestable.
        /// </summary>
        internal static int DetermineHarvestModeStatic(GameObject target)
        {
            if (target.GetComponent<TreeBase>() != null) return CompanionSetup.ModeGatherWood;
            if (target.GetComponent<TreeLog>() != null) return CompanionSetup.ModeGatherWood;

            var dest = target.GetComponent<Destructible>();
            if (dest != null && target.name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return CompanionSetup.ModeGatherWood;

            // MineRock5 (multi-node boulders) excluded — AI can't reliably
            // navigate between nodes without getting stuck.
            if (target.GetComponent<MineRock5>() != null) return -1;
            if (target.GetComponent<MineRock>() != null) return CompanionSetup.ModeGatherStone;

            if (dest != null && dest.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                && dest.m_damages.m_chop == HitData.DamageModifier.Immune)
                return CompanionSetup.ModeGatherStone;

            if (dest != null && dest.m_damages.m_chop != HitData.DamageModifier.Immune)
                return CompanionSetup.ModeGatherWood;

            return -1;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Mode transitions
        // ══════════════════════════════════════════════════════════════════════

        private void EnterGatherMode()
        {
            int mode = GetMode();
            string modeName = mode == CompanionSetup.ModeGatherWood  ? "Wood"
                            : mode == CompanionSetup.ModeGatherStone ? "Stone"
                            : mode == CompanionSetup.ModeGatherOre   ? "Ore"
                            : mode == CompanionSetup.ModeForage      ? "Forage"
                            : "Unknown";
            IsInGatherMode = true;
            _scanTimer = 0f; // scan immediately on mode entry
            _heartbeatTimer = 0f; // heartbeat immediately

            var followTarget = _ai?.GetFollowTarget();
            if (mode == CompanionSetup.ModeForage)
                LogInfo($"Foraging started — scanning for pickables near {transform.position:F1}");
            Log($"ENTER gather mode: {modeName} (mode={mode}) " +
                $"followTarget=\"{followTarget?.name ?? "null"}\" " +
                $"pos={transform.position:F1}");
        }

        private void ExitGatherMode()
        {
            Log($"EXIT gather mode — was state={_state} " +
                $"target=\"{(_target != null ? _target.name : "null")}\" " +
                $"toolEquipped={_toolEquipped}");

            IsInGatherMode = false;
            ResetToIdle();
            RestoreLoadout();

            // Restore follow target — StayHome takes priority over Follow.
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (_ai != null && stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
                Log("Restored patrol to home (StayHome active)");
            }
            else if (_ai != null && follow && Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                Log("Restored follow target to player (Follow=ON)");
            }
            else
            {
                Log("Follow OFF, no StayHome — idle");
            }
        }

        private void ResetToIdle()
        {
            var prevState = _state;
            _state      = State_Idle;
            _target     = null;
            _targetType = null;
            _swingCount    = 0;
            _totalAttempts = 0;
            _consecutiveFailures = 0;
            _reapproach    = false;
            _scanTimer     = 0f;
            _pendingToolReequip = null;
            _moveTargetLocked = false;
            _attackFaceLocked = false;
            UnclaimTarget();

            // If this was a directed harvest, clean up and restore loadout
            if (_isDirectedHarvest)
            {
                _isDirectedHarvest = false;
                _directedTarget = null;
                RestoreLoadout();
                Log($"ResetToIdle (directed harvest complete) — restored loadout");
            }

            // Restore follow target — StayHome takes priority over Follow.
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (_ai != null && stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
            }
            else if (_ai != null && follow && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);

            if (prevState != State_Idle)
                Log($"ResetToIdle (was {prevState}) — follow={follow} stayHome={stayHome}");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Idle — follow player + scan for resources
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdle(int mode)
        {
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            // Cleanup expired blacklist entries
            if (_blacklist.Count > 0)
            {
                float now = Time.time;
                List<int> expired = null;
                foreach (var kvp in _blacklist)
                {
                    if (now - kvp.Value > BlacklistDuration)
                    {
                        if (expired == null) expired = new List<int>();
                        expired.Add(kvp.Key);
                    }
                }
                if (expired != null)
                {
                    for (int i = 0; i < expired.Count; i++)
                        _blacklist.Remove(expired[i]);
                    Log($"Blacklist cleanup: removed {expired.Count} expired, {_blacklist.Count} remain");
                }
            }

            Log($"Scanning for targets... mode={mode} " +
                $"pos={transform.position:F1} radius={ScanRadius}");

            var target = ScanForTarget(mode);
            if (target == null)
            {
                Log("Scan found 0 valid targets — staying idle");
                return;
            }

            // Forage mode doesn't need a tool
            if (mode == CompanionSetup.ModeForage)
            {
                float forageDist = Vector3.Distance(transform.position, target.transform.position);
                LogInfo($"Forage target: \"{target.name}\" dist={forageDist:F1}m");
                Log($"Forage target acquired: \"{target.name}\" dist={forageDist:F1}m " +
                    $"pos={target.transform.position:F1}");

                _target = target;
                _targetType = "Pickable";
                _swingCount = 0;
                _totalAttempts = 0;
                ClaimTarget();
            }
            else
            {
                // Find appropriate tool
                var tool = FindBestTool(mode);
                if (tool == null)
                {
                    LogWarn($"No tool in inventory for mode {mode} — cannot harvest! " +
                        $"(need {(mode == CompanionSetup.ModeGatherWood ? "axe (chop dmg)" : "pickaxe (pickaxe dmg)")})");
                    LogInventoryContents();
                    return;
                }

                float dist = Vector3.Distance(transform.position, target.transform.position);
                Log($"Target acquired: \"{target.name}\" dist={dist:F1}m " +
                    $"pos={target.transform.position:F1} " +
                    $"tool=\"{tool.m_shared.m_name}\" " +
                    $"chop={tool.GetDamage().m_chop:F0} pick={tool.GetDamage().m_pickaxe:F0}");

                _target = target;
                _targetType = ClassifyTargetType(target, mode);
                _swingCount = 0;
                _totalAttempts = 0;
                EquipTool(tool);
                ClaimTarget();
            }

            // Clear follow target — HarvestController owns movement exclusively
            // during Moving/Attacking/CollectingDrops via MoveToPoint/MoveTowards.
            // CompanionAI.UpdateAI skips Follow() when harvest IsActive, so null
            // follow won't trigger IdleMovement's StopMoving().
            if (_ai != null)
            {
                _ai.SetFollowTarget(null);
                Log($"Target set: \"{_target.name}\" — HarvestController driving movement");
            }

            _moveTimer = 0f;
            _moveLogTimer = 0f;
            _moveTargetLocked = false;
            _attackFaceLocked = false;
            _state = State_Moving;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Moving — BaseAI.Follow() drives pathfinding, we reinforce + check arrival
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
            // Retry deferred tool equip — EquipItem fails silently during attack animations
            if (_pendingToolReequip != null && _humanoid != null && !_humanoid.InAttack())
            {
                Log($"Retrying deferred equip: \"{_pendingToolReequip.m_shared.m_name}\"");
                EquipTool(_pendingToolReequip);
            }

            // DoorHandler is actively opening a door — pause movement so we don't
            // fight its MoveTowards with our MoveToPoint. Also pause the stuck
            // timer since the delay is the door handler's fault, not ours.
            if (_doorHandler != null && _doorHandler.IsActive)
                return;

            if (!IsTargetValid(_target))
            {
                Log("Target gone while moving — waiting 1.5s then rescanning");
                ResetToIdle();
                _scanTimer = 1.5f;
                return;
            }

            // Early arrival — if already horizontally within attack range, skip pathfinding.
            // Handles standing on top of ore: NavMesh can't path to a point below,
            // but the companion can swing from where it is.
            {
                Vector3 earlyPos = GetEffectiveTargetPosition();
                Vector3 earlyDir = earlyPos - transform.position;
                earlyDir.y = 0f;
                float earlyRange = _targetType == "Pickable" ? 1.5f : GetAttackRange();
                if (earlyDir.magnitude <= earlyRange)
                {
                    Log($"Early arrival — already within horizontal range " +
                        $"(horiz={earlyDir.magnitude:F1} range={earlyRange:F1})");
                    if (_ai != null) _ai.StopMoving();
                    if (IsRockLikeTarget())
                    {
                        _lockedAttackFaceTarget = GetRockFacePosition();
                        _attackFaceLocked = true;
                    }
                    else
                        _attackFaceLocked = false;
                    FaceTarget();
                    _reapproach = false;
                    _attackTimer = 0.3f;
                    _state = State_Attacking;
                    if (_targetType == "MineRock5")
                        _attackNodePos = GetEffectiveTargetPosition();
                    return;
                }
            }

            float dt = Time.deltaTime;

            // Don't count time toward stuck timeout while locked in attack animation —
            // the companion physically can't move, so it's not a pathfinding failure.
            // Check velocity TOWARD the target, not total velocity — a companion
            // circling a tree on a slope has high speed but zero approach progress.
            // Only slow the timer when genuinely closing distance (toward speed > 1 m/s).
            bool inAttack = _humanoid != null && _humanoid.InAttack();
            if (!inAttack)
            {
                Vector3 velVec = _character?.GetVelocity() ?? Vector3.zero;
                Vector3 dirToTgt = _target.transform.position - transform.position;
                dirToTgt.y = 0f;
                float towardSpeed = dirToTgt.sqrMagnitude > 0.01f
                    ? Vector3.Dot(velVec, dirToTgt.normalized) : 0f;
                float stuckRate = towardSpeed > 1f ? 0.3f : 1f;
                _moveTimer += dt * stuckRate;
            }

            // Stuck timeout — give up and scan for a new target
            if (_moveTimer > MoveTimeout)
            {
                float stuckDist = Vector3.Distance(transform.position, _target.transform.position);
                var vel = _character?.GetVelocity() ?? Vector3.zero;
                LogWarn($"STUCK timeout ({MoveTimeout}s) — giving up on \"{_target.name}\" " +
                    $"dist={stuckDist:F1}m vel={vel.magnitude:F1} " +
                    $"myPos={transform.position:F1} targetPos={_target.transform.position:F1} " +
                    $"targetY={_target.transform.position.y:F2} myY={transform.position.y:F2} " +
                    $"heightDiff={(_target.transform.position.y - transform.position.y):F2}");
                _blacklist[_target.GetInstanceID()] = Time.time;
                Log($"Blacklisted \"{_target.name}\" (id={_target.GetInstanceID()}) for {BlacklistDuration}s");
                ResetToIdle();
                return;
            }

            bool isForage = _targetType == "Pickable";
            float range = isForage ? 1.5f : GetAttackRange();

            // Cache the target position for the duration of this Moving phase.
            // GetEffectiveTargetPosition() uses Collider.ClosestPoint, which shifts
            // each frame as the companion moves around the rock — this causes NavMesh
            // to continuously recalculate the path, making the companion oscillate
            // left-right near mineral rocks (MineRock5/MineRock).
            if (!_moveTargetLocked)
            {
                _lockedMoveTarget = GetEffectiveTargetPosition();
                _moveTargetLocked = true;
            }
            Vector3 targetPos = _lockedMoveTarget;
            float distToTarget = Vector3.Distance(transform.position, targetPos);

            // Use MoveToPoint (pathfinding) for all distances. This handles
            // slopes and terrain obstacles that MoveTowards can't climb.
            // CompanionAI.UpdateAI skips Follow() when harvest is active,
            // so there's no dual-control jitter.
            // After re-approach (attacks failed/whiffed), get much closer.
            // Re-approach uses walk mode (run=false) so MoveTo's internal
            // stop distance is 0.5m instead of 1.0m, letting the companion
            // get genuinely close to the target.
            float moveGoal;
            bool runToTarget;
            bool rockLikeTarget = IsRockLikeTarget();
            if (isForage)
            {
                moveGoal = 1.2f;           // tight arrival for picking
                runToTarget = false;        // walk for natural "picking" look
            }
            else if (_reapproach)
            {
                moveGoal = range * 0.5f;   // closer than normal but outside target's collider
                runToTarget = false;        // walk for tighter stop distance
            }
            else if (IsLowTarget())
            {
                moveGoal = range * 0.3f;
                // Walk when within 8m to avoid overshooting low targets
                runToTarget = distToTarget > 8f;
            }
            else if (rockLikeTarget)
            {
                // Get close enough that the pickaxe actually hits the rock.
                // range - 0.2f was too far — NavMesh often overshoots the stop distance,
                // leaving the companion ~1m short and digging into the ground instead.
                moveGoal = range * 0.5f;
                runToTarget = distToTarget > 6f;
            }
            else
            {
                moveGoal = range * 0.5f;
                // Walk when within 8m to prevent momentum overshoot.
                // Running at close range causes the companion to sprint past the
                // target and then turn around, creating visible jitter.
                runToTarget = distToTarget > 8f;
            }
            _shouldRun = runToTarget;
            bool moveResult = _ai.MoveToPoint(dt, targetPos, moveGoal, runToTarget);

            // When navmesh is retrying (claimed done but still far), stop and wait —
            // the tiles may not be built yet and ctx-steer hasn't activated yet.
            // Do NOT stop during ctx-steer fallback — ctx-steer IS the wall avoidance
            // and calling StopMoving() on the same frame cancels its MoveTowards output.
            if (_ai.IsNavMeshRetrying)
            {
                _ai.StopMoving();
            }
            // Direct walk push — only when NavMesh is NOT actively handling navigation
            // (ctx-steer or retry state) AND the companion isn't already moving.
            // Guarding with IsNavMeshFailing prevents overriding ctx-steer's direction;
            // guarding with vel < 1f prevents overriding a live NavMesh path that curves
            // around small terrain rocks (which would cause left-right oscillation).
            else if (!moveResult && distToTarget < 4f && !_ai.IsNavMeshFailing)
            {
                float pushVel = _character?.GetVelocity().magnitude ?? 0f;
                if (pushVel < 1f)
                {
                    Vector3 dir = (targetPos - transform.position);
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.01f)
                        _ai.MoveTowards(dir.normalized, false);
                }
            }
            // Pathfinding "completed" but we haven't actually arrived — height
            // differences or navmesh edges cause MoveTo to return true while the
            // companion is still out of weapon range with zero velocity.
            else if (moveResult && distToTarget > moveGoal + 0.5f)
            {
                var vel = _character?.GetVelocity() ?? Vector3.zero;
                if (vel.magnitude < 0.3f)
                {
                    Vector3 dir = (targetPos - transform.position);
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.01f)
                        _ai.MoveTowards(dir.normalized, false);
                }
            }

            // Periodic movement logging (every 1s to avoid spam)
            _moveLogTimer -= dt;
            if (_moveLogTimer <= 0f)
            {
                _moveLogTimer = MoveLogInterval;
                float distNow = Vector3.Distance(transform.position, targetPos);
                var velocity = _character?.GetVelocity() ?? Vector3.zero;
                var followTarget = _ai?.GetFollowTarget();

                Log($"Moving → \"{_target.name}\" dist={distNow:F1}m " +
                    $"range={range:F1} timer={_moveTimer:F1}s " +
                    $"moveOK={moveResult} vel={velocity.magnitude:F1} velY={velocity.y:F2} " +
                    $"follow=\"{followTarget?.name ?? "null"}\" " +
                    $"inAttack={inAttack} " +
                    $"pos={transform.position:F1} tgt={targetPos:F1} " +
                    $"heightDiff={(targetPos.y - transform.position.y):F2}");
            }

            // Check if we've arrived — transition to Attacking once within weapon range.
            // Use horizontal distance as an alternative — height differences on slopes
            // inflate 3D distance even when the companion is horizontally close enough
            // to swing (weapon hit detection is 3D collision-based, not distance-gated).
            distToTarget = Vector3.Distance(transform.position, targetPos);
            Vector3 toTarget = targetPos - transform.position;
            float horizDist = new Vector3(toTarget.x, 0f, toTarget.z).magnitude;
            bool isLow = IsLowTarget();
            float arrivalDist;
            if (isForage)
                arrivalDist = 1.5f;
            else if (_reapproach)
                arrivalDist = range * 0.7f;   // tighter than re-approach trigger (0.85*range)
            else if (isLow)
                arrivalDist = range;
            else if (rockLikeTarget)
                arrivalDist = range;           // no slack for rocks — pickaxes have short range
            else
                arrivalDist = range + ArrivalSlack;
            // Safety: if re-approach can't reach the tighter distance after 3s
            // but the companion IS within normal attack range, stop trying to
            // get closer and resume attacking — the target's collider likely
            // prevents getting any nearer.
            if (_reapproach && _moveTimer > 3f)
            {
                float normalArrival = (isLow || rockLikeTarget) ? range : range + ArrivalSlack;
                if (distToTarget <= normalArrival || horizDist <= normalArrival)
                    arrivalDist = normalArrival;
            }
            if (distToTarget <= arrivalDist || horizDist <= arrivalDist)
            {
                if (_ai != null) _ai.StopMoving();
                if (rockLikeTarget)
                {
                    _lockedAttackFaceTarget = GetRockFacePosition();
                    _attackFaceLocked = true;
                }
                else
                {
                    _attackFaceLocked = false;
                }
                FaceTarget();
                _reapproach = false;  // consumed
                _attackTimer = 0.3f; // brief pause before first swing
                _state = State_Attacking;
                // Cache the active node position so we detect when it dies (node shifts > 1.5m)
                if (_targetType == "MineRock5")
                    _attackNodePos = GetEffectiveTargetPosition();

                Log($"ARRIVED at \"{_target.name}\" dist={distToTarget:F1}m " +
                    $"(threshold={arrivalDist:F1} isLow={isLow}) — switching to Attacking");
            }
        }

        /// <summary>
        /// LateUpdate runs AFTER all Update calls, including CompanionAI.UpdateAI.
        /// We force run speed here so it overrides Follow()'s walk decision
        /// for distances under 10m. Also shuffles the companion closer during
        /// Attacking state — BaseAI.Follow() stops at ~3m but small targets
        /// (stumps) need the companion within actual weapon range to land hits.
        /// </summary>
        private void LateUpdate()
        {
            if (_character == null) return;
            if (IsCompanionUIOpen()) return;

            if (_state == State_Moving || (_state == State_CollectingDrops && _currentDrop != null))
            {
                // Forage: walk for natural picking look
                bool forageWalk = _targetType == "Pickable" && _state == State_Moving;
                // Respect the run/walk decision from UpdateMoving — forcing run at close
                // range causes momentum overshoot, making the companion sprint past stones.
                _character.SetRun(!forageWalk && _shouldRun);
            }

            // During attack, shuffle closer if beyond actual hit range.
            // NavMesh stop distance is imprecise — the companion can end up 1m short
            // and hit the ground instead of the target. This nudges them closer.
            // ONLY for low targets (stumps/logs) — rocks should stay planted once
            // arrived. The shuffle's MoveToPoint fights with StopMoving and causes
            // the companion to run back and forth over rocks.
            if (_state == State_Attacking && _target != null && _ai != null
                && IsLowTarget() && !IsRockLikeTarget())
            {
                Vector3 effectivePos = GetEffectiveTargetPosition();
                float dist = Vector3.Distance(transform.position, effectivePos);
                float range = GetAttackRange();
                bool needsCloser = _swingCount >= 2 || _totalAttempts >= 2;
                float shuffleThreshold = needsCloser ? range * 0.5f : range * 0.8f;
                float shuffleGoal     = needsCloser ? range * 0.15f : range * 0.5f;
                if (dist > shuffleThreshold)
                    _ai.MoveToPoint(Time.deltaTime, effectivePos, shuffleGoal, false);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Attacking — swing at resource
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            // Forage mode: interact with Pickable instead of attacking
            int currentMode = GetMode();
            if (currentMode == CompanionSetup.ModeForage)
            {
                var pickable = _target?.GetComponentInParent<Pickable>();
                if (pickable != null)
                {
                    // Re-validate: another companion or the player may have picked it while we walked over
                    var pnv = pickable.GetComponent<ZNetView>();
                    bool alreadyPicked = pnv != null && pnv.GetZDO() != null
                        && pnv.GetZDO().GetBool(s_pickedHash, false);
                    if (alreadyPicked)
                    {
                        LogInfo($"Forage target already picked — skipping \"{pickable.name}\"");
                    }
                    else
                    {
                        FaceTarget();
                        pickable.Interact(_humanoid, false, false);
                        LogInfo($"Foraged: \"{pickable.name}\" at {pickable.transform.position:F1}");
                    }
                }
                else
                {
                    LogWarn($"Forage target has no Pickable — skipping \"{_target?.name ?? "null"}\"");
                }
                _lastDestroyPos = _target?.transform.position ?? transform.position;
                _currentDrop = null;
                _dropTimer = 0f;
                _dropScanDelayTimer = DropScanDelay;
                _dropsPickedUp = 0;
                _state = State_CollectingDrops;
                if (_ai != null) _ai.StopMoving();
                return;
            }

            if (!IsTargetValid(_target))
            {
                Log($"Target DESTROYED after {_swingCount} hits, {_totalAttempts} attempts " +
                    $"— entering drop collection");
                _lastDestroyPos = transform.position;

                // Tree just fell — try to immediately claim the spawned log before
                // another companion's next scan can grab it.
                if (_targetType == "TreeBase")
                {
                    var log = TryFindNearbyLog(transform.position);
                    if (log != null && !s_claimedTargets.Contains(log.GetInstanceID()))
                    {
                        UnclaimTarget();
                        _target = log;
                        _targetType = ClassifyTargetType(log, CompanionSetup.ModeGatherWood);
                        _swingCount = 0; _totalAttempts = 0;
                        ClaimTarget();
                        _moveTargetLocked = false;
                        _moveTimer = 0f;
                        _state = State_Moving;
                        Log($"Tree fell — immediately targeting fallen log \"{log.name}\"");
                        if (_ai != null) _ai.StopMoving();
                        return;
                    }
                }

                _currentDrop = null;
                _dropTimer = 0f;
                _dropScanDelayTimer = DropScanDelay;
                _dropsPickedUp = 0;
                _state = State_CollectingDrops;

                // Stop moving while we wait for drops to spawn
                if (_ai != null) _ai.StopMoving();
                return;
            }

            FaceTarget();

            // Check distance — may have drifted or never been close enough.
            // Trees use range + 1.5f for hysteresis vs the arrival threshold (range + 0.5f).
            // Rocks use a tighter range + 0.8f — pickaxes have short range (~2m) and the
            // generous 1.5f margin was letting companions stay 3.5m away, hitting the ground
            // instead of the rock face.
            // For rocks with a locked face target, use that position instead of live
            // ClosestPoint — ClosestPoint shifts every frame as the companion moves,
            // causing false "too far" triggers that send the companion back to Moving.
            bool rockLike = IsRockLikeTarget();
            Vector3 effectivePos = (rockLike && _attackFaceLocked)
                ? _lockedAttackFaceTarget
                : GetEffectiveTargetPosition();
            float dist = Vector3.Distance(transform.position, effectivePos);
            float range = GetAttackRange();
            float tooFarThreshold = rockLike ? range + 1.2f : range + 1.5f;
            if (dist > tooFarThreshold)
            {
                Log($"Too far from target: dist={dist:F1}m " +
                    $"(threshold={tooFarThreshold:F1}) — returning to Moving state");
                _reapproach = true;
                _attackFaceLocked = false;
                _moveTimer = 0f;
                _moveLogTimer = 0f;
                _moveTargetLocked = false;
                _state = State_Moving;
                return;
            }

            _attackTimer -= Time.deltaTime;
            if (_attackTimer > 0f) return;

            // Check weapon is still equipped AND is the correct tool type.
            // Combat interruption can replace the harvest tool with a combat weapon.
            var weapon = ReflectionHelper.GetRightItem(_humanoid);
            int mode = GetMode();
            bool needsReequip = weapon == null;
            if (!needsReequip)
            {
                // Verify it's the right type of tool for the current gather mode
                var dmg = weapon.GetDamage();
                if (mode == CompanionSetup.ModeGatherWood)
                    needsReequip = dmg.m_chop <= 0f;
                else
                    needsReequip = dmg.m_pickaxe <= 0f;
            }

            if (needsReequip)
            {
                Log($"Wrong/missing tool: \"{weapon?.m_shared?.m_name ?? "NONE"}\" — re-equipping for mode={mode}");
                var tool = FindBestTool(mode);
                if (tool != null)
                    EquipTool(tool);
                else
                {
                    CompanionsPlugin.Log.LogError($"{_tag} Cannot find tool to re-equip — aborting harvest");
                    ResetToIdle();
                    return;
                }
                // Re-read weapon after equip — may still be null if EquipTool deferred
                weapon = ReflectionHelper.GetRightItem(_humanoid);
                if (weapon == null)
                {
                    _attackTimer = AttackRetry;
                    return;
                }
            }

            // Log attack preconditions
            bool inAttack = _humanoid != null && _humanoid.InAttack();
            bool inDodge  = _humanoid != null && _humanoid.InDodge();
            bool stagger  = _character != null && _character.IsStaggering();

            if (inAttack || inDodge || stagger)
            {
                Log($"Attack blocked by animation: inAttack={inAttack} " +
                    $"inDodge={inDodge} stagger={stagger} — will retry in {AttackRetry}s");
                _attackTimer = AttackRetry;
                return;
            }

            // Wait for stamina to regenerate before spamming attacks.
            // Primary attack costs ~12-20 stamina; without food, base max is 25.
            float priCost = weapon.m_shared.m_attack.m_attackStamina;
            if (_stamina != null && priCost > 0f && _stamina.Stamina < priCost + 0.1f)
            {
                _attackTimer = 0.5f;
                return;
            }

            // Power attack (secondary, downward swing) is better for low targets
            // (logs/stumps) but costs more stamina. Start with primary attack;
            // only escalate to secondary after 3+ primary whiffs AND enough stamina.
            bool wantPower = _swingCount >= 3 && weapon.HaveSecondaryAttack();
            bool usePowerAttack = false;
            if (wantPower)
            {
                // Check if companion has enough stamina for the secondary attack.
                // Secondary attacks typically cost more than base stamina (25),
                // so without food they'd fail 100% of the time.
                float secCost = weapon.m_shared.m_secondaryAttack.m_attackStamina;
                float curStam = _stamina != null ? _stamina.Stamina : 999f;
                usePowerAttack = curStam >= secCost + 1f;
            }

            bool attacked = _humanoid != null && _humanoid.StartAttack(null, usePowerAttack);
            _totalAttempts++;
            _attackTimer = attacked ? AttackInterval : 0.5f; // failed retry at 0.5s (not 0.25s spam)
            if (attacked)
            {
                _swingCount++;
                _consecutiveFailures = 0;

                // MineRock5: detect when the active node dies (effective position shifts
                // significantly = that node was destroyed, next closest node is elsewhere).
                // Always reposition — boulder nodes can be spread 3-5m apart and the
                // companion needs to walk to the correct side of the next node.
                if (_targetType == "MineRock5" && _target != null)
                {
                    Vector3 nowNode = GetEffectiveTargetPosition();
                    float nodeShift = Vector3.Distance(nowNode, _attackNodePos);
                    if (nodeShift > 1.5f)
                    {
                        float distToNewNode = Vector3.Distance(transform.position, nowNode);
                        Log($"MineRock5 node destroyed — repositioning to next node " +
                            $"(dist={distToNewNode:F1}m shift={nodeShift:F1}m)");
                        _attackNodePos = nowNode;
                        _reapproach = true;
                        _attackFaceLocked = false;
                        _moveTimer = 0f; _moveLogTimer = 0f; _moveTargetLocked = false;
                        _state = State_Moving;
                        return;
                    }
                }
            }
            else
            {
                _consecutiveFailures++;
                // After 3 consecutive StartAttack failures (not animation-blocked),
                // return to Moving to re-approach closer.
                if (_consecutiveFailures >= 3)
                {
                    LogWarn($"Re-approach — {_consecutiveFailures} consecutive attack failures at dist={dist:F1}m " +
                        $"range={range:F1} — moving closer");
                    _reapproach = true;
                    _attackFaceLocked = false;
                    _consecutiveFailures = 0;
                    _moveTimer = 0f;
                    _moveLogTimer = 0f;
                    _moveTargetLocked = false;
                    _state = State_Moving;
                    return;
                }
            }

            // After several swings without destroy, the weapon probably isn't
            // connecting — force re-approach to get closer to the target.
            // Only trigger when genuinely far from the target (>85% of range),
            // since trees/rocks often survive many swings — don't interrupt
            // productive attacking when the companion is already close enough.
            // Trigger threshold (0.85*range) must be HIGHER than re-approach
            // arrival distance (0.7*range) to prevent infinite re-approach cycles.
            if (attacked && _swingCount > 0 && _swingCount % 5 == 0 && dist > range * 0.85f)
            {
                LogWarn($"Re-approach — {_swingCount} swings without destroy at dist={dist:F1}m " +
                    $"range={range:F1} — moving closer");
                _reapproach = true;
                _attackFaceLocked = false;
                _moveTimer = 0f;
                _moveLogTimer = 0f;
                _moveTargetLocked = false;
                _state = State_Moving;
                return;
            }

            // Bailout — abandon if too many successful swings without destroy OR
            // too many total attempts (catches infinite failure loops).
            if (_swingCount >= WhiffRetryMax || _totalAttempts >= TotalAttemptMax)
            {
                LogWarn($"Bailout — {_swingCount} hits, {_totalAttempts} attempts on \"{_target.name}\" " +
                    $"dist={dist:F1}m without destruction. Abandoning target.");
                _blacklist[_target.GetInstanceID()] = Time.time;
                Log($"Blacklisted \"{_target.name}\" (id={_target.GetInstanceID()}) for {BlacklistDuration}s");
                ResetToIdle();
                return;
            }

            if (attacked)
            {
                Log($"Attack swing → \"{_target.name}\" " +
                    $"success=True dist={dist:F1}m " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "NONE"}\" " +
                    $"power={usePowerAttack} swings={_swingCount}/{WhiffRetryMax} " +
                    $"attempts={_totalAttempts}/{TotalAttemptMax}");
            }
            else
            {
                // Throttle failure logs — only log first failure and every 5th after
                if (_totalAttempts <= 1 || _totalAttempts % 5 == 0)
                {
                    var animator = _humanoid?.GetComponentInChildren<Animator>();
                    string animState = "unknown";
                    if (animator != null)
                    {
                        var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                        animState = $"hash={stateInfo.shortNameHash} norm={stateInfo.normalizedTime:F2}";
                    }
                    float stam = _stamina != null ? _stamina.Stamina : -1f;
                    float stamMax = _stamina != null ? _stamina.MaxStamina : -1f;
                    LogWarn($"Attack FAILED — attempts={_totalAttempts}/{TotalAttemptMax} " +
                        $"animState=[{animState}] power={usePowerAttack} " +
                        $"weapon=\"{weapon?.m_shared?.m_name ?? "NONE"}\" " +
                        $"stamina={stam:F0}/{stamMax:F0} " +
                        $"atkCost={weapon.m_shared.m_attack.m_attackStamina:F0}");
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  CollectingDrops — pick up items near the destroy position
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCollectingDrops()
        {
            float dt = Time.deltaTime;
            _dropTimer += dt;

            // Overall timeout — give up and go back to harvesting
            if (_dropTimer > DropTimeout)
            {
                Log($"Drop collection timeout ({DropTimeout}s) — picked up {_dropsPickedUp} items, resuming harvest");
                FinishDropCollection();
                return;
            }

            // Brief delay after destroy to let drops spawn
            if (_dropScanDelayTimer > 0f)
            {
                _dropScanDelayTimer -= dt;
                return;
            }

            // If we have a current drop target, move toward it and try to pick up
            if (_currentDrop != null)
            {
                // Drop was picked up by someone else or despawned
                if (_currentDrop == null || _currentDrop.GetComponent<ZNetView>() == null ||
                    !_currentDrop.GetComponent<ZNetView>().IsValid())
                {
                    Log("Current drop target gone — scanning for more");
                    _currentDrop = null;
                    // Fall through to scan for next drop
                }
                else
                {
                    float distToDrop = Vector3.Distance(transform.position, _currentDrop.transform.position);

                    // Close enough to pick up
                    if (distToDrop <= DropPickupRange)
                    {
                        TryPickupDrop(_currentDrop);
                        _currentDrop = null;
                        // Fall through to scan for next drop
                    }
                    else
                    {
                        // Still moving toward drop — MoveTo reinforcement
                        _ai.MoveToPoint( dt, _currentDrop.transform.position, DropPickupRange * 0.5f, true);
                        // Log approach progress every 2s
                        _moveLogTimer -= dt;
                        if (_moveLogTimer <= 0f)
                        {
                            _moveLogTimer = 2f;
                            var itemDrop2 = _currentDrop.GetComponent<ItemDrop>();
                            string iName = itemDrop2?.m_itemData?.m_shared?.m_name ?? "?";
                            Log($"Approaching drop \"{iName}\" dist={distToDrop:F1}m " +
                                $"(pickup range={DropPickupRange:F1}) timer={_dropTimer:F1}s");
                        }
                        return;
                    }
                }
            }

            // Scan for next nearby drop
            var nextDrop = ScanForDrops();
            if (nextDrop == null)
            {
                Log($"No more drops nearby — picked up {_dropsPickedUp} items, resuming harvest");
                FinishDropCollection();
                return;
            }

            _currentDrop = nextDrop;
            float dist = Vector3.Distance(transform.position, nextDrop.transform.position);

            // HarvestController drives movement to drops via MoveToPoint.
            // No need to set follow target — CompanionAI skips Follow()
            // when harvest IsActive.

            var itemDrop = nextDrop.GetComponent<ItemDrop>();
            string itemName = itemDrop?.m_itemData?.m_shared?.m_name ?? "?";
            Log($"Moving to drop: \"{itemName}\" dist={dist:F1}m pos={nextDrop.transform.position:F1}");
        }

        private GameObject ScanForDrops()
        {
            int count = Physics.OverlapSphereNonAlloc(
                _lastDestroyPos, DropScanRadius, _dropBuffer, _itemLayerMask);

            if (count > _dropBuffer.Length) count = _dropBuffer.Length;

            GameObject best = null;
            float bestDist = float.MaxValue;
            int validCount = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _dropBuffer[i];
                if (col == null || col.attachedRigidbody == null) continue;

                var itemDrop = col.attachedRigidbody.GetComponent<ItemDrop>();
                if (itemDrop == null) continue;
                if (!itemDrop.m_autoPickup) continue;

                var nview = itemDrop.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                // Check weight limit before pickup
                if (IsOverweight())
                    continue;

                // Check if companion inventory can hold this item
                var inv = _humanoid?.GetInventory();
                if (inv != null)
                {
                    itemDrop.Load();
                    if (!inv.CanAddItem(itemDrop.m_itemData))
                    {
                        string rejName = itemDrop.m_itemData?.m_shared?.m_name ?? "?";
                        Log($"Drop scan: skipping \"{rejName}\" — inventory full/can't add");
                        continue;
                    }
                }

                validCount++;
                float dist = Vector3.Distance(transform.position, itemDrop.transform.position);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = itemDrop.gameObject;
                }
            }

            if (validCount > 0)
                Log($"Drop scan: {validCount} pickable items within {DropScanRadius}m of destroy pos");

            return best;
        }

        private void TryPickupDrop(GameObject dropGO)
        {
            if (dropGO == null || _humanoid == null) return;

            // Weight limit check
            if (IsOverweight())
            {
                Log($"Skipping pickup — overweight ({GetCurrentWeight():F1} >= {OverweightThreshold})");
                return;
            }

            var itemDrop = dropGO.GetComponent<ItemDrop>();
            if (itemDrop == null) return;

            string itemName = itemDrop.m_itemData?.m_shared?.m_name ?? "?";
            int stack = itemDrop.m_itemData?.m_stack ?? 0;

            // Use Humanoid.Pickup — autoequip=false (keep harvest tool), autoPickupDelay=false (skip 0.5s spawn delay)
            bool picked = _humanoid.Pickup(dropGO, false, false);

            if (picked)
            {
                _dropsPickedUp++;
                Log($"Picked up: \"{itemName}\" x{stack} (total {_dropsPickedUp} this cycle)");
            }
            else
            {
                Log($"Failed to pick up: \"{itemName}\" x{stack} — inventory full or item invalid");
            }
        }

        private void FinishDropCollection()
        {
            _currentDrop = null;
            _state = State_Idle;
            _scanTimer = 0f; // scan immediately for next harvest target
            UnclaimTarget();

            // Restore follow target — StayHome takes priority over Follow.
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (_ai != null && stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
            }
            else if (_ai != null && follow && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);

            Log($"FinishDropCollection — collected {_dropsPickedUp} items, follow={follow} stayHome={stayHome}");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Resource scanning
        // ══════════════════════════════════════════════════════════════════════

        private GameObject ScanForTarget(int mode)
        {
            // Forage mode: scan for Pickable objects
            if (mode == CompanionSetup.ModeForage)
                return ScanForPickable();

            int count = Physics.OverlapSphereNonAlloc(
                transform.position, ScanRadius, _scanBuffer);

            bool bufferFull = count >= _scanBuffer.Length;
            if (count > _scanBuffer.Length) count = _scanBuffer.Length;

            Log($"Physics scan: {count} colliders in {ScanRadius}m radius" +
                (bufferFull ? " ** BUFFER FULL — may miss targets! **" : ""));

            // Pre-compute the best tool tier for this mode so we can skip
            // targets that require a higher tier tool (e.g. birch with stone axe)
            int toolTier = GetBestToolTier(mode);

            _seenIds.Clear();
            GameObject best = null;
            float bestDist = float.MaxValue;
            // Fallback: track closest standing tree separately (only used if
            // no fallen logs or stumps are found in the scan radius)
            _bestTree = null;
            _bestTreeDist = float.MaxValue;
            int candidateCount = 0;
            int treeBaseCount = 0, treeLogCount = 0, stumpCount = 0, rockCount = 0;
            int skippedTierCount = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                string type;
                GameObject candidate = GetHarvestCandidateWithType(col, mode, out type);
                if (candidate == null) continue;
                if (!_seenIds.Add(candidate.GetInstanceID())) continue;

                // Skip targets that require a higher tool tier than we have
                int requiredTier = GetMinToolTier(candidate);
                if (requiredTier > toolTier)
                {
                    skippedTierCount++;
                    if (skippedTierCount <= 3)
                        Log($"  Skipping \"{candidate.name}\" — requires tier {requiredTier}, best tool is tier {toolTier}");
                    continue;
                }

                // Skip blacklisted (unreachable) targets
                if (_blacklist.ContainsKey(candidate.GetInstanceID()))
                {
                    // Log first blacklist skip per scan (avoid spam)
                    if (candidateCount == 0)
                        Log($"  Skipping blacklisted \"{candidate.name}\" (id={candidate.GetInstanceID()})");
                    continue;
                }

                // Skip targets already claimed by another companion
                if (s_claimedTargets.Contains(candidate.GetInstanceID())) continue;

                // StayHome leash: skip targets outside the configured home zone radius
                if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition())
                {
                    float distFromHome = Vector3.Distance(
                        _setup.GetHomePosition(), candidate.transform.position);
                    float homeRadius = _setup.GetHomeRadius();
                    if (distFromHome > homeRadius) continue;
                }

                // Player leash: skip targets beyond 40m from the player
                // Prevents the companion from drifting endlessly away
                if (Player.m_localPlayer != null &&
                    (_setup == null || !_setup.GetStayHome()))
                {
                    float distFromPlayer = Vector3.Distance(
                        Player.m_localPlayer.transform.position,
                        candidate.transform.position);
                    if (distFromPlayer > 40f) continue;
                }

                candidateCount++;
                float dist = Vector3.Distance(transform.position, candidate.transform.position);

                // Count by type
                bool isStandingTree = type == "TreeBase";
                if (isStandingTree) treeBaseCount++;
                else if (type == "TreeLog") treeLogCount++;
                else if (type == "Stump") stumpCount++;
                else rockCount++;

                // Log first 5 candidates and any TreeLogs (always interesting)
                if (candidateCount <= 5 || type == "TreeLog")
                    Log($"  Candidate #{candidateCount}: \"{candidate.name}\" " +
                        $"type={type} dist={dist:F1}m pos={candidate.transform.position:F1}");

                // Two-pass priority for wood mode: ALWAYS prefer fallen logs
                // and stumps over standing trees. Only consider standing trees
                // if no fallen debris exists in the scan radius.
                if (isStandingTree)
                {
                    // Track best standing tree separately as fallback
                    if (dist < _bestTreeDist)
                    {
                        _bestTreeDist = dist;
                        _bestTree = candidate;
                    }
                    continue; // skip — check for logs/stumps first
                }

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            if (candidateCount > 5)
                Log($"  ...and {candidateCount - 5} more candidates");

            if (skippedTierCount > 0)
                Log($"  Skipped {skippedTierCount} targets requiring higher tool tier (best tier={toolTier})");

            // Type breakdown
            string modeName = mode == CompanionSetup.ModeGatherWood ? "Wood"
                            : mode == CompanionSetup.ModeGatherOre  ? "Ore" : "Stone";
            if (mode == CompanionSetup.ModeGatherWood)
                Log($"  Breakdown [{modeName}]: {treeBaseCount} TreeBase, {treeLogCount} TreeLog, {stumpCount} Stump " +
                    $"(total {candidateCount} unique targets)");
            else
                Log($"  Breakdown [{modeName}]: {rockCount} deposits (total {candidateCount} unique targets)");

            // Fallback: no fallen logs/stumps found — use closest standing tree
            if (best == null && _bestTree != null)
            {
                best = _bestTree;
                bestDist = _bestTreeDist;
                Log($"No logs/stumps found — falling back to standing tree");
            }

            if (best != null)
                Log($"Best target: \"{best.name}\" dist={bestDist:F1}m " +
                    $"(out of {candidateCount} total candidates)");

            return best;
        }

        private GameObject ScanForPickable()
        {
            int count = Physics.OverlapSphereNonAlloc(
                transform.position, ScanRadius, _scanBuffer);

            if (count > _scanBuffer.Length) count = _scanBuffer.Length;

            Log($"Forage scan: {count} colliders in {ScanRadius}m radius");

            _seenIds.Clear();
            GameObject best = null;
            float bestDist = float.MaxValue;
            int candidateCount = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                var pickable = col.GetComponentInParent<Pickable>();
                if (pickable == null) continue;

                // Config filter — skip items not in the ForageItems list
                if (!IsForageItemAllowed(pickable)) continue;

                var go = pickable.gameObject;
                if (!_seenIds.Add(go.GetInstanceID())) continue;

                // Skip already picked
                var nview = go.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null &&
                    nview.GetZDO().GetBool(s_pickedHash, false))
                    continue;

                // Skip blacklisted (unreachable) targets
                if (_blacklist.ContainsKey(go.GetInstanceID()))
                    continue;

                // StayHome leash: skip targets beyond 50m from home position
                if (_setup != null && _setup.GetStayHome() && _setup.HasHomePosition())
                {
                    float distFromHome = Vector3.Distance(
                        _setup.GetHomePosition(), go.transform.position);
                    if (distFromHome > 50f) continue;
                }

                // Player leash: skip targets beyond 40m from the player
                if (Player.m_localPlayer != null &&
                    (_setup == null || !_setup.GetStayHome()))
                {
                    float distFromPlayer = Vector3.Distance(
                        Player.m_localPlayer.transform.position,
                        go.transform.position);
                    if (distFromPlayer > 40f) continue;
                }

                candidateCount++;
                float dist = Vector3.Distance(transform.position, go.transform.position);

                if (candidateCount <= 5)
                    Log($"  Pickable #{candidateCount}: \"{go.name}\" dist={dist:F1}m " +
                        $"pos={go.transform.position:F1}");

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = go;
                }
            }

            if (candidateCount > 5)
                Log($"  ...and {candidateCount - 5} more pickables");

            Log($"  Forage breakdown: {candidateCount} pickable targets");

            if (best != null)
                Log($"Best pickable: \"{best.name}\" dist={bestDist:F1}m " +
                    $"(out of {candidateCount} total)");
            else if (candidateCount == 0)
                LogInfo($"Forage scan: no pickables found within {ScanRadius}m");

            return best;
        }

        private static bool IsForageItemAllowed(Pickable pickable)
        {
            RefreshForageFilter();
            if (_forageAll) return true;
            if (pickable.m_itemPrefab == null) return false;
            string prefabName = Utils.GetPrefabName(pickable.m_itemPrefab);
            return _forageFilter.Contains(prefabName);
        }

        private static void RefreshForageFilter()
        {
            string raw = ModConfig.ForageItems.Value;
            if (raw == _forageFilterRaw) return;
            _forageFilterRaw = raw;

            if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "*")
            {
                _forageAll = true;
                _forageFilter = null;
                return;
            }

            _forageAll = false;
            _forageFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts = raw.Split(',');
            for (int i = 0; i < parts.Length; i++)
            {
                string item = parts[i].Trim();
                if (item.Length > 0)
                    _forageFilter.Add(item);
            }
        }

        private static GameObject GetHarvestCandidateWithType(Collider col, int mode, out string type)
        {
            type = null;
            if (mode == CompanionSetup.ModeGatherWood)
            {
                var tree = col.GetComponentInParent<TreeBase>();
                if (tree != null) { type = "TreeBase"; return tree.gameObject; }

                var log = col.GetComponentInParent<TreeLog>();
                if (log != null) { type = "TreeLog"; return log.gameObject; }

                // Tree stumps — spawned by TreeBase.m_stubPrefab, always named "*_stub*"
                var dest = col.GetComponentInParent<Destructible>();
                if (dest != null && dest.m_damages.m_chop != HitData.DamageModifier.Immune
                    && dest.gameObject.name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    type = "Stump";
                    return dest.gameObject;
                }
            }
            else if (mode == CompanionSetup.ModeGatherOre)
            {
                // Ore mode — only MineRock that drops actual ore (not just stone).
                // MineRock5 (multi-node boulders) excluded — AI can't reliably
                // navigate between nodes without getting stuck.
                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null)
                {
                    bool stoneOnly = DropsOnlyStone(rock.m_dropItems);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Harvest] OreMode MineRock \"{rock.gameObject.name}\" stoneOnly={stoneOnly} → {(stoneOnly ? "SKIP" : "ACCEPT")}");
                    if (!stoneOnly) { type = "MineRock"; return rock.gameObject; }
                }

                // Destructible with DropOnDestroyed (e.g. muddy scrap piles)
                var oDest = col.GetComponentInParent<Destructible>();
                if (oDest != null
                    && oDest.m_damages.m_pickaxe != HitData.DamageModifier.Immune)
                {
                    var dropComp = oDest.GetComponent<DropOnDestroyed>();
                    if (dropComp != null && !DropsOnlyStone(dropComp.m_dropWhenDestroyed))
                    {
                        type = "Destructible";
                        return oDest.gameObject;
                    }
                }
            }
            else // Stone
            {
                // Stone mode — only MineRock that drops stone (not ore) + Destructible rocks.
                // Ore veins (copper, tin, etc.) are reserved for Ore mode.
                // MineRock5 (multi-node boulders) excluded — AI can't reliably
                // navigate between nodes without getting stuck.
                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null)
                {
                    bool stoneOnly = DropsOnlyStone(rock.m_dropItems);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Harvest] StoneMode MineRock \"{rock.gameObject.name}\" stoneOnly={stoneOnly} → {(stoneOnly ? "ACCEPT" : "SKIP")}");
                    if (stoneOnly) { type = "MineRock"; return rock.gameObject; }
                }

                // Destructible rocks: must respond to pickaxe AND not respond to chop.
                // Accept both Immune and Ignore for chop — some rocks use Ignore which
                // is functionally identical (0 damage) but failed the strict equality check.
                var dest = col.GetComponentInParent<Destructible>();
                if (dest != null
                    && dest.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                    && (dest.m_damages.m_chop == HitData.DamageModifier.Immune
                        || dest.m_damages.m_chop == HitData.DamageModifier.Ignore))
                {
                    type = "Destructible";
                    return dest.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns true if a DropTable only contains stone-type drops (Stone, Flint)
        /// and no actual ore. Used to distinguish regular rock deposits from ore veins.
        /// </summary>
        private static bool DropsOnlyStone(DropTable table)
        {
            if (table == null || table.m_drops.Count == 0)
                return true;
            foreach (var drop in table.m_drops)
            {
                if (drop.m_item == null) continue;
                string name = drop.m_item.name;
                if (name != "Stone" && name != "Flint")
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Returns the minimum tool tier required to damage a harvest target.
        /// Every destructible type (TreeBase, TreeLog, MineRock, MineRock5,
        /// Destructible) has an m_minToolTier field. Returns 0 if unknown.
        /// </summary>
        private static int GetMinToolTier(GameObject target)
        {
            if (target == null) return 0;

            var tree = target.GetComponent<TreeBase>();
            if (tree != null) return tree.m_minToolTier;

            var log = target.GetComponent<TreeLog>();
            if (log != null) return log.m_minToolTier;

            var rock5 = target.GetComponent<MineRock5>();
            if (rock5 != null) return rock5.m_minToolTier;

            var rock = target.GetComponent<MineRock>();
            if (rock != null) return rock.m_minToolTier;

            var dest = target.GetComponent<Destructible>();
            if (dest != null) return dest.m_minToolTier;

            return 0;
        }

        /// <summary>
        /// Returns the highest tool tier among all tools in the companion's
        /// inventory that match the given harvest mode (chop for wood,
        /// pickaxe for stone/ore).
        /// Returns -1 if no matching tools exist.
        /// </summary>
        private int GetBestToolTier(int mode)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return -1;

            int bestTier = -1;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                var dmg = item.GetDamage();

                bool match = false;
                if (mode == CompanionSetup.ModeGatherWood)
                    match = dmg.m_chop > 0f;
                else
                    match = dmg.m_pickaxe > 0f;

                if (match && item.m_shared.m_toolTier > bestTier)
                    bestTier = item.m_shared.m_toolTier;
            }
            return bestTier;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Tool management
        // ══════════════════════════════════════════════════════════════════════

        private ItemDrop.ItemData FindBestTool(int mode)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null)
            {
                LogWarn("FindBestTool — inventory is null!");
                return null;
            }

            ItemDrop.ItemData best = null;
            float bestDmg = 0f;
            int toolCount = 0;

            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                var dmg = item.GetDamage();

                bool match = false;
                float relevantDmg = 0f;

                if (mode == CompanionSetup.ModeGatherWood)
                {
                    if (dmg.m_chop > 0f)
                    {
                        match = true;
                        relevantDmg = dmg.m_chop;
                    }
                }
                else // Stone or Ore
                {
                    if (dmg.m_pickaxe > 0f)
                    {
                        match = true;
                        relevantDmg = dmg.m_pickaxe;
                    }
                }

                if (match)
                {
                    toolCount++;
                    Log($"  Tool candidate: \"{item.m_shared.m_name}\" " +
                        $"type={item.m_shared.m_itemType} " +
                        $"chop={dmg.m_chop:F0} pick={dmg.m_pickaxe:F0} " +
                        $"relevantDmg={relevantDmg:F0} " +
                        $"equipped={_humanoid.IsItemEquiped(item)}");

                    if (relevantDmg > bestDmg)
                    {
                        best = item;
                        bestDmg = relevantDmg;
                    }
                }
            }

            if (best != null)
                Log($"Best tool: \"{best.m_shared.m_name}\" dmg={bestDmg:F0} " +
                    $"(out of {toolCount} matching tools)");
            else
                LogWarn($"No matching tools found! " +
                    $"Mode={mode} need={(mode == CompanionSetup.ModeGatherWood ? "m_chop > 0" : "m_pickaxe > 0")} " +
                    $"totalItems={inv.GetAllItems().Count}");

            return best;
        }

        private void EquipTool(ItemDrop.ItemData tool)
        {
            if (tool == null || _humanoid == null) return;

            // Suppress auto-equip so CompanionSetup doesn't re-equip combat gear
            if (_setup != null) _setup.SuppressAutoEquip = true;

            var curRight = ReflectionHelper.GetRightItem(_humanoid);
            var curLeft  = ReflectionHelper.GetLeftItem(_humanoid);

            // Already equipped — check by both reference AND IsItemEquiped
            bool sameRef = curRight == tool;
            bool alreadyEquipped = _humanoid.IsItemEquiped(tool);

            if (sameRef || alreadyEquipped)
            {
                Log($"EquipTool: \"{tool.m_shared.m_name}\" already equipped " +
                    $"(sameRef={sameRef} isEquipped={alreadyEquipped}) — skipping");
                _toolEquipped = true;
                return;
            }

            // Check if same-name item is already in hand (different reference, same item type)
            bool sameNameInHand = curRight != null &&
                curRight.m_shared?.m_name == tool.m_shared?.m_name;

            Log($"EquipTool: equipping \"{tool.m_shared.m_name}\" " +
                $"(right=\"{curRight?.m_shared?.m_name ?? "none"}\" " +
                $"left=\"{curLeft?.m_shared?.m_name ?? "none"}\") " +
                $"sameNameInHand={sameNameInHand} " +
                $"rightHash={curRight?.GetHashCode() ?? 0} toolHash={tool.GetHashCode()} " +
                $"SuppressAutoEquip=true");

            // If the same-named item is in hand, only unequip left (avoid the unequip/re-equip bug)
            if (sameNameInHand)
            {
                if (curLeft != null) _humanoid.UnequipItem(curLeft, false);
                // The right hand already has the right type of tool — use it directly
                _toolEquipped = true;
                var afterRight = ReflectionHelper.GetRightItem(_humanoid);
                Log($"After equip (same-name shortcut): rightItem=\"{afterRight?.m_shared?.m_name ?? "NONE"}\" " +
                    $"attackRange={GetAttackRange():F1}");
                return;
            }

            if (curRight != null) _humanoid.UnequipItem(curRight, false);
            if (curLeft  != null) _humanoid.UnequipItem(curLeft, false);

            // Equip the harvest tool
            _humanoid.EquipItem(tool, false);

            // Verify it actually equipped — EquipItem silently fails during attack animations
            var verifyRight = ReflectionHelper.GetRightItem(_humanoid);
            bool success = verifyRight != null;
            if (success)
            {
                _toolEquipped = true;
                _pendingToolReequip = null;
                Log($"After equip: rightItem=\"{verifyRight.m_shared?.m_name ?? "?"}\" " +
                    $"attackRange={GetAttackRange():F1}");
            }
            else
            {
                _toolEquipped = false;
                _pendingToolReequip = tool;
                LogWarn($"After equip: rightItem=\"NONE\" — EQUIP FAILED (will retry). " +
                    $"Tried to equip \"{tool.m_shared.m_name}\" type={tool.m_shared.m_itemType} " +
                    $"inAttack={(_humanoid.InAttack() ? "true" : "false")}");
            }
        }

        private void RestoreLoadout()
        {
            if (!_toolEquipped) return;
            _toolEquipped = false;

            Log("RestoreLoadout — SuppressAutoEquip=false, re-equipping best gear");

            if (_setup != null)
            {
                _setup.SuppressAutoEquip = false;
                _setup.SyncEquipmentToInventory();
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private bool IsCompanionUIOpen()
        {
            return CompanionInteractPanel.IsOpenFor(_setup)
                || CompanionRadialMenu.IsOpenFor(_setup)
                || HomeZonePanel.IsOpenFor(_setup);
        }

        private float GetCurrentWeight()
        {
            var inv = _humanoid?.GetInventory();
            return inv != null ? inv.GetTotalWeight() : 0f;
        }

        /// <summary>True when companion weight is at or above the overweight threshold.</summary>
        public bool IsOverweight()
        {
            return GetCurrentWeight() >= OverweightThreshold;
        }

        /// <summary>
        /// Find nearest Container (chest) within 50m that has free slots.
        /// Skips companions, characters, in-use chests, and full inventories.
        /// Result is cached for 5s to avoid per-frame FindObjectsByType scans.
        /// </summary>
        private Container FindNearestChest()
        {
            if (_chestScanTimer > 0f)
                return _cachedChest;
            _chestScanTimer = 5f;

            Container best = null;
            float bestDist = float.MaxValue;
            int scanned = 0, skipped = 0;
            foreach (var c in UnityEngine.Object.FindObjectsByType<Container>(FindObjectsSortMode.None))
            {
                if (c == null) continue;
                scanned++;
                // Distance check first — cheapest filter, skips most objects
                float dist = Vector3.Distance(transform.position, c.transform.position);
                if (dist > 50f) continue;
                if (c.GetComponent<CompanionSetup>() != null) { skipped++; continue; }
                if (c.GetComponent<Character>() != null) { skipped++; continue; }
                if (c.IsInUse()) { skipped++; continue; }
                var nv = c.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) { skipped++; continue; }
                var inv = c.GetInventory();
                if (inv == null) continue;
                int items = inv.GetAllItems().Count;
                int capacity = inv.GetWidth() * inv.GetHeight();
                if (items >= capacity) continue;
                if (dist < bestDist) { bestDist = dist; best = c; }
            }
            if (best != null)
                Log($"FindNearestChest — found \"{best.m_name}\" at {bestDist:F1}m " +
                    $"(scanned={scanned} skipped={skipped})");
            else
                Log($"FindNearestChest — NONE found (scanned={scanned} skipped={skipped})");
            _cachedChest = best;
            return best;
        }

        private int GetMode()
        {
            // Directed harvest overrides the ZDO mode
            if (_isDirectedHarvest)
                return _directedMode;

            var zdo = _nview?.GetZDO();
            return zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
        }

        private float GetAttackRange()
        {
            var weapon = ReflectionHelper.GetRightItem(_humanoid);
            if (weapon?.m_shared?.m_attack != null)
                return weapon.m_shared.m_attack.m_attackRange;
            return 2f;
        }

        /// <summary>True for targets lying on the ground (logs, stumps) where horizontal swings whiff.</summary>
        private bool IsLowTarget()
        {
            if (_targetType == "TreeLog" || _targetType == "Stump")
                return true;
            // TreeBase on sloped terrain often sits below the companion —
            // treat it as low when there's a significant height difference
            if (_targetType == "TreeBase" && _target != null)
            {
                float heightDiff = _target.transform.position.y - transform.position.y;
                if (heightDiff < -0.3f)
                    return true;
            }
            return false;
        }

        /// <summary>Determine the resource type of the target for attack strategy.</summary>
        private static string ClassifyTargetType(GameObject target, int mode)
        {
            if (target == null) return null;
            if (mode == CompanionSetup.ModeGatherWood)
            {
                if (target.GetComponent<TreeBase>() != null) return "TreeBase";
                if (target.GetComponent<TreeLog>() != null) return "TreeLog";
                var dest = target.GetComponent<Destructible>();
                if (dest != null && target.name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "Stump";
            }
            if (target.GetComponent<MineRock5>() != null) return "MineRock5";
            if (target.GetComponent<MineRock>() != null) return "MineRock";
            if (target.GetComponent<Destructible>() != null) return "Destructible";
            return "Unknown";
        }

        private bool IsRockLikeTarget()
        {
            return _targetType == "MineRock5" || _targetType == "MineRock" || _targetType == "Destructible";
        }

        private void FaceTarget()
        {
            if (_target == null || _ai == null || _character == null) return;
            Vector3 facePoint;
            if (_state == State_Attacking && IsRockLikeTarget())
                facePoint = _attackFaceLocked ? _lockedAttackFaceTarget : GetRockFacePosition();
            else
                facePoint = GetEffectiveTargetPosition();

            // For large rocks, the closest surface point can be almost directly beside
            // the companion — BaseAI.LookAt bails out when XZ distance < 0.01.
            // Fall back to the rock's center position to guarantee a valid facing direction.
            Vector3 toFace = facePoint - transform.position;
            toFace.y = 0f;
            if (toFace.sqrMagnitude < 0.1f)
                facePoint = _target.transform.position;

            // Set look direction through the AI system so it isn't overridden
            _ai.LookAtPoint(facePoint);

            // Also snap body rotation instantly during Attacking state —
            // Character.RotateTowards is smooth and too slow for pickaxe timing.
            if (_state == State_Attacking)
            {
                Vector3 dir = facePoint - transform.position;
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.001f)
                {
                    Quaternion targetRot = Quaternion.LookRotation(dir.normalized);
                    _character.SetLookDir(dir.normalized);
                    transform.rotation = targetRot;
                }
            }
        }

        /// <summary>
        /// Returns the center position of the nearest rock node/collider.
        /// Used for attack facing — aims at the rock face (elevated) instead of
        /// the ground-level ClosestPoint, which causes pickaxes to hit the terrain.
        /// </summary>
        private Vector3 GetRockFacePosition()
        {
            if (_target == null) return transform.position;

            if (_targetType == "MineRock5")
            {
                float bestDist = float.MaxValue;
                Vector3 bestPos = _target.transform.position;
                bool found = false;
                foreach (Transform child in _target.transform)
                {
                    if (!child.gameObject.activeSelf) continue;
                    var col = child.GetComponent<Collider>();
                    if (col == null) continue;
                    // Use ClosestPoint for distance ranking but return collider center
                    Vector3 surfacePoint = col.ClosestPoint(transform.position);
                    float dist = Vector3.Distance(transform.position, surfacePoint);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestPos = col.bounds.center;
                        found = true;
                    }
                }
                if (found) return bestPos;
            }

            // MineRock / Destructible — use collider bounds center (elevated)
            if (_targetType == "MineRock" || _targetType == "Destructible")
            {
                var col = _target.GetComponent<Collider>();
                if (col != null)
                    return col.bounds.center;
            }

            return _target.transform.position;
        }

        /// <summary>
        /// Returns the nearest reachable point on the target's surface.
        /// Uses Collider.ClosestPoint so the companion pathfinds to the rock's
        /// face instead of its center (which is inside the geometry, causing the
        /// companion to run around/over the rock trying to reach an unreachable point).
        /// </summary>
        private Vector3 GetEffectiveTargetPosition()
        {
            if (_target == null) return transform.position;

            if (_targetType == "MineRock5")
            {
                var rock5 = _target.GetComponent<MineRock5>();
                if (rock5 != null)
                {
                    float bestDist = float.MaxValue;
                    Vector3 bestPos = _target.transform.position;
                    bool found = false;
                    foreach (Transform child in _target.transform)
                    {
                        if (!child.gameObject.activeSelf) continue;
                        var col = child.GetComponent<Collider>();
                        if (col == null) continue;
                        Vector3 surfacePoint = col.ClosestPoint(transform.position);
                        float dist = Vector3.Distance(transform.position, surfacePoint);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestPos = surfacePoint;
                            found = true;
                        }
                    }
                    if (found) return bestPos;
                }
            }

            // For MineRock and Destructible, use collider surface point
            if (_targetType == "MineRock" || _targetType == "Destructible")
            {
                var col = _target.GetComponent<Collider>();
                if (col != null)
                    return col.ClosestPoint(transform.position);
            }

            return _target.transform.position;
        }

        private static bool IsTargetValid(GameObject target)
        {
            // When fully destroyed, Unity destroys the GO → null check catches it
            return target != null;
        }

        private void LogInventoryContents()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null)
            {
                LogWarn("Cannot log inventory — null");
                return;
            }
            var items = inv.GetAllItems();
            Log($"Inventory ({items.Count} items):");
            foreach (var item in items)
            {
                if (item?.m_shared == null) continue;
                var dmg = item.GetDamage();
                Log($"  \"{item.m_shared.m_name}\" type={item.m_shared.m_itemType} " +
                    $"chop={dmg.m_chop:F0} pick={dmg.m_pickaxe:F0} " +
                    $"slash={dmg.m_slash:F0} pierce={dmg.m_pierce:F0} " +
                    $"equipped={_humanoid.IsItemEquiped(item)}");
            }
        }

        // ── Target claim helpers ────────────────────────────────────────────

        /// <summary>
        /// Claims _target so other companions skip it during their scan.
        /// Releases any previously held claim first.
        /// </summary>
        private void ClaimTarget()
        {
            UnclaimTarget();
            _claimedTargetId = _target?.GetInstanceID() ?? 0;
            if (_claimedTargetId != 0) s_claimedTargets.Add(_claimedTargetId);
        }

        private void UnclaimTarget()
        {
            if (_claimedTargetId != 0)
            {
                s_claimedTargets.Remove(_claimedTargetId);
                _claimedTargetId = 0;
            }
        }

        /// <summary>
        /// Scans for a recently spawned TreeLog near the given position (tree just fell).
        /// Uses the shared _scanBuffer to avoid allocation.
        /// </summary>
        private GameObject TryFindNearbyLog(Vector3 center)
        {
            int count = Physics.OverlapSphereNonAlloc(center, 12f, _scanBuffer);
            for (int i = 0; i < count; i++)
            {
                if (_scanBuffer[i] == null) continue;
                var go = _scanBuffer[i].gameObject;
                if (go.GetComponent<TreeLog>() == null) continue;
                if (_blacklist.ContainsKey(go.GetInstanceID())) continue;
                return go;
            }
            return null;
        }

        // ── Logging helpers with per-companion tag ─────────────────────────

        private void Log(string msg) =>
            CompanionsPlugin.Log.LogDebug($"{_tag} {msg}");

        private void LogInfo(string msg) =>
            CompanionsPlugin.Log.LogInfo($"{_tag} {msg}");

        private void LogWarn(string msg) =>
            CompanionsPlugin.Log.LogWarning($"{_tag} {msg}");
    }
}
