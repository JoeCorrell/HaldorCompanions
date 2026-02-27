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
        private ZNetView       _nview;
        private CompanionAI    _ai;
        private Humanoid       _humanoid;
        private Character      _character;
        private CompanionSetup _setup;
        private DoorHandler    _doorHandler;

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
        private float        _selfDefenseLogTimer;
        private bool         _wasInSelfDefense;  // true last frame when combat target was present

        // ── Directed harvest (one-shot from hotkey, works outside gather mode) ─
        private GameObject   _directedTarget;
        private int          _directedMode;       // ModeGatherWood or ModeGatherStone
        private bool         _isDirectedHarvest;

        // ── Blacklist — tracks unreachable targets to prevent infinite stuck loops ──
        private readonly Dictionary<int, float> _blacklist = new Dictionary<int, float>();
        private const float BlacklistDuration = 60f;

        private const HarvestState State_Idle           = HarvestState.Idle;
        private const HarvestState State_Moving         = HarvestState.Moving;
        private const HarvestState State_Attacking      = HarvestState.Attacking;
        private const HarvestState State_CollectingDrops = HarvestState.CollectingDrops;

        // ── Config ──────────────────────────────────────────────────────────
        private const float ScanInterval   = 4f;
        private const float ScanRadius     = 30f;
        private const float AttackInterval = 2.5f;
        private const float AttackRetry    = 0.25f;
        private const float MoveTimeout    = 12f;
        private const float ArrivalSlack   = 0.5f;
        private const int   WhiffRetryMax  = 20;   // abandon after this many SUCCESSFUL swings without destroy
        private const int   TotalAttemptMax = 30;  // abandon after this many total attempts (incl. failures)

        // Drop collection config
        private const float DropScanRadius  = 8f;   // radius around destroy pos to look for drops
        private const float DropPickupRange = 3.0f;  // must be >= BaseAI.Follow() stop distance (~3m)
        private const float DropTimeout     = 10f;   // max time in CollectingDrops before giving up
        private const float DropScanDelay   = 0.5f;  // brief delay after destroy before scanning (drops need time to spawn)

        // Weight limit — stop gathering when near max carry weight
        private const float OverweightThreshold = 298f;
        private float _overweightMsgTimer;

        // ── Per-instance scan buffer (thread-safe with multiple companions) ─
        private readonly Collider[]  _scanBuffer = new Collider[1024];
        private readonly Collider[]  _dropBuffer = new Collider[128];
        private readonly HashSet<int> _seenIds   = new HashSet<int>();

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

            // Build a per-companion tag: "[Harvest#1234]" using instance ID
            int id = GetInstanceID();
            _tag = $"[Harvest#{id & 0xFFFF}]";

            // Layer mask for item drops — same as Player.m_autoPickupMask
            _itemLayerMask = LayerMask.GetMask("item");

            Log($"Awake — nview={_nview != null} ai={_ai != null} " +
                $"humanoid={_humanoid != null} character={_character != null} " +
                $"setup={_setup != null} instanceId={id}");
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;

            // Update tag with companion name if available (may be set after Awake)
            if (_character != null && !_tag.Contains("|"))
            {
                string name = _character.m_name;
                if (!string.IsNullOrEmpty(name) && name != "HC_Companion")
                    _tag = $"[Harvest#{GetInstanceID() & 0xFFFF}|{name}]";
            }

            int mode = GetMode();
            bool gatherMode = mode >= CompanionSetup.ModeGatherWood
                           && mode <= CompanionSetup.ModeGatherOre;

            if (!gatherMode && !_isDirectedHarvest)
            {
                if (IsInGatherMode) ExitGatherMode();
                return;
            }

            if (gatherMode && !IsInGatherMode) EnterGatherMode();

            // Weight check — stop gathering if overweight
            if (IsOverweight())
            {
                var talk = GetComponent<CompanionTalk>();
                if (talk != null && _overweightMsgTimer <= 0f)
                {
                    talk.Say("My back is hurting from all this weight!");
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

            // Check for nearby enemies — if any are close, pause gathering and
            // let CombatController handle self-defense. Must match TargetPatches
            // SelfDefenseRange so the two systems agree.
            if (_ai != null && _character != null)
            {
                var creature = _ai.m_targetCreature;

                // Detect dead enemies that IsDead() hasn't caught yet (hp=0, m_dead not set)
                if (creature != null && !creature.IsDead() && creature.GetHealth() <= 0f)
                {
                    Log($"DEAD ENEMY detected by health guard — \"{creature.m_name}\" " +
                        $"isDead=False hp={creature.GetHealth():F0} — clearing target");
                    _ai.ClearTargets();
                    creature = null; // fall through to resume harvest
                }

                if (creature != null && !creature.IsDead() && creature.GetHealth() > 0f)
                {
                    // CompanionAI has a target — let combat handle it, pause harvest
                    float enemyDist = Vector3.Distance(transform.position, creature.transform.position);
                    if (enemyDist < 10f)
                    {
                        // Throttle this log — fires every frame during combat
                        _selfDefenseLogTimer -= Time.deltaTime;
                        if (_selfDefenseLogTimer <= 0f)
                        {
                            _selfDefenseLogTimer = 3f;
                            Log($"SELF-DEFENSE — pausing harvest state={_state} enemy \"{creature.m_name}\" " +
                                $"at {enemyDist:F1}m — CombatController will engage");
                        }
                        _wasInSelfDefense = true;
                        // Don't clear the target! Let combat run.
                        return;
                    }
                    else
                    {
                        // Enemy is far away — clear and continue gathering
                        Log($"Clearing distant combat target \"{creature.m_name}\" " +
                            $"dist={enemyDist:F1}m — too far for self-defense");
                        _ai.ClearTargets();
                    }
                }

                // Log when transitioning back from self-defense to harvest
                if (_wasInSelfDefense)
                {
                    _wasInSelfDefense = false;

                    // Clear alert state — m_alerted persists after enemies die and causes
                    // CompanionAI.UpdateAI to override movement with alert-scanning behavior,
                    // preventing the companion from reaching harvest targets.
                    _ai.SetAlerted(false);

                    var rightItem = ReflectionHelper.GetRightItem(_humanoid);
                    Log($"SELF-DEFENSE ended — resuming harvest state={_state} " +
                        $"tool=\"{rightItem?.m_shared?.m_name ?? "NONE"}\" " +
                        $"toolEquipped={_toolEquipped} suppress={_setup?.SuppressAutoEquip ?? false}");
                }

                // Also clear static targets that might have been set
                var staticT = _ai.m_targetStatic;
                if (staticT != null)
                {
                    Log($"Clearing static combat target \"{staticT.name}\"");
                    _ai.m_targetStatic = null;
                }
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

        /// <summary>Called by UI when action mode changes.</summary>
        public void NotifyActionModeChanged()
        {
            int mode = GetMode();
            Log($"NotifyActionModeChanged — new mode={mode} " +
                $"(wasGather={IsInGatherMode}, state={_state})");
            CancelDirectedTarget();
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

            _directedTarget = target;
            _directedMode = harvestMode;
            _isDirectedHarvest = true;

            // Force into Moving state toward this target
            _target = target;
            _targetType = ClassifyTargetType(target, harvestMode);
            _state = State_Moving;
            _moveTimer = 0f;
            _swingCount = 0;
            _totalAttempts = 0;
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

            if (target.GetComponent<MineRock5>() != null) return CompanionSetup.ModeGatherStone;
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
                            : "Unknown";
            IsInGatherMode = true;
            _scanTimer = 0f; // scan immediately on mode entry
            _heartbeatTimer = 0f; // heartbeat immediately

            var followTarget = _ai?.GetFollowTarget();
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

            // Restore follow target
            if (_ai != null && Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                Log("Restored follow target to player");
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
            _scanTimer     = 0f;

            // If this was a directed harvest, clean up and restore loadout
            if (_isDirectedHarvest)
            {
                _isDirectedHarvest = false;
                _directedTarget = null;
                RestoreLoadout();
                Log($"ResetToIdle (directed harvest complete) — restored loadout");
            }

            // Restore follow target so companion follows player between targets
            if (_ai != null && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);

            if (prevState != State_Idle)
                Log($"ResetToIdle (was {prevState}) — follow target restored to player");
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
            _state = State_Moving;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Moving — BaseAI.Follow() drives pathfinding, we reinforce + check arrival
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
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

            float dt = Time.deltaTime;
            _moveTimer += dt;

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

            float range = GetAttackRange();
            Vector3 targetPos = _target.transform.position;
            float distToTarget = Vector3.Distance(transform.position, targetPos);

            // Use MoveToPoint (pathfinding) for all distances. This handles
            // slopes and terrain obstacles that MoveTowards can't climb.
            // CompanionAI.UpdateAI skips Follow() when harvest is active,
            // so there's no dual-control jitter.
            float moveGoal = IsLowTarget() ? range * 0.3f : range * 0.5f;
            bool moveResult = _ai.MoveToPoint(dt, targetPos, moveGoal, true);

            // Pathfinding failed at close range — fall back to direct push.
            // This handles cases where the navmesh has gaps near the target.
            if (!moveResult && distToTarget < 4f)
            {
                Vector3 dir = (targetPos - transform.position);
                dir.y = 0f;
                if (dir.sqrMagnitude > 0.01f)
                    _ai.MoveTowards(dir.normalized, true);
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
                        _ai.MoveTowards(dir.normalized, true);
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
                bool inAttack = _humanoid != null && _humanoid.InAttack();

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
            float arrivalDist = isLow ? range : range + ArrivalSlack;
            if (distToTarget <= arrivalDist || horizDist <= arrivalDist)
            {
                if (_ai != null) _ai.StopMoving();
                FaceTarget();
                _attackTimer = 0.3f; // brief pause before first swing
                _state = State_Attacking;

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
                _character.SetRun(true);

            // During attack, shuffle closer if beyond actual hit range.
            // Follow() stops at ~3m but weapon range is ~2.2m — the gap means
            // small targets get missed. LateUpdate overrides Follow's StopMoving.
            // After 2+ swings (likely whiffing), get much more aggressive about closing distance.
            if (_state == State_Attacking && _target != null && _ai != null)
            {
                float dist = Vector3.Distance(transform.position, _target.transform.position);
                float range = GetAttackRange();
                bool needsCloser = _swingCount >= 2 || IsLowTarget();
                float shuffleThreshold = needsCloser ? range * 0.5f : range * 0.8f;
                float shuffleGoal     = needsCloser ? range * 0.2f : range * 0.5f;
                if (dist > shuffleThreshold)
                    _ai.MoveToPoint( Time.deltaTime, _target.transform.position, shuffleGoal, false);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Attacking — swing at resource
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            if (!IsTargetValid(_target))
            {
                Log($"Target DESTROYED after {_swingCount} hits, {_totalAttempts} attempts " +
                    $"— entering drop collection");
                _lastDestroyPos = transform.position;
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

            // Check distance — may have drifted
            float dist = Vector3.Distance(transform.position, _target.transform.position);
            float range = GetAttackRange();
            if (dist > range + 1.5f)
            {
                Log($"Drifted too far from target: dist={dist:F1}m " +
                    $"(range={range:F1} + 1.5) — returning to Moving state");
                _moveTimer = 0f;
                _moveLogTimer = 0f;
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
                // Re-read weapon after equip
                weapon = ReflectionHelper.GetRightItem(_humanoid);
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

            // Use power attack (secondary, downward swing) for low targets like
            // fallen logs and stumps — horizontal swing often passes over them.
            // Also escalate to power attack after 3+ whiffs on any target.
            // CRITICAL: Only use power attack if the weapon actually HAS a secondary attack!
            // Without this check, weapons without secondary (e.g. bronze pickaxe) fail forever.
            bool wantPower = IsLowTarget() || _swingCount >= 3;
            bool usePowerAttack = wantPower && weapon.HaveSecondaryAttack();

            bool attacked = _humanoid != null && _humanoid.StartAttack(null, usePowerAttack);
            _totalAttempts++;
            _attackTimer = attacked ? AttackInterval : 0.5f; // failed retry at 0.5s (not 0.25s spam)
            if (attacked) _swingCount++;

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
                    LogWarn($"Attack FAILED — attempts={_totalAttempts}/{TotalAttemptMax} " +
                        $"animState=[{animState}] power={usePowerAttack} " +
                        $"weapon=\"{weapon?.m_shared?.m_name ?? "NONE"}\"");
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

            // Restore follow target to player
            if (_ai != null && Player.m_localPlayer != null)
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);

            Log($"ResetToIdle (was CollectingDrops) — collected {_dropsPickedUp} items, follow target restored to player");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Resource scanning
        // ══════════════════════════════════════════════════════════════════════

        private GameObject ScanForTarget(int mode)
        {
            int count = Physics.OverlapSphereNonAlloc(
                transform.position, ScanRadius, _scanBuffer);

            bool bufferFull = count >= _scanBuffer.Length;
            if (count > _scanBuffer.Length) count = _scanBuffer.Length;

            Log($"Physics scan: {count} colliders in {ScanRadius}m radius" +
                (bufferFull ? " ** BUFFER FULL — may miss targets! **" : ""));

            _seenIds.Clear();
            GameObject best = null;
            float bestDist = float.MaxValue;
            int candidateCount = 0;
            int treeBaseCount = 0, treeLogCount = 0, stumpCount = 0, rockCount = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                string type;
                GameObject candidate = GetHarvestCandidateWithType(col, mode, out type);
                if (candidate == null) continue;
                if (!_seenIds.Add(candidate.GetInstanceID())) continue;

                // Skip blacklisted (unreachable) targets
                if (_blacklist.ContainsKey(candidate.GetInstanceID()))
                {
                    // Log first blacklist skip per scan (avoid spam)
                    if (candidateCount == 0)
                        Log($"  Skipping blacklisted \"{candidate.name}\" (id={candidate.GetInstanceID()})");
                    continue;
                }

                candidateCount++;
                float dist = Vector3.Distance(transform.position, candidate.transform.position);

                // Count by type
                if (type == "TreeBase") treeBaseCount++;
                else if (type == "TreeLog") treeLogCount++;
                else if (type == "Stump") stumpCount++;
                else rockCount++;

                // Log first 5 candidates and any TreeLogs (always interesting)
                if (candidateCount <= 5 || type == "TreeLog")
                    Log($"  Candidate #{candidateCount}: \"{candidate.name}\" " +
                        $"type={type} dist={dist:F1}m pos={candidate.transform.position:F1}");

                // Prioritize fallen logs and stumps over standing trees —
                // standing trees create more logs/stumps, so clean up first.
                float score = dist;
                if (type == "TreeBase") score *= 3f;

                if (score < bestDist)
                {
                    bestDist = score;
                    best = candidate;
                }
            }

            if (candidateCount > 5)
                Log($"  ...and {candidateCount - 5} more candidates");

            // Type breakdown
            string modeName = mode == CompanionSetup.ModeGatherWood ? "Wood"
                            : mode == CompanionSetup.ModeGatherOre  ? "Ore" : "Stone";
            if (mode == CompanionSetup.ModeGatherWood)
                Log($"  Breakdown [{modeName}]: {treeBaseCount} TreeBase, {treeLogCount} TreeLog, {stumpCount} Stump " +
                    $"(total {candidateCount} unique targets)");
            else
                Log($"  Breakdown [{modeName}]: {rockCount} deposits (total {candidateCount} unique targets)");

            if (best != null)
                Log($"Best target: \"{best.name}\" dist={bestDist:F1}m " +
                    $"(out of {candidateCount} total candidates)");

            return best;
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
                // Ore mode — only MineRock/MineRock5 that drop actual ore (not just stone)
                var rock5 = col.GetComponentInParent<MineRock5>();
                if (rock5 != null && !DropsOnlyStone(rock5.m_dropItems))
                { type = "MineRock5"; return rock5.gameObject; }

                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null && !DropsOnlyStone(rock.m_dropItems))
                { type = "MineRock"; return rock.gameObject; }

                // Skip Destructible — ore deposits use MineRock/MineRock5
            }
            else // Stone
            {
                // Stone mode — Destructible rocks + any MineRock/MineRock5 that only drop stone
                var rock5 = col.GetComponentInParent<MineRock5>();
                if (rock5 != null && DropsOnlyStone(rock5.m_dropItems))
                { type = "MineRock5"; return rock5.gameObject; }

                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null && DropsOnlyStone(rock.m_dropItems))
                { type = "MineRock"; return rock.gameObject; }

                // Destructible rocks: must respond to pickaxe AND be immune to chop
                // (wood-type destructibles like Beech_small2, stumps respond to chop — exclude them)
                var dest = col.GetComponentInParent<Destructible>();
                if (dest != null
                    && dest.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                    && dest.m_damages.m_chop == HitData.DamageModifier.Immune)
                {
                    type = "Destructible";
                    return dest.gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns true if a DropTable only contains Stone drops (no ore).
        /// Used to distinguish regular rock deposits from ore veins.
        /// </summary>
        private static bool DropsOnlyStone(DropTable table)
        {
            if (table == null || table.m_drops.Count == 0) return true;
            foreach (var drop in table.m_drops)
            {
                if (drop.m_item == null) continue;
                if (drop.m_item.name != "Stone") return false;
            }
            return true;
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
            _toolEquipped = true;

            // Verify it actually equipped
            var verifyRight = ReflectionHelper.GetRightItem(_humanoid);
            bool success = verifyRight != null;
            if (success)
                Log($"After equip: rightItem=\"{verifyRight.m_shared?.m_name ?? "?"}\" " +
                    $"attackRange={GetAttackRange():F1}");
            else
                LogWarn($"After equip: rightItem=\"NONE\" — EQUIP FAILED! " +
                    $"Tried to equip \"{tool.m_shared.m_name}\" type={tool.m_shared.m_itemType} " +
                    $"gridPos=({tool.m_gridPos.x},{tool.m_gridPos.y})");
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
            var panel = CompanionInteractPanel.Instance;
            return panel != null && panel.IsVisible && panel.CurrentCompanion == _setup;
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
            return _targetType == "TreeLog" || _targetType == "Stump";
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

        private void FaceTarget()
        {
            if (_target == null) return;
            Vector3 dir = _target.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                transform.rotation = Quaternion.LookRotation(dir);
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

        // ── Logging helpers with per-companion tag ─────────────────────────

        private void Log(string msg) =>
            CompanionsPlugin.Log.LogInfo($"{_tag} {msg}");

        private void LogWarn(string msg) =>
            CompanionsPlugin.Log.LogWarning($"{_tag} {msg}");
    }
}
