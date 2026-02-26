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
        private enum HarvestState { Idle, Moving, Attacking }

        // ── Components ──────────────────────────────────────────────────────
        private ZNetView       _nview;
        private MonsterAI      _ai;
        private Humanoid       _humanoid;
        private Character      _character;
        private CompanionSetup _setup;

        // ── State ───────────────────────────────────────────────────────────
        private HarvestState _state = State_Idle;
        private GameObject   _target;
        private float        _scanTimer;
        private float        _attackTimer;
        private float        _moveTimer;
        private bool         _toolEquipped;

        private const HarvestState State_Idle      = HarvestState.Idle;
        private const HarvestState State_Moving    = HarvestState.Moving;
        private const HarvestState State_Attacking = HarvestState.Attacking;

        // ── Config ──────────────────────────────────────────────────────────
        private const float ScanInterval   = 4f;
        private const float ScanRadius     = 30f;
        private const float AttackInterval = 2.5f;
        private const float AttackRetry    = 0.25f;
        private const float MoveTimeout    = 12f;
        private const float ArrivalSlack   = 0.5f;

        // ── Per-instance scan buffer (thread-safe with multiple companions) ─
        private readonly Collider[]  _scanBuffer = new Collider[1024];
        private readonly HashSet<int> _seenIds   = new HashSet<int>();

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

        /// <summary>Current harvest state name for external diagnostics.</summary>
        internal string CurrentStateName => _state.ToString();

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _ai        = GetComponent<MonsterAI>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup     = GetComponent<CompanionSetup>();

            // Build a per-companion tag: "[Harvest#1234]" using instance ID
            int id = GetInstanceID();
            _tag = $"[Harvest#{id & 0xFFFF}]";

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

            if (!gatherMode)
            {
                if (IsInGatherMode) ExitGatherMode();
                return;
            }

            if (!IsInGatherMode) EnterGatherMode();

            // Heartbeat — full state dump every 5s
            _heartbeatTimer -= Time.deltaTime;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = HeartbeatInterval;
                LogHeartbeat();
            }

            // Clear combat targets every frame — TargetPatches.Prefix handles
            // suppressing UpdateTarget, but clear here as a safety net.
            if (_ai != null)
            {
                var creature = ReflectionHelper.GetTargetCreature(_ai);
                var staticT  = ReflectionHelper.GetTargetStatic(_ai);
                if (creature != null || staticT != null)
                {
                    Log($"Clearing combat targets: creature=\"{creature?.m_name ?? ""}\" " +
                        $"static=\"{staticT?.name ?? ""}\"");
                    ReflectionHelper.ClearAllTargets(_ai);
                }
            }

            switch (_state)
            {
                case State_Idle:      UpdateIdle(mode);     break;
                case State_Moving:    UpdateMoving();       break;
                case State_Attacking: UpdateAttacking();    break;
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
            var creature  = _ai != null ? ReflectionHelper.GetTargetCreature(_ai) : null;

            bool inAttack = _character is Humanoid h ? h.InAttack() : false;

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
                $"combatTarget=\"{creature?.m_name ?? "null"}\"");
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
            ResetToIdle();
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
            _state     = State_Idle;
            _target    = null;
            _scanTimer = 0f;

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
            EquipTool(tool);

            // Set follow target to the harvest resource — MonsterAI.Follow() will
            // drive movement naturally using pathfinding + correct speed.
            // Previously we set follow=null, but that caused MonsterAI to idle
            // and call StopMoving() every frame, fighting our MoveTo calls.
            if (_ai != null)
            {
                _ai.SetFollowTarget(_target);
                Log($"Set follow target to resource \"{_target.name}\" — " +
                    "MonsterAI.Follow() will drive movement");
            }

            _moveTimer = 0f;
            _moveLogTimer = 0f;
            _state = State_Moving;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Moving — MonsterAI.Follow() drives pathfinding, we reinforce + check arrival
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateMoving()
        {
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
                ResetToIdle();
                return;
            }

            float range = GetAttackRange();
            Vector3 targetPos = _target.transform.position;

            // MonsterAI.Follow() handles primary movement (pathfinding, speed).
            // We call MoveTo as reinforcement in case Follow stops early (< 3m).
            bool moveResult = ReflectionHelper.TryMoveTo(_ai, dt, targetPos, range, true);

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

            // Check if we've arrived (distance within attack range + slack)
            float distToTarget = Vector3.Distance(transform.position, targetPos);
            if (distToTarget <= range + ArrivalSlack)
            {
                if (_ai != null) _ai.StopMoving();
                FaceTarget();
                _attackTimer = 0.3f; // brief pause before first swing
                _state = State_Attacking;

                Log($"ARRIVED at \"{_target.name}\" dist={distToTarget:F1}m " +
                    $"(range={range:F1} + slack={ArrivalSlack}) — switching to Attacking");
            }
        }

        /// <summary>
        /// LateUpdate runs AFTER all Update calls, including MonsterAI.UpdateAI.
        /// We force run speed here so it overrides Follow()'s walk decision
        /// for distances under 10m.
        /// </summary>
        private void LateUpdate()
        {
            if (_state == State_Moving && _character != null)
                _character.SetRun(true);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Attacking — swing at resource
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateAttacking()
        {
            if (!IsTargetValid(_target))
            {
                Log("Target destroyed — waiting 1.5s for logs/drops to spawn");
                ResetToIdle();
                // Delay rescan so TreeLog/drops have time to spawn after tree falls
                _scanTimer = 1.5f;
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
                if (_ai != null) _ai.SetFollowTarget(_target);
                _moveTimer = 0f;
                _moveLogTimer = 0f;
                _state = State_Moving;
                return;
            }

            _attackTimer -= Time.deltaTime;
            if (_attackTimer > 0f) return;

            // Check weapon is still equipped
            var weapon = ReflectionHelper.GetRightItem(_humanoid);
            if (weapon == null)
            {
                LogWarn("No weapon equipped during attack! Attempting re-equip...");
                int mode = GetMode();
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

            // Swing at target (null = not a Character, false = primary attack)
            bool attacked = _humanoid != null && _humanoid.StartAttack(null, false);
            _attackTimer = attacked ? AttackInterval : AttackRetry;

            Log($"Attack swing → \"{_target.name}\" " +
                $"success={attacked} dist={dist:F1}m " +
                $"weapon=\"{weapon?.m_shared?.m_name ?? "NONE"}\" " +
                $"equipped={(_humanoid != null && weapon != null ? _humanoid.IsItemEquiped(weapon) : false)} " +
                $"nextSwing={_attackTimer:F1}s");

            if (!attacked)
            {
                // Extra diagnostics on attack failure
                var animator = _humanoid?.GetComponentInChildren<Animator>();
                string animState = "unknown";
                if (animator != null)
                {
                    var stateInfo = animator.GetCurrentAnimatorStateInfo(0);
                    animState = $"hash={stateInfo.shortNameHash} norm={stateInfo.normalizedTime:F2}";
                }
                LogWarn($"Attack FAILED — animState=[{animState}] " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "NONE"}\" " +
                    $"rightItem=\"{ReflectionHelper.GetRightItem(_humanoid)?.m_shared?.m_name ?? "NONE"}\"");
            }
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
            int treeBaseCount = 0, treeLogCount = 0, rockCount = 0;

            for (int i = 0; i < count; i++)
            {
                var col = _scanBuffer[i];
                if (col == null) continue;

                string type;
                GameObject candidate = GetHarvestCandidateWithType(col, mode, out type);
                if (candidate == null) continue;
                if (!_seenIds.Add(candidate.GetInstanceID())) continue;

                candidateCount++;
                float dist = Vector3.Distance(transform.position, candidate.transform.position);

                // Count by type
                if (type == "TreeBase") treeBaseCount++;
                else if (type == "TreeLog") treeLogCount++;
                else rockCount++;

                // Log first 5 candidates and any TreeLogs (always interesting)
                if (candidateCount <= 5 || type == "TreeLog")
                    Log($"  Candidate #{candidateCount}: \"{candidate.name}\" " +
                        $"type={type} dist={dist:F1}m pos={candidate.transform.position:F1}");

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            if (candidateCount > 5)
                Log($"  ...and {candidateCount - 5} more candidates");

            // Type breakdown
            if (mode == CompanionSetup.ModeGatherWood)
                Log($"  Breakdown: {treeBaseCount} TreeBase, {treeLogCount} TreeLog " +
                    $"(total {candidateCount} unique targets)");
            else
                Log($"  Breakdown: {rockCount} rocks (total {candidateCount} unique targets)");

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
            }
            else // Stone or Ore
            {
                var rock5 = col.GetComponentInParent<MineRock5>();
                if (rock5 != null) { type = "MineRock5"; return rock5.gameObject; }

                var rock = col.GetComponentInParent<MineRock>();
                if (rock != null) { type = "MineRock"; return rock.gameObject; }
            }

            return null;
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

        private int GetMode()
        {
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
