using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Higher-level combat decision-making: emergency retreat, consumable use,
    /// player threat assist, low-health target focus, and adaptive weapon switching.
    ///
    /// Bug #2 fix: Retreat uses flag-based coordination with HarvestController.
    /// Bug #3 fix: ThreatAssist and LowHealthFocus skip during active harvesting.
    /// Bug #8 fix: Consumable matching uses status effect types, not keyword substrings.
    /// </summary>
    internal class CombatBrain
    {
        internal bool IsRetreating => _retreatActive;

        private readonly Character _character;
        private readonly Humanoid _humanoid;
        private readonly MonsterAI _ai;
        private readonly ZNetView _nview;
        private readonly CompanionStamina _stamina;
        private readonly CompanionSetup _setup;
        private readonly CompanionTalk _talk;
        private readonly Transform _transform;

        private float _retreatLockTimer;
        private bool  _retreatActive;

        private float _assistScanTimer;
        private float _focusScanTimer;
        private float _weaponTacticTimer;
        private float _consumableScanTimer;
        private float _healCooldown;
        private float _staminaCooldown;

        // Retreat
        private const float RetreatStartHpPct     = 0.35f;
        private const float RetreatRecoverHpPct    = 0.65f;
        private const float RetreatEnemyRange      = 16f;
        private const float RetreatLockDuration    = 3f;

        // Consumables
        private const float ConsumableScanInterval = 0.5f;
        private const float HealHealthPct          = 0.55f;
        private const float StamPctThreshold       = 0.25f;
        private const float HealCooldownTime       = 25f;
        private const float StamCooldownTime       = 18f;

        // Weapon tactics
        private const float AssistScanInterval     = 0.3f;
        private const float FocusScanInterval      = 0.6f;
        private const float WeaponTacticInterval   = 0.8f;
        private const float BowPreferDist          = 10f;
        private const float MeleePreferDist        = 5f;

        // Combat positioning
        private float _repositionTimer;
        private const float RepositionInterval     = 0.4f;
        private const float OptimalMeleeRange      = 2.5f;
        private const float TooCloseRange          = 1.2f;

        /// <summary>
        /// Accessor set by CompanionBrain so CombatBrain can check harvest state
        /// without a direct dependency on HarvestController.
        /// </summary>
        internal System.Func<bool> IsHarvestActive;

        internal CombatBrain(Character character, Humanoid humanoid, MonsterAI ai,
                             ZNetView nview, CompanionStamina stamina,
                             CompanionSetup setup, CompanionTalk talk, Transform transform)
        {
            _character = character;
            _humanoid  = humanoid;
            _ai        = ai;
            _nview     = nview;
            _stamina   = stamina;
            _setup     = setup;
            _talk      = talk;
            _transform = transform;
        }

        internal void Update(float dt, EnemyCache enemies, CombatState combat)
        {
            if (_ai == null || _humanoid == null || _character == null) return;

            if (_healCooldown > 0f) _healCooldown -= dt;
            if (_staminaCooldown > 0f) _staminaCooldown -= dt;

            UpdateEmergencyRetreat(dt, enemies);
            UpdateCombatConsumables(dt, combat);

            if (_retreatActive) return;

            UpdatePlayerThreatAssist(dt, enemies);
            UpdateLowHealthTargetFocus(dt, enemies, combat);
            UpdateAdaptiveCombatWeapon(dt, enemies, combat);
            UpdateCombatPositioning(dt, enemies, combat);
        }

        internal void ResetState()
        {
            _retreatActive = false;
            _retreatLockTimer = 0f;
            if (_stamina != null) _stamina.IsRunning = false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Emergency Retreat
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateEmergencyRetreat(float dt, EnemyCache enemies)
        {
            var player = Player.m_localPlayer;
            if (player == null || _nview == null || _nview.GetZDO() == null)
            {
                _retreatActive = false;
                _retreatLockTimer = 0f;
                if (_stamina != null) _stamina.IsRunning = false;
                return;
            }

            float hpPct = _character.GetHealthPercentage();
            bool enemyPressure = enemies.NearestEnemy != null &&
                                 enemies.NearestEnemyDist < RetreatEnemyRange;

            int mode = _nview.GetZDO().GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            bool inStayMode = mode == CompanionSetup.ModeStay;
            bool inHarvestMode = mode >= CompanionSetup.ModeGatherWood &&
                                 mode <= CompanionSetup.ModeGatherOre;

            // Stay mode never forces retreat-follow
            if (inStayMode && _retreatActive)
            {
                _retreatActive = false;
                _retreatLockTimer = 0f;
                if (_stamina != null) _stamina.IsRunning = false;
                return;
            }

            // Start retreat
            if (!_retreatActive && !inStayMode && enemyPressure && hpPct <= RetreatStartHpPct)
            {
                _retreatActive = true;
                _retreatLockTimer = RetreatLockDuration;

                // Bug #2 fix: during harvest, only set the flag.
                // HarvestController will check IsRetreating and pause itself.
                if (!inHarvestMode)
                {
                    _ai.SetFollowTarget(player.gameObject);
                    ReflectionHelper.ClearAllTargets(_ai);
                }

                _talk?.Say("Falling back!");
            }

            if (!_retreatActive) return;

            _retreatLockTimer -= dt;

            // During retreat in non-harvest modes, keep following player
            if (!inHarvestMode)
            {
                _ai.SetFollowTarget(player.gameObject);
                ReflectionHelper.ClearAllTargets(_ai);
            }

            if (_stamina != null) _stamina.IsRunning = true;

            float playerDist = Vector3.Distance(
                _transform.position, player.transform.position);
            bool recovered = hpPct >= RetreatRecoverHpPct;
            bool safe = !enemyPressure || playerDist < 4f;

            if (_retreatLockTimer <= 0f && recovered && safe)
            {
                _retreatActive = false;
                if (_stamina != null) _stamina.IsRunning = false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Combat Consumables
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCombatConsumables(float dt, CombatState combat)
        {
            _consumableScanTimer -= dt;
            if (_consumableScanTimer > 0f) return;
            _consumableScanTimer = ConsumableScanInterval;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            float hpPct = _character.GetHealthPercentage();
            if (hpPct <= HealHealthPct && _healCooldown <= 0f)
            {
                if (TryUseConsumable(inv, ConsumableType.Healing))
                {
                    _healCooldown = HealCooldownTime;
                    return;
                }
            }

            if (_stamina != null &&
                _stamina.GetStaminaPercentage() <= StamPctThreshold &&
                _staminaCooldown <= 0f &&
                (combat.InCombat || _retreatActive || _stamina.IsRunning))
            {
                if (TryUseConsumable(inv, ConsumableType.Stamina))
                    _staminaCooldown = StamCooldownTime;
            }
        }

        private enum ConsumableType { Healing, Stamina }

        /// <summary>
        /// Bug #8 fix: Uses status effect type checking and food stat heuristics
        /// instead of fragile keyword substring matching.
        /// </summary>
        private bool TryUseConsumable(Inventory inv, ConsumableType type)
        {
            if (inv == null) return false;

            ItemDrop.ItemData best = null;
            float bestScore = float.MinValue;

            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) continue;
                if (!_humanoid.CanConsumeItem(item, checkWorldLevel: false)) continue;

                // Must be a combat consumable (mead/potion type)
                if (!IsCombatConsumable(item)) continue;

                float score = ScoreConsumable(item, type);
                if (score <= 0f) continue;

                if (score > bestScore)
                {
                    best = item;
                    bestScore = score;
                }
            }

            if (best == null) return false;
            _humanoid.UseItem(inv, best, true);
            return true;
        }

        private static bool IsCombatConsumable(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;

            // Check if it has a status effect (meads/potions always do)
            if (item.m_shared.m_consumeStatusEffect != null)
                return true;

            // Fall back to checking if the food values indicate a mead/potion pattern:
            // Meads typically have high single-stat values with no food duration
            // or have specific food values that indicate they're not regular meals
            string name = (item.m_shared.m_name ?? "").ToLowerInvariant();
            return name.Contains("mead") ||
                   name.Contains("potion") ||
                   name.Contains("brew") ||
                   name.Contains("elixir") ||
                   name.Contains("tonic");
        }

        private static float ScoreConsumable(ItemDrop.ItemData item, ConsumableType type)
        {
            if (item == null || item.m_shared == null) return 0f;

            float score = 0f;

            // Check status effect for type matching
            var se = item.m_shared.m_consumeStatusEffect;
            if (se != null)
            {
                string seName = (se.name ?? "").ToLowerInvariant();
                switch (type)
                {
                    case ConsumableType.Healing:
                        if (seName.Contains("heal") || seName.Contains("health") || seName.Contains("hp"))
                            score += 4f;
                        if (seName.Contains("stamina")) score -= 2f;
                        break;
                    case ConsumableType.Stamina:
                        if (seName.Contains("stamina") || seName.Contains("tasty") || seName.Contains("eitr"))
                            score += 4f;
                        if (seName.Contains("heal") || seName.Contains("health"))
                            score -= 2f;
                        break;
                }
            }

            // Food stat heuristics as fallback
            float food = item.m_shared.m_food;
            float foodStam = item.m_shared.m_foodStamina;
            float foodEitr = item.m_shared.m_foodEitr;

            switch (type)
            {
                case ConsumableType.Healing:
                    if (food > foodStam && food > foodEitr) score += 2f;
                    score += food * 0.01f;
                    break;
                case ConsumableType.Stamina:
                    if (foodStam > food && foodStam > foodEitr) score += 2f;
                    if (foodEitr > food) score += 1f;
                    score += foodStam * 0.01f;
                    break;
            }

            if (item.m_quality > 1) score += 0.5f;
            if (item.m_stack > 1) score += 0.5f;

            return score;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Player Threat Assist
        // ══════════════════════════════════════════════════════════════════════

        private void UpdatePlayerThreatAssist(float dt, EnemyCache enemies)
        {
            _assistScanTimer -= dt;
            if (_assistScanTimer > 0f) return;
            _assistScanTimer = AssistScanInterval;

            // Bug #3 fix: don't set combat targets during active harvest
            if (IsHarvestActive?.Invoke() == true) return;

            if (Player.m_localPlayer == null) return;
            if (enemies.BestThreatAssistTarget == null) return;

            SetCombatTarget(enemies.BestThreatAssistTarget);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Low-Health Target Focus
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateLowHealthTargetFocus(float dt, EnemyCache enemies, CombatState combat)
        {
            if (!combat.InCombat) return;

            // Bug #3 fix: don't set combat targets during active harvest
            if (IsHarvestActive?.Invoke() == true) return;

            _focusScanTimer -= dt;
            if (_focusScanTimer > 0f) return;
            _focusScanTimer = FocusScanInterval;

            if (enemies.BestLowHealthTarget == null) return;

            SetCombatTarget(enemies.BestLowHealthTarget);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Adaptive Weapon Switching
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateAdaptiveCombatWeapon(float dt, EnemyCache enemies, CombatState combat)
        {
            _weaponTacticTimer -= dt;
            if (_weaponTacticTimer > 0f) return;
            _weaponTacticTimer = WeaponTacticInterval;

            if (_character.InAttack()) return;
            if (_nview == null || _nview.GetZDO() == null) return;

            int mode = _nview.GetZDO().GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode >= CompanionSetup.ModeGatherWood &&
                mode <= CompanionSetup.ModeGatherOre)
                return;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            var current = _humanoid.GetCurrentWeapon();
            var bestMelee = FindBestCombatWeapon(inv, preferBow: false);
            var bestBow = FindBestCombatWeapon(inv, preferBow: true);

            // Never fight with tools if a combat weapon is available
            if (current != null && current.m_shared != null &&
                (current.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Tool ||
                 current.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Torch) &&
                bestMelee != null)
            {
                _humanoid.EquipItem(bestMelee, true);
                return;
            }

            float enemyDist = enemies.NearestEnemyDist;
            if (enemies.NearestEnemy == null && !combat.InCombat) return;

            if (enemyDist >= BowPreferDist && bestBow != null && HasAmmoFor(bestBow, inv))
            {
                if (current != bestBow) _humanoid.EquipItem(bestBow, true);
                return;
            }

            if (enemyDist <= MeleePreferDist && bestMelee != null && current != bestMelee)
                _humanoid.EquipItem(bestMelee, true);
        }

        private ItemDrop.ItemData FindBestCombatWeapon(Inventory inv, bool preferBow)
        {
            ItemDrop.ItemData best = null;
            float bestDmg = float.MinValue;

            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;

                var type = item.m_shared.m_itemType;
                bool isBow = type == ItemDrop.ItemData.ItemType.Bow;
                bool isMelee = type == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                               type == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                               type == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
                if (preferBow ? !isBow : !isMelee) continue;

                float dmg = item.GetDamage().GetTotalDamage();
                if (dmg > bestDmg)
                {
                    bestDmg = dmg;
                    best = item;
                }
            }
            return best;
        }

        private bool HasAmmoFor(ItemDrop.ItemData bow, Inventory inv)
        {
            if (bow == null || bow.m_shared == null) return false;

            string ammoType = bow.m_shared.m_ammoType;
            var equippedAmmo = _humanoid.GetAmmoItem();
            if (equippedAmmo != null && equippedAmmo.m_stack > 0)
            {
                if (string.IsNullOrEmpty(ammoType) ||
                    equippedAmmo.m_shared == null ||
                    string.IsNullOrEmpty(equippedAmmo.m_shared.m_ammoType) ||
                    equippedAmmo.m_shared.m_ammoType == ammoType)
                    return true;
            }

            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null || item.m_stack <= 0) continue;
                var type = item.m_shared.m_itemType;
                if (type != ItemDrop.ItemData.ItemType.Ammo &&
                    type != ItemDrop.ItemData.ItemType.AmmoNonEquipable)
                    continue;

                if (string.IsNullOrEmpty(ammoType) ||
                    string.IsNullOrEmpty(item.m_shared.m_ammoType) ||
                    item.m_shared.m_ammoType == ammoType)
                    return true;
            }
            return false;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Combat Positioning
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCombatPositioning(float dt, EnemyCache enemies, CombatState combat)
        {
            if (!combat.InCombat) return;
            if (_character.InAttack() || _character.IsStaggering()) return;

            _repositionTimer -= dt;
            if (_repositionTimer > 0f) return;
            _repositionTimer = RepositionInterval;

            var target = ReflectionHelper.GetTargetCreature(_ai);
            if (target == null || target.IsDead()) return;

            float dist = Vector3.Distance(_transform.position, target.transform.position);
            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon == null) return;

            bool isBow = weapon.m_shared != null &&
                         weapon.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;

            if (isBow && dist < MeleePreferDist)
            {
                // Ranged: clear target briefly so MonsterAI stops approach,
                // then re-set it after one frame — MonsterAI will re-path from
                // the new position. This gives the companion time to back away
                // via normal pathfinding away from obstacles.
                _ai.SetFollowTarget(Player.m_localPlayer != null
                    ? Player.m_localPlayer.gameObject : null);
                return;
            }

            // Melee positioning: let MonsterAI handle all movement (it uses
            // circleTarget behavior natively). We only nudge the companion
            // to look at the enemy when idle in optimal range so attacks land.
            if (dist > TooCloseRange && dist < OptimalMeleeRange * 1.5f)
            {
                Vector3 toTarget = target.transform.position - _transform.position;
                toTarget.y = 0f;
                if (toTarget.sqrMagnitude > 0.01f)
                    _character.SetLookDir(toTarget.normalized);
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private void SetCombatTarget(Character target)
        {
            if (_ai == null) return;
            ReflectionHelper.TrySetTargetCreature(_ai, target);
            if (target != null)
            {
                ReflectionHelper.TrySetTargetStatic(_ai, null);
                _ai.Alert();
            }
        }
    }
}
