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

        /// <summary>
        /// When > 0, all AI movement is suppressed. Used to hold position
        /// after snapping to a bed/cart so animation starts at correct spot.
        /// </summary>
        internal float FreezeTimer;

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
        private DoorHandler _doorHandler;
        private CompanionRest _rest;

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
            _doorHandler = GetComponent<DoorHandler>();
            _rest = GetComponent<CompanionRest>();

            // Restore sleep state from ZDO
            ZDO zdo = m_nview.GetZDO();
            if (zdo != null)
            {
                m_sleeping = zdo.GetBool(ZDOVars.s_sleeping, m_sleeping);
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

            CompanionsPlugin.Log.LogInfo("[CompanionAI] Awake complete");
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
                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionAI] ClearTargets — creature=\"{m_targetCreature?.m_name ?? ""}\" " +
                    $"static=\"{m_targetStatic?.name ?? ""}\"");
            }
            m_targetCreature = null;
            m_targetStatic = null;
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
            if (Player.m_localPlayer != null)
                SetFollowTarget(Player.m_localPlayer.gameObject);
        }

        internal void SetMoveTarget(Vector3 pos)
        {
            CompanionsPlugin.Log.LogInfo(
                $"[AI] SetMoveTarget — target={pos:F1} dist={Vector3.Distance(transform.position, pos):F1}");
            PendingMoveTarget = pos;
            _pendingMoveTimeout = 30f;
            SetFollowTarget(null);
        }

        internal void CancelMoveTarget()
        {
            if (PendingMoveTarget == null) return;
            CompanionsPlugin.Log.LogInfo(
                $"[AI] CancelMoveTarget — was={PendingMoveTarget.Value:F1}");
            PendingMoveTarget = null;
            if (Player.m_localPlayer != null)
                SetFollowTarget(Player.m_localPlayer.gameObject);
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
                    CompanionsPlugin.Log.LogInfo($"[AI] FreezeTimer expired — resuming movement");
                return true;
            }

            // Resting (sitting/sleeping) — skip all movement so position stays
            if (_rest != null && _rest.IsResting)
                return true;

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

            // Cart navigation — walking to cart attach point
            if (PendingCartAttach != null)
            {
                _pendingCartTimeout -= dt;
                if (_pendingCartTimeout <= 0f || PendingCartAttach == null)
                {
                    CompanionsPlugin.Log.LogInfo("[AI] Cart navigation timed out — cancelling");
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
                        var body = GetComponent<Rigidbody>();
                        if (body != null)
                        {
                            body.position = attachWorldPos;
                            body.velocity = Vector3.zero;
                        }

                        Vector3 toCart = PendingCartAttach.transform.position - transform.position;
                        toCart.y = 0f;
                        if (toCart.sqrMagnitude > 0.01f)
                            transform.rotation = Quaternion.LookRotation(toCart.normalized);

                        FreezeTimer = 1f;
                        SetFollowTarget(PendingCartAttach.gameObject);
                        PendingCartAttach.Interact(PendingCartHumanoid, false, false);

                        CompanionsPlugin.Log.LogInfo(
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
                    CompanionsPlugin.Log.LogInfo("[AI] Move-to timed out — cancelling");
                    CancelMoveTarget();
                }
                else
                {
                    float distToMove = Vector3.Distance(transform.position, PendingMoveTarget.Value);
                    if (distToMove < 2f)
                    {
                        CompanionsPlugin.Log.LogInfo(
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

            // Directed target lock countdown
            if (DirectedTargetLockTimer > 0f)
                DirectedTargetLockTimer -= dt;

            // Read stance once for this frame
            int stance = _setup != null ? _setup.GetCombatStance() : CompanionSetup.StanceBalanced;

            // Passive stance: never target, never attack, just follow
            if (stance == CompanionSetup.StancePassive)
            {
                ClearTargets();
                if (IsAlerted()) SetAlerted(false);
                SuppressAttack = true;

                if ((_harvest != null && _harvest.IsActive) ||
                    (_repair != null && _repair.IsActive) ||
                    (_doorHandler != null && _doorHandler.IsActive))
                    return true;

                if (m_follow != null)
                    FollowWithFormation(m_follow, dt, stance);
                else
                    IdleMovement(dt);
                return true;
            }

            // Target acquisition with companion-specific suppression
            Humanoid humanoid = m_character as Humanoid;
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
                    (_doorHandler != null && _doorHandler.IsActive))
                    return true;

                if (m_follow != null)
                    FollowWithFormation(m_follow, dt, stance);
                else
                    IdleMovement(dt);
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

        private void UpdateTarget(Humanoid humanoid, float dt, int stance)
        {
            m_updateTargetTimer -= dt;

            // ── UI open → suppress targeting completely ──
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsVisible && _setup != null && panel.CurrentCompanion == _setup)
            {
                ThrottledTargetLog("UIOpen");
                ClearTargets();
                return;
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
                    CompanionsPlugin.Log.LogInfo(
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
                            targetsPlayer = aiTarget != null && aiTarget.IsPlayer();
                        }
                        if (eDist > 5f && !targetsPlayer)
                            enemy = null; // ignore distant non-threats
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
                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionAI] Suppressing targeting — reason={reason}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Combat Movement + Attack
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCombat(Humanoid humanoid, float dt, int stance)
        {
            if (m_targetCreature == null) return;

            ItemDrop.ItemData weapon = humanoid.GetCurrentWeapon();
            if (weapon == null)
            {
                // No weapon — just follow
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
                bool inRange = dist < weaponRange;

                if (!inRange || !canSee || !IsAlerted())
                {
                    // Out of range — move toward target (with flanking for melee)
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
                    // In range — stop and face target
                    StopMoving();
                }

                // Attack if in range, can see, and alerted
                if (inRange && canSee && IsAlerted())
                {
                    LookAt(m_targetCreature.GetTopPoint());

                    bool canAttack = CanAttackNow(weapon);
                    if (canAttack && IsLookingAt(m_lastKnownTargetPos,
                            weapon.m_shared.m_aiAttackMaxAngle,
                            weapon.m_shared.m_aiInvertAngleCheck))
                    {
                        DoAttack(m_targetCreature);
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
                    CompanionsPlugin.Log.LogInfo(
                        $"[CompanionAI] Power attack on staggered \"{target.m_name}\"");
            }

            return success;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Formation Following
        // ══════════════════════════════════════════════════════════════════════

        private void FollowWithFormation(GameObject target, float dt, int stance)
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
            bool handlingDoor = _doorHandler != null && _doorHandler.IsActive;

            if (moved < 0.1f && !inAttack && !atFollowDist && !harvesting && !repairing && !handlingDoor)
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

                    CompanionsPlugin.Log.LogWarning(
                        $"[CompanionAI] STUCK {_stuckDetectTimer:F1}s — " +
                        $"target=\"{m_targetCreature?.m_name ?? "null"}\" targetDist={distToTarget:F1} " +
                        $"follow=\"{follow?.name ?? "null"}\" followDist={distToFollow:F1} " +
                        $"inAttack={inAttack} isAlerted={IsAlerted()} " +
                        $"charging={IsCharging()} pos={transform.position:F1}");
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

                CompanionsPlugin.Log.LogInfo(
                    $"[CompanionAI] target=\"{m_targetCreature?.m_name ?? "null"}\"({distToTarget:F1}) " +
                    $"follow=\"{follow?.name ?? "null"}\"({distToFollow:F1}) " +
                    $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                    $"combat={combat?.Phase} " +
                    $"alerted={IsAlerted()} suppress={SuppressAttack} " +
                    $"vel={m_character?.GetVelocity().magnitude ?? 0f:F1} " +
                    $"moved={moved:F2}");
            }
        }
    }
}
