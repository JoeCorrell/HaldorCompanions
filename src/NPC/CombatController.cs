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
        private const float BlockDropDelay      = 0.6f;
        private const float BlockDetectRange    = 4f;
        private const float AttackCooldown      = 0.3f;
        private const float ConsumeCooldown     = 10f;
        private const float BowMaxRange         = 30f;
        private const float BowMinRange         = 10f;    // below this, always melee
        private const float BowSwitchDistance   = 20f;    // switch TO bow when target > this
        private const float MeleeSwitchDistance  = 12f;    // switch FROM bow to melee when target < this
        private const float PowerAttackCooldown = 3f;
        private const float RetreatDistance     = 12f;
        private const float StuckTimeout        = 8f;     // disengage if stuck this long
        private const float HeartbeatInterval   = 2f;     // detailed state log interval

        // ── Components ──────────────────────────────────────────────────────
        private MonsterAI        _ai;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionSetup   _setup;
        private CompanionStamina _stamina;
        private HarvestController _harvest;
        private ZNetView         _nview;

        // ── State ───────────────────────────────────────────────────────────
        private CombatPhase _phase;
        private float _blockTimer;
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

        private void Awake()
        {
            _ai       = GetComponent<MonsterAI>();
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

            // Mode check — in gather modes, only allow self-defense combat
            int mode = _nview.GetZDO().GetInt(CompanionSetup.ActionModeHash,
                CompanionSetup.ModeFollow);
            bool isGatherMode = mode >= CompanionSetup.ModeGatherWood
                             && mode <= CompanionSetup.ModeGatherOre;

            if (isGatherMode)
            {
                // In gather mode: only engage if a nearby enemy is actively targeting us
                Character gatherTarget = ReflectionHelper.GetTargetCreature(_ai);
                bool nearbyThreat = gatherTarget != null && !gatherTarget.IsDead()
                    && Vector3.Distance(transform.position, gatherTarget.transform.position) < 10f;

                if (!nearbyThreat)
                {
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

            // Get current target from MonsterAI
            Character target = ReflectionHelper.GetTargetCreature(_ai);

            // ── Heartbeat logging ──
            _heartbeatTimer -= dt;
            if (_heartbeatTimer <= 0f)
            {
                _heartbeatTimer = HeartbeatInterval;
                LogHeartbeat(target);
            }

            if (target == null || target.IsDead())
            {
                if (_phase != CombatPhase.Idle)
                    ExitCombat(target == null ? "no target" : "target dead");
                return;
            }

            // ── CRITICAL: Ensure we have a combat weapon, not a tool/pickaxe ──
            EnsureCombatWeapon();

            // ── Stuck detection (melee only — ranged stands still intentionally) ──
            float movedSinceLast = Vector3.Distance(transform.position, _lastStuckPos);
            if (_phase == CombatPhase.Melee)
            {
                if (movedSinceLast < 0.3f && !_character.InAttack())
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
                    ReflectionHelper.ClearAllTargets(_ai);
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

            // ── Retreat check (highest priority) ──
            float healthPct = _character.GetHealthPercentage();
            float staminaPct = _stamina != null ? _stamina.GetStaminaPercentage() : 1f;

            if (_phase == CombatPhase.Retreat)
            {
                if (healthPct >= HealthRecoverPct && staminaPct >= StaminaRecoverPct)
                    ExitCombat("recovered from retreat");
                else
                {
                    UpdateRetreat(target, dt);
                    return;
                }
            }
            else if (healthPct < HealthRetreatPct || staminaPct < StaminaRetreatPct)
            {
                TransitionTo(CombatPhase.Retreat);
                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] Entering RETREAT — hp={healthPct:P0} stam={staminaPct:P0}");
                UpdateRetreat(target, dt);
                return;
            }

            // ── Target classification + weapon selection ──
            float distToTarget = Vector3.Distance(transform.position, target.transform.position);
            bool isFleeingAnimal = target.GetComponent<AnimalAI>() != null;
            bool hasBow = HasBowAndArrows();

            // Skip recently abandoned targets (prevents infinite re-engage on fleeing animals)
            if (_abandonCooldown > 0f && target.GetInstanceID() == _abandonedTargetId)
            {
                ReflectionHelper.ClearAllTargets(_ai);
                if (_phase != CombatPhase.Idle) ExitCombat("target on abandon cooldown");
                return;
            }

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
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] Ignoring fleeing animal \"{target.m_name}\" — no bow+arrows");
                    AbandonTarget(target);
                    ReflectionHelper.ClearAllTargets(_ai);
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
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] Bow→Melee switch — dist={distToTarget:F1} < {MeleeSwitchDistance} " +
                        $"hasBow={hasBow}");
                    RestoreMeleeLoadout();
                    TransitionTo(CombatPhase.Melee);
                    UpdateMelee(target, dt);
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
                    CompanionsPlugin.Log.LogInfo(
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
                    }
                    UpdateMelee(target, dt);
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
                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] WEAPON CHECK — fighting with tool \"{weapon.m_shared.m_name}\" " +
                    $"(type={type} isTool={isTool} isPickaxe={isPickaxe} toolDmgOnly={isToolDamageOnly}) — forcing re-equip");

                // Clear suppress flag that HarvestController may have left on
                if (_setup != null)
                    _setup.SuppressAutoEquip = false;

                // Force auto-equip to pick best combat weapon
                _setup?.SyncEquipmentToInventory();

                var newWeapon = ReflectionHelper.GetRightItem(_humanoid);
                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] After re-equip: \"{newWeapon?.m_shared?.m_name ?? "NONE"}\" " +
                    $"type={newWeapon?.m_shared?.m_itemType}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  Melee Combat
        // ════════════════════════════════════════════════════════════════════

        private void UpdateMelee(Character target, float dt)
        {
            float dist = Vector3.Distance(transform.position, target.transform.position);
            var weapon = _humanoid.GetCurrentWeapon();
            float weaponRange = weapon != null ? weapon.m_shared.m_aiAttackRange : 2f;

            // ── Blocking ──
            bool shouldBlock = ShouldBlock(target, dist, weaponRange);
            if (shouldBlock)
            {
                ReflectionHelper.TrySetBlocking(_character, true);
                ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                _blockTimer += dt;

                // Drop block after delay if no hit landed
                if (_blockTimer > BlockDropDelay)
                {
                    ReflectionHelper.TrySetBlocking(_character, false);
                    _blockTimer = 0f;
                }
                return; // Don't attack while blocking
            }
            else
            {
                if (ReflectionHelper.GetBlocking(_character))
                {
                    ReflectionHelper.TrySetBlocking(_character, false);
                    _blockTimer = 0f;
                }
            }

            // ── Power attack on staggered enemy ──
            if (target.IsStaggering() && dist <= weaponRange + 0.5f &&
                _powerAttackTimer <= 0f && _attackCooldownTimer <= 0f)
            {
                if (weapon != null && weapon.HaveSecondaryAttack())
                {
                    _humanoid.StartAttack(target, true);
                    _powerAttackTimer = PowerAttackCooldown;
                    _attackCooldownTimer = AttackCooldown;
                    CompanionsPlugin.Log.LogInfo(
                        $"[Combat] POWER ATTACK on staggered \"{target.m_name}\" " +
                        $"dist={dist:F1} range={weaponRange:F1}");
                }
            }

            // Normal attacks handled by MonsterAI.DoAttack — we don't interfere
        }

        private bool ShouldBlock(Character target, float dist, float weaponRange)
        {
            // Only block if we have a shield/blocker
            var blocker = ReflectionHelper.GetLeftItem(_humanoid);
            if (blocker == null || blocker.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield)
                return false;

            // Check if target or any nearby enemy is mid-attack
            float blockRange = weaponRange * 1.5f;
            if (blockRange < BlockDetectRange) blockRange = BlockDetectRange;

            foreach (Character c in Character.GetAllCharacters())
            {
                if (c == _character || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;
                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d <= blockRange && c.InAttack())
                    return true;
            }

            return false;
        }

        // ════════════════════════════════════════════════════════════════════
        //  Ranged Combat — manual draw timer + fire (vanilla ChargeStart
        //  doesn't work for player bows on MonsterAI creatures)
        // ════════════════════════════════════════════════════════════════════

        private void UpdateRanged(Character target, float dt)
        {
            float dist = Vector3.Distance(transform.position, target.transform.position);

            // Target out of range — give up and abandon
            if (dist > BowMaxRange)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] Target \"{target.m_name}\" out of bow range ({dist:F1} > {BowMaxRange}) — abandoning");
                AbandonTarget(target);
                ReflectionHelper.ClearAllTargets(_ai);
                ExitCombat("target out of bow range");
                return;
            }

            // Don't fire while moving into position — let vanilla move us closer first
            if (_character.InAttack()) return;

            // Aim at target center mass with velocity lead
            Vector3 aimPoint = target.GetCenterPoint();
            Vector3 targetVel = target.GetVelocity();
            if (targetVel.magnitude > 0.5f)
            {
                float arrowSpeed = 60f;
                float travelTime = dist / arrowSpeed;
                aimPoint += targetVel * travelTime;
            }

            // Face target and stop to aim
            ReflectionHelper.LookAt(_ai, aimPoint);
            _ai.StopMoving();

            // Check if we're looking at the target
            bool onTarget = ReflectionHelper.IsLookingAt(_ai, aimPoint, 15f);

            // Draw timer — only accumulates while facing target
            if (onTarget && _bowFireCooldown <= 0f)
                _bowDrawTimer += dt;
            else if (!onTarget)
                _bowDrawTimer = Mathf.Max(0f, _bowDrawTimer - dt * 2f); // decay if off-target

            // Fire when fully drawn and on target
            if (_bowDrawTimer >= BowDrawTime && onTarget && _bowFireCooldown <= 0f)
            {
                bool fired = _humanoid.StartAttack(target, false);
                _bowDrawTimer = 0f;
                _bowFireCooldown = BowFireInterval;

                CompanionsPlugin.Log.LogInfo(
                    $"[Combat] BOW FIRE at \"{target.m_name}\" dist={dist:F1} " +
                    $"fired={fired} onTarget={onTarget}");
            }
        }

        private void AbandonTarget(Character target)
        {
            if (target == null) return;
            _abandonedTargetId = target.GetInstanceID();
            _abandonCooldown = 30f; // Don't re-engage this target for 30s
            CompanionsPlugin.Log.LogInfo(
                $"[Combat] Abandoned target \"{target.m_name}\" — 30s cooldown");
        }

        // ════════════════════════════════════════════════════════════════════
        //  Retreat + Consumables
        // ════════════════════════════════════════════════════════════════════

        private void UpdateRetreat(Character target, float dt)
        {
            _retreatTimer += dt;

            // Flee away from target
            Vector3 awayDir = (transform.position - target.transform.position).normalized;
            Vector3 fleePoint = transform.position + awayDir * RetreatDistance;
            ReflectionHelper.TryMoveTo(_ai, dt, fleePoint, 2f, true);

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
                        CompanionsPlugin.Log.LogInfo("[Combat] Consumed health mead while retreating");
                    }
                }
                if (staminaPct < StaminaRetreatPct && _consumeTimer <= 0f)
                {
                    if (TryConsumeMead(MeadType.Stamina))
                    {
                        _consumeTimer = ConsumeCooldown;
                        CompanionsPlugin.Log.LogInfo("[Combat] Consumed stamina mead while retreating");
                    }
                }
            }

            // Safety: if retreating too long, disengage
            if (_retreatTimer > 15f)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Combat] Retreat timeout ({_retreatTimer:F1}s) — disengaging from \"{target.m_name}\"");
                ReflectionHelper.ClearAllTargets(_ai);
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
                CompanionsPlugin.Log.LogInfo(
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
                CompanionsPlugin.Log.LogInfo(
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
            CompanionsPlugin.Log.LogInfo(
                $"[Combat] Restored melee — now wielding \"{newRight?.m_shared?.m_name ?? "NONE"}\"");
        }

        /// <summary>
        /// Clean exit from any combat phase back to idle.
        /// Ensures weapons are restored and suppress flags cleared.
        /// </summary>
        private void ExitCombat(string reason)
        {
            var oldPhase = _phase;

            // Stop charging if active
            if (_ai.IsCharging())
                _ai.ChargeStop();

            if (_bowEquipped)
                RestoreMeleeLoadout();

            // Always clear suppress in case HarvestController left it on
            if (_setup != null)
                _setup.SuppressAutoEquip = false;

            TransitionTo(CombatPhase.Idle);
            _stuckTimer = 0f;
            _retreatTimer = 0f;
            _bowDrawTimer = 0f;
            _bowFireCooldown = 0f;

            CompanionsPlugin.Log.LogInfo(
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
                CompanionsPlugin.Log.LogInfo(
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
            CompanionsPlugin.Log.LogInfo($"[Combat] Phase: {_phase} → {newPhase}");
            _phase = newPhase;
            _blockTimer = 0f;
            if (newPhase != CombatPhase.Retreat) _retreatTimer = 0f;
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

            CompanionsPlugin.Log.LogInfo(
                $"[Combat] ♥ phase={_phase} " +
                $"target=\"{target?.m_name ?? "null"}\" dist={distToTarget:F1} " +
                $"follow=\"{followTarget?.name ?? "null"}\" followDist={distToFollow:F1} " +
                $"weapon=\"{weapon?.m_shared?.m_name ?? "null"}\" " +
                $"right=\"{rightItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"left=\"{leftItem?.m_shared?.m_name ?? "NONE"}\" " +
                $"bowEquipped={_bowEquipped} bowDraw={_bowDrawTimer:F1}/{BowDrawTime:F1} " +
                $"bowCD={_bowFireCooldown:F1} charging={_ai.IsCharging()} " +
                $"inAttack={_character.InAttack()} " +
                $"hp={_character.GetHealthPercentage():P0} " +
                $"stam={(_stamina != null ? _stamina.GetStaminaPercentage() : 1f):P0} " +
                $"stuckTimer={_stuckTimer:F1} atkCD={_attackCooldownTimer:F1} " +
                $"suppress={_setup?.SuppressAutoEquip ?? false}");
        }
    }
}
