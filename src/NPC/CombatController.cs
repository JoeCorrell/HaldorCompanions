using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Combat AI controller for companions. Manages weapon selection (melee vs bow),
    /// ensures tools/pickaxes are never used in combat, bow charging via vanilla
    /// ChargeStart system, shield parrying, power attacks, and retreat behavior.
    ///
    /// Active in Follow (0) and Stay (4) modes. Gather modes defer to HarvestController.
    /// </summary>
    public class CombatController : MonoBehaviour
    {
        internal enum CombatPhase { Idle, Melee, Ranged, Retreat }

        internal CombatPhase Phase => _phase;

        // ── Tuning ──────────────────────────────────────────────────────────
        private const float HealthRetreatPct    = 0.30f;
        private const float StaminaRetreatPct   = 0.15f;
        private const float HealthRecoverPct    = 0.50f;
        private const float StaminaRecoverPct   = 0.30f;
        private const float AttackCooldown      = 0.3f;
        private const float ConsumeCooldown     = 10f;
        private const float BowMaxRange         = 30f;
        private const float BowMinRange         = 10f;    // below this, always melee
        private const float BowSwitchDistance   = 20f;    // switch TO bow when target > this
        private const float MeleeSwitchDistance  = 12f;    // switch FROM bow to melee when target < this
        private const float PowerAttackCooldown = 3f;
        private const float RetreatDistance     = 12f;
        private const float StuckTimeout        = 8f;     // disengage if stuck this long
        private const float HeartbeatIdleInterval   = 10f;  // log interval when idle (no combat)
        private const float HeartbeatCombatInterval = 2f;   // log interval during active combat

        // ── Blocking / parry tuning ────────────────────────────────────────
        private const float ThreatDetectRange    = 8f;    // detect enemy attacks within this range
        private const float ProjectileDetectRange = 20f;  // detect incoming arrows/spells within this range
        private const float ProjectileScanInterval = 0.25f; // scan frequency for projectiles
        private const float BlockGrace           = 0.3f;  // hold block after last threat clears
        private const float BlockSafetyCap     = 3.0f;  // safety cap: force counter after 3s continuous block
        private const float CounterWindowDuration = 0.8f; // post-parry attack window (no blocking allowed)

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI      _ai;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionSetup   _setup;
        private CompanionStamina _stamina;
        private HarvestController _harvest;
        private ZNetView         _nview;

        // ── State ───────────────────────────────────────────────────────────
        private CombatPhase _phase;
        private float _attackCooldownTimer;
        private float _consumeTimer;
        private float _powerAttackTimer;
        private float _retreatTimer;
        private float _heartbeatTimer;
        private float _stuckTimer;
        private Vector3 _lastStuckPos;
        private bool  _initialized;
        private bool  _bowEquipped;

        // Bow draw state (manual — vanilla ChargeStart doesn't work for player bows)
        private float _bowDrawTimer;
        private float _bowFireCooldown;
        private const float BowDrawTime     = 1.2f;  // seconds to fully draw bow
        private const float BowFireInterval = 2.5f;  // seconds between shots

        // Target abandon tracking — prevents infinite re-engage on fleeing animals
        private int   _abandonedTargetId;
        private float _abandonCooldown;

        // ── Blocking / parry state ─────────────────────────────────────────
        /// <summary>
        /// When true, CompanionAI.DoAttack is suppressed.
        /// Set when the companion should be blocking instead of attacking.
        /// Delegates to CompanionAI.SuppressAttack directly.
        /// </summary>
        private bool SuppressAttack
        {
            get => _ai != null && _ai.SuppressAttack;
            set { if (_ai != null) _ai.SuppressAttack = value; }
        }

        private readonly HashSet<int> _activeAttackers = new HashSet<int>();
        private float _blockGraceTimer;
        private float _blockHoldTimer;      // how long block has been held continuously
        private float _counterWindow;
        private float _projectileScanTimer;
        private bool  _lastProjectileThreat;
        private bool  _wasBlocking;         // track transitions for counter-attack window

        // ── Dodge state ──────────────────────────────────────────────────────
        private float _dodgeCooldown;
        private float _dodgeDuration;
        private Vector3 _dodgeDirection;
        private bool _isDodging;
        private const float DodgeCooldownTime = 2.5f;
        private const float DodgeDurationTime = 0.3f;
        private const float DodgeStaminaCost  = 15f;

        // Per-phase periodic logging timers
        private float _rangedLogTimer;    // log bow draw progress
        private float _retreatLogTimer;   // log retreat status
        private float _blockLogTimer;     // log block events (throttled)

        private void Awake()
        {
            _ai       = GetComponent<CompanionAI>();
            _humanoid = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _setup    = GetComponent<CompanionSetup>();
            _stamina  = GetComponent<CompanionStamina>();
            _harvest  = GetComponent<HarvestController>();
            _nview    = GetComponent<ZNetView>();
        }

        private void Start()
        {
            _initialized = _ai != null && _humanoid != null && _character != null &&
                           _setup != null && _nview != null;
            if (!_initialized)
                CompanionsPlugin.Log.LogWarning("[Combat] CombatController missing components — " +
                    $"ai={_ai != null} humanoid={_humanoid != null} char={_character != null} " +
                    $"setup={_setup != null} nview={_nview != null}");
            else
                CompanionsPlugin.Log.LogInfo("[Combat] CombatController initialized OK");
        }

        private void Update()
        {
            if (!_initialized) return;
            if (_nview.GetZDO() == null || !_nview.IsOwner()) return;

            float dt = Time.deltaTime;
            _attackCooldownTimer = Mathf.Max(0f, _attackCooldownTimer - dt);
            _consumeTimer = Mathf.Max(0f, _consumeTimer - dt);
            _powerAttackTimer = Mathf.Max(0f, _powerAttackTimer - dt);
            _bowFireCooldown = Mathf.Max(0f, _bowFireCooldown - dt);
            _abandonCooldown = Mathf.Max(0f, _abandonCooldown - dt);
            _dodgeCooldown = Mathf.Max(0f, _dodgeCooldown - dt);

            // Read combat stance
            int stance = _setup != null ? _setup.GetCombatStance() : CompanionSetup.StanceBalanced;

            // Active dodge — supersedes all other combat logic
            if (_isDodging)
            {
                _dodgeDuration -= dt;
                if (_dodgeDuration <= 0f)
                {
                    _isDodging = false;
                }
                else
                {
                    _ai.PushDirection(_dodgeDirection, true);
                    return;
                }
            }

            // Mode check — in gather modes, only allow self-defense combat
            int mode = _nview.GetZDO().GetInt(CompanionSetup.ActionModeHash,
                CompanionSetup.ModeFollow);
            bool isGatherMode = mode >= CompanionSetup.ModeGatherWood
                             && mode <= CompanionSetup.ModeGatherOre;

            if (isGatherMode)
            {
                // In gather mode: only engage if a nearby enemy is actively targeting us
                Character gatherTarget = _ai.m_targetCreature;
                bool nearbyThreat = gatherTarget != null && !gatherTarget.IsDead()
                    && gatherTarget.GetHealth() > 0f
                    && Vector3.Distance(transform.position, gatherTarget.transform.position) < 10f;

                if (!nearbyThreat)
                {
                    // Log WHY the threat check failed — helps diagnose dead-enemy lingering
                    if (_phase != CombatPhase.Idle && gatherTarget != null)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] Gather threat cleared — \"{gatherTarget.m_name}\" " +
                            $"isDead={gatherTarget.IsDead()} hp={gatherTarget.GetHealth():F0} " +
                            $"dist={Vector3.Distance(transform.position, gatherTarget.transform.position):F1}");
                    }
                    if (_phase != CombatPhase.Idle) ExitCombat("gather mode, no nearby threat");
                    return;
                }
                // Otherwise fall through to combat logic for self-defense
            }
            else if (mode != CompanionSetup.ModeFollow && mode != CompanionSetup.ModeStay)
            {
                if (_phase != CombatPhase.Idle) ExitCombat("invalid mode");
                return;
            }

            // Don't interfere with UI
            if (!isGatherMode && _harvest != null && _harvest.IsActive)
            {
                if (_phase != CombatPhase.Idle) ExitCombat("harvest active");
                return;
            }
            var panel = CompanionInteractPanel.Instance;
            if (panel != null && panel.IsVisible && panel.CurrentCompanion == _setup) return;

            // Get current target from CompanionAI
            Character target = _ai.m_targetCreature;

            // ── Heartbeat logging (less frequent when idle) ──
            _heartbeatTimer -= dt;
            if (_heartbeatTimer <= 0f)
            {
                float hbInterval = (_phase == CombatPhase.Idle)
                    ? HeartbeatIdleInterval
                    : HeartbeatCombatInterval;
                _heartbeatTimer = hbInterval;
                LogHeartbeat(target);
            }

            if (target == null || target.IsDead())
            {
                if (_phase != CombatPhase.Idle)
                {
                    if (target != null)
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] Target lost — \"{target.m_name}\" isDead={target.IsDead()} " +
                            $"hp={target.GetHealth():F0} phase={_phase}");
                    ExitCombat(target == null ? "no target" : "target dead");
                }
                return;
            }

            // ── Early abandon check (before ENGAGE log to prevent spam) ──
            if (_abandonCooldown > 0f && target.GetInstanceID() == _abandonedTargetId)
            {
                _ai.ClearTargets();
                if (_phase != CombatPhase.Idle) ExitCombat("target on abandon cooldown");
                return;
            }

            // ── Log new combat engagement ──
            if (_phase == CombatPhase.Idle)
            {
                float engageDist = Vector3.Distance(transform.position, target.transform.position);
                bool isAnimal = target.GetComponent<AnimalAI>() != null;
                bool hasBowNow = HasBowAndArrows();
                var curWeapon = _humanoid.GetCurrentWeapon();
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] ENGAGE target=\"{target.m_name}\" dist={engageDist:F1} " +
                    $"isAnimal={isAnimal} hasBow={hasBowNow} " +
                    $"weapon=\"{curWeapon?.m_shared?.m_name ?? "NONE"}\" " +
                    $"hp={_character.GetHealthPercentage():P0} " +
                    $"mode={(isGatherMode ? "gather(self-defense)" : "follow")}");
            }

            // ── CRITICAL: Ensure we have a combat weapon, not a tool/pickaxe ──
            EnsureCombatWeapon();

            // ── Stuck detection (melee only — ranged stands still intentionally) ──
            float movedSinceLast = Vector3.Distance(transform.position, _lastStuckPos);
            if (_phase == CombatPhase.Melee)
            {
                // Use same distance formula as CompanionAI.UpdateCombat
                // (center-to-center minus target radius) for consistency.
                float distForStuck = Vector3.Distance(transform.position, target.transform.position)
                                     - target.GetRadius();
                var stuckWeapon = _humanoid.GetCurrentWeapon();
                float stuckRange = stuckWeapon != null ? stuckWeapon.m_shared.m_aiAttackRange : 2f;
                // Use the stop range (half weapon range) — companion should be
                // within this distance when properly engaged. The old check used
                // stuckRange + 1 which was too generous and prevented stuck
                // detection when the companion was swinging at air.
                float stuckStopRange = Mathf.Max(stuckRange * 0.5f, 1.5f);
                bool inStopRange = distForStuck <= stuckStopRange;

                // Don't count as stuck when in close range — standing still between
                // attacks is normal melee behavior, not a stuck state.
                if (movedSinceLast < 0.3f && !_character.InAttack() && !inStopRange)
                    _stuckTimer += dt;
                else
                    _stuckTimer = 0f;

                if (_stuckTimer > StuckTimeout)
                {
                    CompanionsPlugin.Log.LogWarning(
                        $"[Combat] STUCK for {_stuckTimer:F1}s in {_phase} — " +
                        $"target=\"{target.m_name}\" dist={Vector3.Distance(transform.position, target.transform.position):F1} " +
                        $"inAttack={_character.InAttack()} — clearing target");
                    AbandonTarget(target);
                    _ai.ClearTargets();
                    ExitCombat("stuck timeout");
                    _stuckTimer = 0f;
                    return;
                }
            }
            else
            {
                _stuckTimer = 0f;
            }
            _lastStuckPos = transform.position;

            // ── Retreat check (highest priority, stance-aware thresholds) ──
            float healthPct = _character.GetHealthPercentage();
            float staminaPct = _stamina != null ? _stamina.GetStaminaPercentage() : 1f;

            // Stance-modified retreat thresholds
            float retreatHpPct, retreatStamPct;
            switch (stance)
            {
                case CompanionSetup.StanceAggressive:
                    retreatHpPct = 0.15f; retreatStamPct = 0.05f; break;
                case CompanionSetup.StanceDefensive:
                    retreatHpPct = 0.45f; retreatStamPct = 0.25f; break;
                default:
                    retreatHpPct = HealthRetreatPct; retreatStamPct = StaminaRetreatPct; break;
            }

            if (_phase == CombatPhase.Retreat)
            {
                if (healthPct >= HealthRecoverPct && staminaPct >= StaminaRecoverPct)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] Retreat RECOVERED — hp={healthPct:P0} stam={staminaPct:P0} — re-engaging");
                    ExitCombat("recovered from retreat");
                }
                else
                {
                    UpdateRetreat(target, dt, stance);
                    return;
                }
            }
            else if (healthPct < retreatHpPct || staminaPct < retreatStamPct)
            {
                TransitionTo(CombatPhase.Retreat);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Entering RETREAT — hp={healthPct:P0} stam={staminaPct:P0} stance={stance}");
                UpdateRetreat(target, dt, stance);
                return;
            }

            // ── Target classification + weapon selection ──
            float distToTarget = Vector3.Distance(transform.position, target.transform.position);
            bool isFleeingAnimal = target.GetComponent<AnimalAI>() != null;
            bool hasBow = HasBowAndArrows();

            // Fleeing animals: always use bow, never chase on foot
            if (isFleeingAnimal)
            {
                if (hasBow)
                {
                    if (_phase != CombatPhase.Ranged)
                    {
                        EquipBow();
                        TransitionTo(CombatPhase.Ranged);
                    }
                    UpdateRanged(target, dt);
                }
                else
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] Ignoring fleeing animal \"{target.m_name}\" — no bow+arrows");
                    AbandonTarget(target);
                    _ai.ClearTargets();
                    if (_phase != CombatPhase.Idle) ExitCombat("no bow for animal");
                }
                return;
            }

            // Distance-based weapon selection with hysteresis to prevent oscillation
            if (_phase == CombatPhase.Ranged)
            {
                // Currently shooting — switch to melee if target closes in
                if (distToTarget < MeleeSwitchDistance || !hasBow)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] Bow→Melee switch — dist={distToTarget:F1} < {MeleeSwitchDistance} " +
                        $"hasBow={hasBow}");
                    RestoreMeleeLoadout();
                    TransitionTo(CombatPhase.Melee);
                    EnsureShieldEquipped();
                    UpdateMelee(target, dt, stance);
                }
                else
                {
                    UpdateRanged(target, dt);
                }
            }
            else
            {
                // Currently melee or idle — switch to ranged if far enough and have bow
                if (hasBow && distToTarget > BowSwitchDistance)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] Melee→Bow switch — dist={distToTarget:F1} > {BowSwitchDistance}");
                    EquipBow();
                    TransitionTo(CombatPhase.Ranged);
                    UpdateRanged(target, dt);
                }
                else
                {
                    if (_phase != CombatPhase.Melee)
                    {
                        RestoreMeleeLoadout();
                        TransitionTo(CombatPhase.Melee);
                        EnsureShieldEquipped();
                    }
                    UpdateMelee(target, dt, stance);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Weapon Validation — never fight with tools!
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Checks if the currently equipped weapon is a tool (pickaxe, etc.)
        /// and forces a switch to the best combat weapon. This prevents the
        /// companion from fighting enemies with a pickaxe after gathering.
        /// </summary>
        private void EnsureCombatWeapon()
        {
            if (_bowEquipped) return; // bow management handles its own weapons

            var weapon = ReflectionHelper.GetRightItem(_humanoid);
            if (weapon == null) return;

            var type = weapon.m_shared.m_itemType;
            bool isTool = type == ItemDrop.ItemData.ItemType.Tool;

            // Pickaxes are TwoHandedWeapon with m_pickaxe damage — never use in combat
            bool isPickaxe = weapon.GetDamage().m_pickaxe > 0f;

            // Also catch weapons that only have chop/pickaxe damage
            bool isToolDamageOnly = false;
            if (!isTool && !isPickaxe)
            {
                var dmg = weapon.GetDamage();
                float combatDmg = dmg.m_damage + dmg.m_blunt + dmg.m_slash + dmg.m_pierce +
                                  dmg.m_fire + dmg.m_frost + dmg.m_lightning + dmg.m_poison + dmg.m_spirit;
                isToolDamageOnly = combatDmg <= 0f && (dmg.m_chop > 0f);
            }

            if (isTool || isPickaxe || isToolDamageOnly)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] WEAPON CHECK — fighting with tool \"{weapon.m_shared.m_name}\" " +
                    $"(type={type} isTool={isTool} isPickaxe={isPickaxe} toolDmgOnly={isToolDamageOnly}) — forcing re-equip");

                // Clear suppress flag that HarvestController may have left on
                if (_setup != null)
                    _setup.SuppressAutoEquip = false;

                // Force auto-equip to pick best combat weapon
                _setup?.SyncEquipmentToInventory();

                var newWeapon = ReflectionHelper.GetRightItem(_humanoid);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] After re-equip: \"{newWeapon?.m_shared?.m_name ?? "NONE"}\" " +
                    $"type={newWeapon?.m_shared?.m_itemType}");

                // Also restore shield since tool equip unequips it
                EnsureShieldEquipped();
            }
        }

        /// <summary>
        /// Ensures the shield is equipped in the left hand during melee combat.
        /// HarvestController unequips the shield when equipping tools, and
        /// EnsureCombatWeapon won't restore it if the axe is a valid combat weapon.
        /// </summary>
        private void EnsureShieldEquipped()
        {
            var left = ReflectionHelper.GetLeftItem(_humanoid);
            if (left != null) return; // something already in left hand

            var right = ReflectionHelper.GetRightItem(_humanoid);
            if (right != null)
            {
                var type = right.m_shared.m_itemType;
                if (type == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                    type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                    type == ItemDrop.ItemData.ItemType.Bow)
                    return; // 2H weapon — can't equip shield
            }

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            ItemDrop.ItemData bestShield = null;
            float bestBlock = 0f;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                float block = item.m_shared.m_blockPower;
                if (block > bestBlock) { bestBlock = block; bestShield = item; }
            }

            if (bestShield != null)
            {
                _humanoid.EquipItem(bestShield, true);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Equipped shield \"{bestShield.m_shared.m_name}\" for melee combat");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Melee Combat — defensive-first: block threats, then counter-attack
        // ════════════════════════════════════════════════════════════════════

        private void UpdateMelee(Character target, float dt, int stance)
        {
            // Use center-to-surface distance (consistent with CompanionAI.UpdateCombat)
            float dist = Vector3.Distance(transform.position, target.transform.position)
                         - target.GetRadius();
            var weapon = _humanoid.GetCurrentWeapon();
            float weaponRange = weapon != null ? weapon.m_shared.m_aiAttackRange : 2f;

            // ── 1. Threat assessment — scan for enemies winding up attacks + projectiles ──
            bool anyMeleeThreats = false;
            bool newThreatDetected = false;
            Character closestAttacker = null;
            float closestAttackerDist = float.MaxValue;

            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == _character || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;
                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d > ThreatDetectRange) continue;

                if (c.InAttack())
                {
                    anyMeleeThreats = true;
                    if (d < closestAttackerDist)
                    {
                        closestAttacker = c;
                        closestAttackerDist = d;
                    }
                    int cid = c.GetInstanceID();
                    if (!_activeAttackers.Contains(cid))
                    {
                        _activeAttackers.Add(cid);
                        newThreatDetected = true;
                        _blockLogTimer -= dt;
                        if (_blockLogTimer <= 0f)
                        {
                            _blockLogTimer = 1f;
                            CompanionsPlugin.Log.LogDebug(
                                $"[Combat] NEW THREAT — \"{c.m_name}\" InAttack at {d:F1}m " +
                                $"(active threats: {_activeAttackers.Count})");
                        }
                    }
                }
            }

            // ── 1b. Dodge check — before block decision ──
            if (_dodgeCooldown <= 0f && !_isDodging && !_character.InAttack() &&
                !ReflectionHelper.GetBlocking(_character) &&
                stance != CompanionSetup.StanceAggressive &&
                closestAttacker != null)
            {
                // Check if attacker is facing us
                Vector3 attackerToUs = (transform.position - closestAttacker.transform.position).normalized;
                float facingDot = Vector3.Dot(closestAttacker.transform.forward, attackerToUs);
                bool hasStamina = _stamina == null || _stamina.GetStaminaPercentage() > 0.1f;

                if (facingDot > 0.5f && hasStamina)
                {
                    // Perpendicular dodge direction
                    Vector3 perp = Vector3.Cross(closestAttacker.transform.forward, Vector3.up).normalized;

                    // Pick side: toward player if possible
                    var followObj = _ai.GetFollowTarget();
                    if (followObj != null)
                    {
                        Vector3 toPlayer = (followObj.transform.position - transform.position).normalized;
                        if (Vector3.Dot(toPlayer, -perp) > Vector3.Dot(toPlayer, perp))
                            perp = -perp;
                    }

                    _dodgeDirection = perp;
                    _isDodging = true;
                    _dodgeDuration = DodgeDurationTime;
                    float cooldown = stance == CompanionSetup.StanceDefensive
                        ? DodgeCooldownTime * 0.6f
                        : DodgeCooldownTime;
                    _dodgeCooldown = cooldown;
                    _stamina?.UseStamina(DodgeStaminaCost);

                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] DODGE — attacker=\"{closestAttacker.m_name}\" " +
                        $"dist={closestAttackerDist:F1} cd={cooldown:F1}s");
                    return; // skip block/attack this frame
                }
            }

            // Remove stale attacker IDs (enemies no longer InAttack)
            if (_activeAttackers.Count > 0)
                _activeAttackers.RemoveWhere(id => !IsEnemyStillAttacking(id));

            // Projectile threat detection (lower frequency scan)
            bool projectileThreat = DetectIncomingProjectiles(dt);

            bool anyThreats = anyMeleeThreats || projectileThreat;

            // ── 2. Block decision ──
            // Hold shield while attackers are actively swinging (InAttack).
            // Drop shield AFTER they finish + grace period → enter counter window.
            // This ensures the shield is up when the hit lands so BlockAttack fires.
            bool hasShield = HasBlocker();
            bool inCounterWindow = _counterWindow > 0f;

            // Grace timer: keep blocking briefly after last attacker finishes swing
            if (anyMeleeThreats || projectileThreat)
                _blockGraceTimer = BlockGrace;
            else
                _blockGraceTimer = Mathf.Max(0f, _blockGraceTimer - dt);

            // We want to block if threats active OR grace period still running
            bool wantBlock = hasShield && (anyMeleeThreats || projectileThreat || _blockGraceTimer > 0f);

            // Safety cap: if blocking for > 3s continuously, handle it.
            // When threats are still active, DON'T drop shield — just reset the
            // hold timer and parry window so the companion keeps blocking through
            // chained enemy attacks. Only force a counter-attack window when no
            // threats remain (edge case: grace timer keeping block alive too long).
            if (_wasBlocking)
            {
                _blockHoldTimer += dt;
                if (_blockHoldTimer >= BlockSafetyCap)
                {
                    if (anyMeleeThreats || projectileThreat)
                    {
                        // Threats still active — keep blocking, reset timers for
                        // fresh parry window. Dropping shield here caused the
                        // companion to counter-attack while enemies were mid-swing.
                        _blockHoldTimer = 0f;
                        ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] Block SAFETY RESET ({BlockSafetyCap:F1}s) — threats still active, " +
                            $"continuing to block (threats={_activeAttackers.Count})");
                    }
                    else
                    {
                        // No threats — safe to counter-attack
                        ReflectionHelper.TrySetBlocking(_character, false);
                        _wasBlocking = false;
                        _blockHoldTimer = 0f;
                        _activeAttackers.Clear();
                        SuppressAttack = false;
                        _counterWindow = CounterWindowDuration;
                        inCounterWindow = true;

                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] Block SAFETY CAP ({BlockSafetyCap:F1}s) — no threats, counter-attack window");
                    }
                }
            }

            // Natural block end: threats cleared AND grace expired → counter window
            if (_wasBlocking && !wantBlock)
            {
                ReflectionHelper.TrySetBlocking(_character, false);
                _wasBlocking = false;
                _blockHoldTimer = 0f;
                _activeAttackers.Clear();
                SuppressAttack = false;
                _counterWindow = CounterWindowDuration;
                inCounterWindow = true;

                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Block ended (threats cleared) — entering {CounterWindowDuration:F1}s counter window");
            }

            bool shouldBlock = wantBlock && !inCounterWindow;

            // Aggressive: never block
            if (stance == CompanionSetup.StanceAggressive)
                shouldBlock = false;

            if (shouldBlock)
            {
                SuppressAttack = true;

                if (!_character.InAttack())
                {
                    bool wasAlreadyBlocking = _wasBlocking;
                    ReflectionHelper.TrySetBlocking(_character, true);
                    _wasBlocking = true;

                    // Reset block timer for fresh parry window when a new attacker appears
                    if (newThreatDetected)
                    {
                        ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] BLOCK — new threat detected, parry timer reset " +
                            $"(hold={_blockHoldTimer:F2}s threats={_activeAttackers.Count})");
                    }
                    else if (!wasAlreadyBlocking)
                    {
                        ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] BLOCK RAISED — shield up, parry timer reset " +
                            $"(threats={_activeAttackers.Count})");
                    }
                }

                return; // Don't proceed to attack logic while blocking
            }

            // ── 3. Not blocking — clear block state (safety fallback) ──
            SuppressAttack = false;

            if (_wasBlocking)
            {
                // Safety: should have been cleared above, but handle edge cases
                // (e.g., shield broke mid-block)
                ReflectionHelper.TrySetBlocking(_character, false);
                _wasBlocking = false;
                _blockHoldTimer = 0f;
                _activeAttackers.Clear();
                _counterWindow = CounterWindowDuration;

                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Block ended (fallback) — entering {CounterWindowDuration:F1}s counter-attack window");
            }

            // ── 4. Counter-attack window — post-parry, companion swings freely ──
            if (_counterWindow > 0f)
            {
                // Threats reappeared during counter window → cancel and re-block
                // immediately instead of attacking into enemy swings.
                if (wantBlock && !_character.InAttack())
                {
                    _counterWindow = 0f;
                    SuppressAttack = true;
                    ReflectionHelper.TrySetBlocking(_character, true);
                    _wasBlocking = true;
                    _blockHoldTimer = 0f;
                    ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] Counter window CANCELLED — threats reappeared, re-blocking " +
                        $"(threats={_activeAttackers.Count})");
                    return;
                }

                // Close the gap after parry pushback — MoveTowards at full run speed
                // bypasses blocking move-speed penalty and pathfinding delay.
                // Use stop range (half weapon range) to get within reliable hit distance.
                float counterStopRange = Mathf.Max(weaponRange * 0.5f, 1.5f);
                if (dist > counterStopRange && !_character.InAttack())
                {
                    Vector3 dir = (target.transform.position - transform.position);
                    dir.y = 0f;
                    if (dir.sqrMagnitude > 0.01f)
                        _ai.PushDirection(dir.normalized, true);

                    // Don't burn counter timer while chasing a staggered target —
                    // give the companion time to close the gap and deliver the hit.
                    if (target.IsStaggering())
                    {
                        _counterWindow = Mathf.Max(_counterWindow, CounterWindowDuration * 0.5f);
                    }
                }

                _counterWindow -= dt;

                if (target.IsStaggering() && dist <= weaponRange + 0.5f &&
                    _powerAttackTimer <= 0f && _attackCooldownTimer <= 0f)
                {
                    if (weapon != null && weapon.HaveSecondaryAttack())
                    {
                        _humanoid.StartAttack(target, true);
                        _powerAttackTimer = PowerAttackCooldown;
                        _attackCooldownTimer = AttackCooldown;
                        _counterWindow = 0f;
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] COUNTER POWER ATTACK on staggered \"{target.m_name}\" " +
                            $"dist={dist:F1} range={weaponRange:F1}");
                    }
                }
                // Normal attacks are handled by CompanionAI.DoAttack (SuppressAttack=false)
            }

            // ── 5. Normal attacks handled by CompanionAI.DoAttack (SuppressAttack=false) ──
            // Also fire power attacks on staggered targets outside counter window
            if (_counterWindow <= 0f && target.IsStaggering() && dist <= weaponRange + 0.5f &&
                _powerAttackTimer <= 0f && _attackCooldownTimer <= 0f)
            {
                if (weapon != null && weapon.HaveSecondaryAttack())
                {
                    _humanoid.StartAttack(target, true);
                    float paCooldown = stance == CompanionSetup.StanceAggressive
                        ? PowerAttackCooldown * 0.5f : PowerAttackCooldown;
                    _powerAttackTimer = paCooldown;
                    _attackCooldownTimer = AttackCooldown;
                    CompanionsPlugin.Log.LogDebug(
                        $"[Combat] POWER ATTACK on staggered \"{target.m_name}\" " +
                        $"dist={dist:F1} range={weaponRange:F1}");
                }
            }
        }

        /// <summary>
        /// Check if a specific enemy (by instance ID) is still InAttack.
        /// Used to clean stale entries from _activeAttackers.
        /// </summary>
        private bool IsEnemyStillAttacking(int instanceId)
        {
            foreach (Character c in Character.GetAllCharacters())
            {
                if (c.GetInstanceID() == instanceId)
                    return c.InAttack() && !c.IsDead();
            }
            return false;
        }

        /// <summary>
        /// Check if companion has a shield or other blocker equipped.
        /// </summary>
        private bool HasBlocker()
        {
            var blocker = ReflectionHelper.GetLeftItem(_humanoid);
            return blocker != null && blocker.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield;
        }

        /// <summary>
        /// Scans for incoming enemy projectiles heading toward the companion.
        /// Runs at a lower frequency (ProjectileScanInterval) to reduce cost.
        /// </summary>
        private bool DetectIncomingProjectiles(float dt)
        {
            _projectileScanTimer -= dt;
            if (_projectileScanTimer > 0f) return _lastProjectileThreat;
            _projectileScanTimer = ProjectileScanInterval;

            _lastProjectileThreat = false;

            var projectiles = Object.FindObjectsByType<Projectile>(FindObjectsSortMode.None);
            Vector3 myPos = transform.position + Vector3.up; // chest height

            for (int i = 0; i < projectiles.Length; i++)
            {
                var proj = projectiles[i];
                if (proj == null) continue;

                Vector3 projPos = proj.transform.position;
                Vector3 toUs = myPos - projPos;
                float sqrDist = toUs.sqrMagnitude;
                if (sqrDist > ProjectileDetectRange * ProjectileDetectRange || sqrDist < 1f)
                    continue;

                // Skip friendly projectiles
                Character owner = ReflectionHelper.GetProjectileOwner(proj);
                if (owner != null && !BaseAI.IsEnemy(_character, owner))
                    continue;

                // Check if projectile velocity is heading toward us
                Vector3 vel = proj.GetVelocity();
                if (vel.sqrMagnitude < 1f) continue;

                float dot = Vector3.Dot(vel.normalized, toUs.normalized);
                if (dot > 0.5f) // heading roughly toward us
                {
                    _lastProjectileThreat = true;

                    _blockLogTimer -= dt;
                    if (_blockLogTimer <= 0f)
                    {
                        _blockLogTimer = 2f;
                        CompanionsPlugin.Log.LogDebug(
                            $"[Combat] PROJECTILE threat — dist={Mathf.Sqrt(sqrDist):F1}m " +
                            $"dot={dot:F2} owner=\"{owner?.m_name ?? "?"}\"");
                    }
                    return true;
                }
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Ranged Combat — manual draw timer + fire (vanilla ChargeStart
        //  doesn't work for player bows on AI creatures)
        // ════════════════════════════════════════════════════════════════════

        private void UpdateRanged(Character target, float dt)
        {
            float dist = Vector3.Distance(transform.position, target.transform.position);

            // Target out of range — give up and abandon
            if (dist > BowMaxRange)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Target \"{target.m_name}\" out of bow range ({dist:F1} > {BowMaxRange}) — abandoning");
                AbandonTarget(target);
                _ai.ClearTargets();
                ExitCombat("target out of bow range");
                return;
            }

            // Don't fire while attack animation is playing
            if (_character.InAttack()) return;

            // Aim directly at target center mass.
            // Velocity lead is intentionally omitted: draw% on NPC-wielded player bows
            // is unknown, so arrow speed is unpredictable — lead only worsens accuracy.
            // More importantly, a lead-offset aimPoint diverges from where vanilla BaseAI
            // rotates to face the target, causing IsLookingAt to always return false.
            Vector3 aimPoint = target.GetCenterPoint();

            // Face target and stop to aim
            _ai.LookAtPoint( aimPoint);
            _ai.StopMoving();

            // 30° tolerance — generous enough for NPC rotation lag while still
            // ensuring the shot is in roughly the right direction.
            bool onTarget = _ai.IsLookingAtPoint( aimPoint, 30f);

            // Draw timer — only accumulates while facing target
            if (onTarget && _bowFireCooldown <= 0f)
                _bowDrawTimer += dt;
            else if (!onTarget)
                _bowDrawTimer = Mathf.Max(0f, _bowDrawTimer - dt * 2f); // decay if off-target

            // Periodic bow draw progress log (every 1s)
            _rangedLogTimer -= dt;
            if (_rangedLogTimer <= 0f)
            {
                _rangedLogTimer = 1f;
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] BOW AIM — target=\"{target.m_name}\" dist={dist:F1} " +
                    $"draw={_bowDrawTimer:F2}/{BowDrawTime:F2} onTarget={onTarget} " +
                    $"fireCD={_bowFireCooldown:F1} inAttack={_character.InAttack()} " +
                    $"vel={target.GetVelocity().magnitude:F1}");
            }

            // Fire when fully drawn and on target
            if (_bowDrawTimer >= BowDrawTime && onTarget && _bowFireCooldown <= 0f)
            {
                bool fired = _humanoid.StartAttack(target, false);
                _bowDrawTimer = 0f;
                _bowFireCooldown = BowFireInterval;

                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] BOW FIRE at \"{target.m_name}\" dist={dist:F1} " +
                    $"fired={fired} onTarget={onTarget}");
            }
        }

        private void AbandonTarget(Character target)
        {
            if (target == null) return;
            _abandonedTargetId = target.GetInstanceID();
            _abandonCooldown = 30f; // Don't re-engage this target for 30s
            CompanionsPlugin.Log.LogDebug(
                $"[Combat] Abandoned target \"{target.m_name}\" — 30s cooldown");
        }

        // ════════════════════════════════════════════════════════════════════
        //  Retreat + Consumables
        // ════════════════════════════════════════════════════════════════════

        private void UpdateRetreat(Character target, float dt, int stance)
        {
            _retreatTimer += dt;

            // Smart retreat — blend toward player instead of random direction
            Vector3 myPos = transform.position;
            Vector3 targetPos = target.transform.position;
            Vector3 awayDir = (myPos - targetPos).normalized;

            var player = Player.m_localPlayer;
            if (player != null)
            {
                Vector3 toPlayer = (player.transform.position - myPos).normalized;
                float enemyToPlayer = Vector3.Distance(targetPos, player.transform.position);
                float dotCheck = Vector3.Dot(toPlayer, awayDir);

                // Only blend toward player if player isn't behind the enemy
                // and enemy isn't too close to the player
                if (dotCheck > -0.5f && enemyToPlayer > 5f)
                {
                    float playerWeight = stance == CompanionSetup.StanceDefensive ? 0.85f : 0.7f;
                    awayDir = (toPlayer * playerWeight + awayDir * (1f - playerWeight)).normalized;
                }
            }

            Vector3 fleePoint = myPos + awayDir * RetreatDistance;
            _ai.MoveToPoint( dt, fleePoint, 2f, true);

            // Periodic retreat log
            _retreatLogTimer -= dt;
            if (_retreatLogTimer <= 0f)
            {
                _retreatLogTimer = 3f;
                float healthPct2 = _character.GetHealthPercentage();
                float stamPct2 = _stamina != null ? _stamina.GetStaminaPercentage() : 1f;
                float distTarget = Vector3.Distance(transform.position, target.transform.position);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] RETREATING — target=\"{target.m_name}\" dist={distTarget:F1} " +
                    $"hp={healthPct2:P0}/{HealthRecoverPct:P0} stam={stamPct2:P0}/{StaminaRecoverPct:P0} " +
                    $"retreatTimer={_retreatTimer:F1}s");
            }

            // Try consuming meads
            if (_consumeTimer <= 0f)
            {
                float healthPct = _character.GetHealthPercentage();
                float staminaPct = _stamina != null ? _stamina.GetStaminaPercentage() : 1f;

                if (healthPct < HealthRetreatPct)
                {
                    if (TryConsumeMead(MeadType.Health))
                    {
                        _consumeTimer = ConsumeCooldown;
                        CompanionsPlugin.Log.LogDebug("[Combat] Consumed health mead while retreating");
                    }
                }
                if (staminaPct < StaminaRetreatPct && _consumeTimer <= 0f)
                {
                    if (TryConsumeMead(MeadType.Stamina))
                    {
                        _consumeTimer = ConsumeCooldown;
                        CompanionsPlugin.Log.LogDebug("[Combat] Consumed stamina mead while retreating");
                    }
                }
            }

            // Safety: if retreating too long, disengage
            if (_retreatTimer > 15f)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Combat] Retreat timeout ({_retreatTimer:F1}s) — disengaging from \"{target.m_name}\"");
                _ai.ClearTargets();
                ExitCombat("retreat timeout");
                _retreatTimer = 0f;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Weapon Management
        // ════════════════════════════════════════════════════════════════════

        private void EquipBow()
        {
            if (_bowEquipped) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            _setup.SuppressAutoEquip = true;

            // Find best bow
            ItemDrop.ItemData bestBow = null;
            float bestDmg = 0f;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                {
                    float dmg = item.GetDamage().GetTotalDamage();
                    if (dmg > bestDmg) { bestDmg = dmg; bestBow = item; }
                }
            }

            if (bestBow != null)
            {
                // Unequip current weapons first
                var right = ReflectionHelper.GetRightItem(_humanoid);
                var left = ReflectionHelper.GetLeftItem(_humanoid);
                if (right != null) _humanoid.UnequipItem(right, false);
                if (left != null) _humanoid.UnequipItem(left, false);

                _humanoid.EquipItem(bestBow);
                EquipBestArrows();
                _bowEquipped = true;
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Equipped bow \"{bestBow.m_shared.m_name}\" dmg={bestDmg:F0} + arrows");
            }
            else
            {
                CompanionsPlugin.Log.LogWarning("[Combat] EquipBow — no valid bow found in inventory!");
                _setup.SuppressAutoEquip = false;
            }
        }

        private void EquipBestArrows()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            ItemDrop.ItemData bestAmmo = null;
            float bestDmg = 0f;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo)
                {
                    float dmg = item.GetDamage().GetTotalDamage();
                    if (dmg > bestDmg) { bestDmg = dmg; bestAmmo = item; }
                }
            }

            if (bestAmmo != null && !_humanoid.IsItemEquiped(bestAmmo))
            {
                _humanoid.EquipItem(bestAmmo);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Equipped arrows \"{bestAmmo.m_shared.m_name}\" x{bestAmmo.m_stack}");
            }
        }

        private void RestoreMeleeLoadout()
        {
            if (!_bowEquipped) return;
            _bowEquipped = false;

            // Stop any active charge animation
            if (_ai.IsCharging())
                _ai.ChargeStop();

            // Unequip bow + ammo — AutoEquipBest re-equips combat gear
            var right = ReflectionHelper.GetRightItem(_humanoid);
            if (right != null && right.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                _humanoid.UnequipItem(right, false);

            _setup.SuppressAutoEquip = false;
            _setup.SyncEquipmentToInventory();

            var newRight = ReflectionHelper.GetRightItem(_humanoid);
            CompanionsPlugin.Log.LogDebug(
                $"[Combat] Restored melee — now wielding \"{newRight?.m_shared?.m_name ?? "NONE"}\"");
        }

        /// <summary>
        /// Clean exit from any combat phase back to idle.
        /// Ensures weapons are restored and suppress flags cleared.
        /// </summary>
        private void ExitCombat(string reason)
        {
            var oldPhase = _phase;

            // Clear any lingering target reference — dead enemies can hold m_targetCreature
            // after IsDead() becomes true, keeping CombatController and HarvestController
            // stuck in self-defense until the ZDO is removed.
            _ai.ClearTargets();

            // Clear alert state — m_alerted persists after enemies die and causes
            // CompanionAI.UpdateAI to override movement with alert-scanning behavior,
            // preventing the companion from reaching harvest targets.
            _ai.SetAlerted(false);

            // Stop charging if active
            if (_ai.IsCharging())
            {
                CompanionsPlugin.Log.LogDebug("[Combat] ChargeStop — was charging on combat exit");
                _ai.ChargeStop();
            }

            if (_bowEquipped)
                RestoreMeleeLoadout();

            // Always clear suppress in case HarvestController left it on
            if (_setup != null)
                _setup.SuppressAutoEquip = false;

            // Clear blocking / parry state
            SuppressAttack = false;
            _activeAttackers.Clear();
            _blockGraceTimer = 0f;
            _blockHoldTimer = 0f;
            _counterWindow = 0f;
            _wasBlocking = false;
            if (ReflectionHelper.GetBlocking(_character))
                ReflectionHelper.TrySetBlocking(_character, false);

            _isDodging = false;

            TransitionTo(CombatPhase.Idle);
            _stuckTimer = 0f;
            _retreatTimer = 0f;
            _bowDrawTimer = 0f;
            _bowFireCooldown = 0f;

            CompanionsPlugin.Log.LogDebug(
                $"[Combat] EXIT combat ({oldPhase} → Idle) — reason: {reason}");
        }

        // ════════════════════════════════════════════════════════════════════
        //  Consumable Management
        // ════════════════════════════════════════════════════════════════════

        private enum MeadType { Health, Stamina }

        private bool TryConsumeMead(MeadType type)
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;

            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable)
                    continue;
                if (item.m_shared.m_consumeStatusEffect == null) continue;

                var se = item.m_shared.m_consumeStatusEffect as SE_Stats;
                if (se == null) continue;

                // Check if the companion already has this status effect active
                if (_character.GetSEMan().HaveStatusEffect(
                    item.m_shared.m_consumeStatusEffect.NameHash()))
                    continue;

                bool match = false;
                if (type == MeadType.Health)
                    match = se.m_healthOverTime > 0f || se.m_healthOverTimeDuration > 0f;
                else if (type == MeadType.Stamina)
                    match = se.m_staminaOverTime > 0f || se.m_staminaOverTimeDuration > 0f;

                if (!match) continue;

                // Consume: apply status effect + remove item
                _character.GetSEMan().AddStatusEffect(
                    item.m_shared.m_consumeStatusEffect, true);
                inv.RemoveOneItem(item);
                CompanionsPlugin.Log.LogDebug(
                    $"[Combat] Consumed \"{item.m_shared.m_name}\" ({type})");
                return true;
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Utility
        // ════════════════════════════════════════════════════════════════════

        private bool HasBowAndArrows()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;

            bool hasBow = false;
            bool hasAmmo = false;
            foreach (var item in inv.GetAllItems())
            {
                if (item?.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                    hasBow = true;
                if (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Ammo)
                    hasAmmo = true;
                if (hasBow && hasAmmo) return true;
            }
            return false;
        }

        private void TransitionTo(CombatPhase newPhase)
        {
            if (_phase == newPhase) return;
            CompanionsPlugin.Log.LogDebug($"[Combat] Phase: {_phase} → {newPhase}");
            _phase = newPhase;
            if (newPhase != CombatPhase.Retreat) _retreatTimer = 0f;
            _isDodging = false;

            // Clear blocking when leaving melee for any other phase
            if (newPhase != CombatPhase.Melee)
            {
                SuppressAttack = false;
                _activeAttackers.Clear();
                _blockGraceTimer = 0f;
                _blockHoldTimer = 0f;
                _counterWindow = 0f;
                _wasBlocking = false;
                if (ReflectionHelper.GetBlocking(_character))
                    ReflectionHelper.TrySetBlocking(_character, false);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Logging
        // ════════════════════════════════════════════════════════════════════

        private void LogHeartbeat(Character target)
        {
            var weapon = _humanoid.GetCurrentWeapon();
            var rightItem = ReflectionHelper.GetRightItem(_humanoid);
            var leftItem = ReflectionHelper.GetLeftItem(_humanoid);
            var followTarget = _ai.GetFollowTarget();
            float distToTarget = target != null
                ? Vector3.Distance(transform.position, target.transform.position)
                : -1f;
            float distToFollow = followTarget != null
                ? Vector3.Distance(transform.position, followTarget.transform.position)
                : -1f;

            CompanionsPlugin.Log.LogDebug(
                $"[Combat] ♥ phase={_phase} " +
                $"target=\"{target?.m_name ?? "null"}\" dist={distToTarget:F1} " +
                $"follow=\"{followTarget?.name ?? "null"}\" followDist={distToFollow:F1} " +
                $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                $"right=\"{rightItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"left=\"{leftItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"bowEquipped={_bowEquipped} bowDraw={_bowDrawTimer:F1}/{BowDrawTime:F1} " +
                $"bowCD={_bowFireCooldown:F1} charging={_ai.IsCharging()} " +
                $"inAttack={_character.InAttack()} blocking={_wasBlocking} " +
                $"suppressAtk={SuppressAttack} threats={_activeAttackers.Count} " +
                $"grace={_blockGraceTimer:F2} counter={_counterWindow:F2} " +
                $"hp={_character.GetHealthPercentage():P0} " +
                $"stam={(_stamina != null ? _stamina.GetStaminaPercentage() : 1f):P0} " +
                $"stuckTimer={_stuckTimer:F1} atkCD={_attackCooldownTimer:F1}");
        }
    }
}
