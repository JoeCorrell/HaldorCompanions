using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Standalone combat AI that activates when threats are detected.
    /// Ported directly from vanilla MonsterAI's combat loop (UpdateTarget, circling,
    /// intercept prediction, SelectBestAttack, DoAttack) with Renegade Vikings
    /// configuration parameters.
    ///
    /// Architecture: CompanionAI (BaseAI subclass) handles all non-combat behavior.
    /// When it detects a threat, it calls Engage() on this component, which takes
    /// over the AI loop. When the target dies or is lost, Disengage() returns
    /// control to CompanionAI.
    ///
    /// This is a plain MonoBehaviour — NOT a BaseAI subclass. It delegates all
    /// movement, pathfinding, and perception to CompanionAI through its exposed
    /// internal wrappers.
    /// </summary>
    public class CompanionCombatAI : MonoBehaviour
    {
        // ══════════════════════════════════════════════════════════════════════
        //  Configuration — matches Renegade Vikings SetupMonsterAI()
        // ══════════════════════════════════════════════════════════════════════

        private const float AlertRange           = 9999f;
        private const float InterceptTimeMin     = 0f;
        private const float InterceptTimeMax     = 4f;
        private const float CircleTargetInterval = 3f;
        private const float CircleTargetDuration = 1f;
        private const float CircleTargetDistance  = 4f;
        private const float UpdateWeaponInterval = 1f;
        private const float GiveUpTime           = 30f;
        private const float UpdateTargetIntervalNear = 2f;
        private const float UpdateTargetIntervalFar  = 6f;
        private const float UnableToAttackDuration   = 15f;

        // ══════════════════════════════════════════════════════════════════════
        //  State
        // ══════════════════════════════════════════════════════════════════════

        private CompanionAI _ai;
        private Character _character;
        private Humanoid _humanoid;
        private CompanionSetup _setup;

        // Combat targets (owned by this component while engaged)
        private Character _targetCreature;
        private StaticTarget _targetStatic;
        private Vector3 _lastKnownTargetPos;
        private bool _beenAtLastPos;

        // Timers
        private float _timeSinceAttacking;
        private float _timeSinceSensedTarget;
        private float _updateTargetTimer;
        private float _updateWeaponTimer;
        private float _interceptTime;
        private float _pauseTimer;
        private float _unableToAttackTargetTimer;

        // Weapon switch anti-oscillation (Balanced stance only)
        private float _weaponSwitchCooldown;

        // Active flag — when true, UpdateCombat() runs and CompanionAI yields
        private bool _engaged;
        internal bool IsEngaged => _engaged;

        // Combat stance — refreshed each Engage and frame from ZDO
        private int _stance;

        // Blocking/parry — reactive: block when enemy swings, release after
        private float _blockHoldTimer;
        private float _enemySwingTimer;     // time enemy has been in current attack
        private const float ParryDelay = 0.2f; // raise shield this late into the swing

        // Parry counter-attack — after a successful parry, queue 2 fast attacks
        private int _parryAttackCount;

        // Logging
        private float _logTimer;
        private const float LogInterval = 3f;

        // The creature that was passed to Engage() or set via OnDamaged
        internal Character TargetCreature => _targetCreature;

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _ai = GetComponent<CompanionAI>();
            _character = GetComponent<Character>();
            _humanoid = GetComponent<Humanoid>();
            _setup = GetComponent<CompanionSetup>();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API — Engage / Disengage
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Flip the combat switch ON. Called by CompanionAI when a threat is detected.
        /// </summary>
        internal void Engage(Character target)
        {
            if (target == null || target.IsDead()) return;

            // Passive stance: never engage in combat
            _stance = _setup?.GetCombatStance() ?? CompanionSetup.StanceBalanced;
            if (_stance == CompanionSetup.StancePassive) return;

            _targetCreature = target;
            _targetStatic = null;
            _lastKnownTargetPos = target.transform.position;
            _beenAtLastPos = false;
            _timeSinceAttacking = 0f;
            _timeSinceSensedTarget = 0f;
            _updateTargetTimer = 0f;
            _updateWeaponTimer = 0f;
            _pauseTimer = Random.Range(0f, CircleTargetInterval);
            _interceptTime = Random.Range(InterceptTimeMin, InterceptTimeMax);
            _unableToAttackTargetTimer = 0f;
            _parryAttackCount = 0;
            _engaged = true;

            _ai.SetAlerted(true);

            CompanionsPlugin.Log.LogInfo(
                $"[CombatAI] Engaged — target=\"{target.m_name}\" " +
                $"dist={Vector3.Distance(transform.position, target.transform.position):F1}m");
        }

        /// <summary>
        /// Flip the combat switch OFF. Returns control to CompanionAI.
        /// </summary>
        internal void Disengage()
        {
            if (!_engaged) return;

            string targetName = _targetCreature != null ? _targetCreature.m_name : "none";
            _targetCreature = null;
            _targetStatic = null;
            _engaged = false;

            // Sync targets back to CompanionAI (cleared)
            _ai.m_targetCreature = null;
            _ai.m_targetStatic = null;

            _ai.SetAlerted(false);
            _ai.StopMoving();
            _ai.ChargeStop();
            ClearBlock();
            _parryAttackCount = 0;

            CompanionsPlugin.Log.LogInfo(
                $"[CombatAI] Disengaged — was targeting \"{targetName}\"");
        }

        /// <summary>
        /// Called by CompanionAI.OnDamaged when hit while already in combat.
        /// Switches target to the attacker if we don't have one.
        /// </summary>
        internal void OnDamaged(Character attacker)
        {
            if (!_engaged) return;
            if (attacker == null) return;
            if (attacker.IsPlayer() && _character.IsTamed()) return;

            // Only switch if no current target or current target is dead
            if (_targetCreature == null || _targetCreature.IsDead())
            {
                _targetCreature = attacker;
                _lastKnownTargetPos = attacker.transform.position;
                _beenAtLastPos = false;
                _targetStatic = null;
                _timeSinceSensedTarget = 0f;

                CompanionsPlugin.Log.LogInfo(
                    $"[CombatAI] Target switch (damaged) → \"{attacker.m_name}\"");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Main Combat Loop — called by CompanionAI.UpdateAI when engaged
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs every frame while engaged. Returns true if combat consumed the
        /// AI tick (CompanionAI should return true from UpdateAI).
        /// </summary>
        internal bool UpdateCombat(float dt)
        {
            if (!_engaged) return false;
            if (_humanoid == null) return false;

            // Refresh stance each frame (can change mid-combat via radial)
            _stance = _setup?.GetCombatStance() ?? CompanionSetup.StanceBalanced;
            if (_stance == CompanionSetup.StancePassive)
            {
                Disengage();
                return false;
            }

            // Tombstone recovery takes absolute priority — companion must retrieve
            // their gear even in the middle of a fight.
            if (_ai.IsRecoveringTombstone)
            {
                Disengage();
                return false;
            }

            // Update target finding + validation
            UpdateTarget(dt, out bool canHearTarget, out bool canSeeTarget);

            // No targets at all — disengage
            if (_targetCreature == null && _targetStatic == null)
            {
                Disengage();
                return false;
            }

            // Sync targets to CompanionAI so Attack.GetProjectileSpawnPoint() can
            // aim projectiles via m_baseAI.GetTargetCreature()
            _ai.m_targetCreature = _targetCreature;
            _ai.m_targetStatic = _targetStatic;

            // Reactive blocking — raise shield when enemy swings
            UpdateBlocking(dt);

            // Periodic state dump
            _logTimer += dt;
            if (_logTimer >= LogInterval)
            {
                _logTimer = 0f;
                string tgtName = _targetCreature != null ? _targetCreature.m_name : "static";
                float tgtDist = _targetCreature != null
                    ? Vector3.Distance(transform.position, _targetCreature.transform.position) : -1f;
                string wpnName = _humanoid.GetCurrentWeapon()?.m_shared?.m_name ?? "none";
                CompanionsPlugin.Log.LogInfo(
                    $"[CombatAI] State: target=\"{tgtName}\" dist={tgtDist:F1}m " +
                    $"weapon=\"{wpnName}\" see={canSeeTarget} hear={canHearTarget} " +
                    $"alerted={_ai.IsAlerted()} charging={_ai.IsCharging()}");
            }

            // Circling behavior — periodically strafe around target
            // Suppressed while actively blocking (stand ground to parry)
            // Suppressed during parry counter-attacks (press the advantage)
            bool isBlocking = ReflectionHelper.GetBlocking(_character);
            if (CircleTargetInterval > 0f && _targetCreature != null && !isBlocking
                && _parryAttackCount <= 0)
            {
                _pauseTimer += dt;
                if (_pauseTimer > CircleTargetInterval)
                {
                    if (_pauseTimer > CircleTargetInterval + CircleTargetDuration)
                    {
                        _pauseTimer = Random.Range(0f, CircleTargetInterval / 10f);
                    }
                    _ai.RandomMovementAroundPoint(dt, _targetCreature.transform.position,
                        CircleTargetDistance, _ai.IsAlerted());
                    return true;
                }
            }

            // Select best weapon
            ItemDrop.ItemData weapon = SelectBestAttack(dt);
            bool weaponIntervalOk = weapon != null &&
                Time.time - weapon.m_lastAttackTime > weapon.m_shared.m_aiAttackInterval;
            float minInterval = ModConfig.AttackCooldown.Value;
            bool charIntervalOk = _character.GetTimeSinceLastAttack() >= minInterval;
            bool parryCounter = _parryAttackCount > 0;
            bool canAttack = weapon != null && !_ai.IsTakingOff()
                && (parryCounter || (weaponIntervalOk && charIntervalOk));

            // Charge attack initiation (MonsterAI lines 451-454)
            if (!_ai.IsCharging() && (_targetStatic != null || _targetCreature != null)
                && weapon != null && canAttack && !_character.InAttack()
                && weapon.m_shared.m_attack != null && !weapon.m_shared.m_attack.IsDone()
                && !string.IsNullOrEmpty(weapon.m_shared.m_attack.m_chargeAnimationBool))
            {
                _ai.ChargeStart(weapon.m_shared.m_attack.m_chargeAnimationBool);
                CompanionsPlugin.Log.LogInfo(
                    $"[CombatAI] ChargeStart — anim=\"{weapon.m_shared.m_attack.m_chargeAnimationBool}\"");
            }

            // Circulate while charging — melee only (RV sets
            // circulateWhileCharging=false for ranged weapon users)
            bool isRangedWeapon = weapon != null && weapon.m_shared.m_aiAttackRange > 10f;
            if (_targetCreature != null && weapon != null && !canAttack
                && !_character.InAttack() && !isRangedWeapon)
            {
                // Keep circulating while blocking — face enemy handled in UpdateBlocking
                if (isBlocking)
                {
                    Vector3 circPointB = _targetCreature.transform.position;
                    _ai.RandomMovementAroundPoint(dt, circPointB,
                        _ai.m_randomMoveRange > 0f ? _ai.m_randomMoveRange : 5f,
                        _ai.IsAlerted());
                    return true;
                }
                Vector3 circPoint = _targetCreature.transform.position;
                _ai.RandomMovementAroundPoint(dt, circPoint,
                    _ai.m_randomMoveRange > 0f ? _ai.m_randomMoveRange : 5f,
                    _ai.IsAlerted());
                return true;
            }

            // No target or no weapon — idle
            if ((_targetStatic == null && _targetCreature == null) || weapon == null)
            {
                _ai.ChargeStop();
                Disengage();
                return false;
            }

            // === Attack logic (ported from MonsterAI.UpdateAI lines 474-596) ===

            if (weapon.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Enemy)
            {
                if (_targetCreature != null)
                {
                    if (canHearTarget || canSeeTarget)
                    {
                        _beenAtLastPos = false;
                        _lastKnownTargetPos = _targetCreature.transform.position;
                        float dist = Vector3.Distance(_lastKnownTargetPos, transform.position)
                            - _targetCreature.GetRadius();
                        float stealthRange = AlertRange * _targetCreature.GetStealthFactor();

                        if (canSeeTarget && dist < stealthRange)
                        {
                            _ai.SetAlerted(true);
                        }

                        bool inRange = dist < weapon.m_shared.m_aiAttackRange;

                        if (!inRange || !canSeeTarget ||
                            weapon.m_shared.m_aiAttackRangeMin < 0f || !_ai.IsAlerted())
                        {
                            // Move toward target with intercept prediction
                            Vector3 velocity = _targetCreature.GetVelocity();
                            Vector3 intercept = velocity * _interceptTime;
                            Vector3 moveTarget = _lastKnownTargetPos;
                            if (dist > intercept.magnitude / 4f)
                            {
                                moveTarget += velocity * _interceptTime;
                            }
                            _ai.MoveToPoint(dt, moveTarget, 0f, true);

                            if (_timeSinceAttacking > UnableToAttackDuration)
                            {
                                _unableToAttackTargetTimer = UnableToAttackDuration;
                            }
                        }
                        else
                        {
                            _ai.StopMoving();
                        }

                        // In range + can see + alerted → attack
                        // Skip attack while parrying: if we attack during the parry
                        // frame, InAttack() becomes true and immediately clears the
                        // block before the incoming hit registers.
                        if (inRange && canSeeTarget && _ai.IsAlerted() && !isBlocking)
                        {
                            _ai.LookAtPoint(_targetCreature.GetTopPoint());
                            if (canAttack && _ai.IsLookingAtPoint(
                                _lastKnownTargetPos,
                                weapon.m_shared.m_aiAttackMaxAngle,
                                weapon.m_shared.m_aiInvertAngleCheck))
                            {
                                if (DoAttack(_targetCreature))
                                {
                                    if (_parryAttackCount > 0) _parryAttackCount--;
                                    CompanionsPlugin.Log.LogInfo(
                                        $"[CombatAI] Attack! weapon=\"{weapon.m_shared.m_name}\" " +
                                        $"dist={Vector3.Distance(transform.position, _targetCreature.transform.position):F1}m" +
                                        (_parryAttackCount > 0 ? $" [parry counter, {_parryAttackCount} left]" : ""));
                                }
                            }
                        }
                    }
                    else
                    {
                        // Lost sight — move to last known position
                        CompanionsPlugin.Log.LogInfo("[CombatAI] Lost sight — moving to last known pos");
                        _ai.ChargeStop();
                        if (_beenAtLastPos)
                        {
                            _ai.RandomMovement(dt, _lastKnownTargetPos);
                            if (_timeSinceAttacking > UnableToAttackDuration)
                            {
                                _unableToAttackTargetTimer = UnableToAttackDuration;
                            }
                        }
                        else if (_ai.MoveToPoint(dt, _lastKnownTargetPos, 0f, true))
                        {
                            _beenAtLastPos = true;
                        }
                    }
                }
            }
            else if (weapon.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.FriendHurt
                || weapon.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.Friend)
            {
                // Heal/support weapon — find hurt friend
                Character friend = (weapon.m_shared.m_aiTargetType == ItemDrop.ItemData.AiTarget.FriendHurt)
                    ? _ai.HaveHurtFriendInRange(_ai.m_viewRange)
                    : _ai.HaveFriendInRange(_ai.m_viewRange);

                if (friend != null)
                {
                    float friendDist = Vector3.Distance(friend.transform.position, transform.position);
                    if (friendDist < weapon.m_shared.m_aiAttackRange)
                    {
                        if (canAttack)
                        {
                            _ai.StopMoving();
                            _ai.LookAtPoint(friend.transform.position);
                            DoAttack(friend);
                        }
                        else
                        {
                            _ai.RandomMovement(dt, friend.transform.position);
                        }
                    }
                    else
                    {
                        _ai.MoveToPoint(dt, friend.transform.position, 0f, true);
                    }
                }
                else
                {
                    _ai.RandomMovement(dt, transform.position);
                }
            }

            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Target Finding — ported from MonsterAI.UpdateTarget()
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateTarget(float dt, out bool canHearTarget, out bool canSeeTarget)
        {
            _unableToAttackTargetTimer -= dt;
            _updateTargetTimer -= dt;

            if (_updateTargetTimer <= 0f && !_character.InAttack())
            {
                bool nearPlayer = Player.IsPlayerInRange(transform.position, 50f);
                _updateTargetTimer = nearPlayer
                    ? UpdateTargetIntervalNear : UpdateTargetIntervalFar;

                // Find new enemy (skip deer)
                Character enemy = _ai.FindNearbyEnemy();
                if (enemy != null && IsPassiveAnimal(enemy))
                    enemy = null;
                if (enemy != null)
                {
                    if (_targetCreature != enemy)
                    {
                        CompanionsPlugin.Log.LogInfo(
                            $"[CombatAI] New target: \"{enemy.m_name}\" " +
                            $"dist={Vector3.Distance(transform.position, enemy.transform.position):F1}m");
                    }
                    _targetCreature = enemy;
                    _targetStatic = null;
                }
            }

            // Tamed alert range enforcement — don't chase beyond alert range
            // from follow target or patrol point
            if (_targetCreature != null && _character.IsTamed())
            {
                Vector3 anchor;
                bool hasAnchor = false;

                if (_ai.GetPatrolPoint(out var patrolPt))
                {
                    anchor = patrolPt;
                    hasAnchor = true;
                }
                else
                {
                    var follow = _ai.GetFollowTarget();
                    if (follow != null)
                    {
                        anchor = follow.transform.position;
                        hasAnchor = true;
                    }
                    else
                    {
                        anchor = Vector3.zero;
                    }
                }

                if (hasAnchor)
                {
                    float distFromAnchor = Vector3.Distance(
                        _targetCreature.transform.position, anchor);
                    if (distFromAnchor > AlertRange)
                    {
                        _targetCreature = null;
                    }
                }
            }

            // Validate current creature target
            if (_targetCreature != null)
            {
                if (_targetCreature.IsDead())
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[CombatAI] Target \"{_targetCreature.m_name}\" died");
                    _targetCreature = null;
                }
                else if (!_ai.IsEnemy(_targetCreature))
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[CombatAI] Target \"{_targetCreature.m_name}\" no longer enemy");
                    _targetCreature = null;
                }
            }

            // Sense tracking
            canHearTarget = false;
            canSeeTarget = false;

            if (_targetCreature != null)
            {
                canHearTarget = _ai.CanHearTarget(_targetCreature);
                canSeeTarget = _ai.CanSeeTarget(_targetCreature);
                if (canSeeTarget || canHearTarget)
                {
                    _timeSinceSensedTarget = 0f;
                }
            }

            _timeSinceSensedTarget += dt;

            // Give-up logic
            if (_ai.IsAlerted() || _targetCreature != null)
            {
                _timeSinceAttacking += dt;
                float giveUpAttackTime = 60f;

                if (_timeSinceSensedTarget > GiveUpTime
                    || _timeSinceAttacking > giveUpAttackTime)
                {
                    CompanionsPlugin.Log.LogInfo(
                        $"[CombatAI] Giving up — sensed={_timeSinceSensedTarget:F1}s " +
                        $"attacked={_timeSinceAttacking:F1}s");
                    _ai.SetAlerted(false);
                    _targetCreature = null;
                    _targetStatic = null;
                    _timeSinceAttacking = 0f;
                    _updateTargetTimer = 5f;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Weapon Selection — ported from MonsterAI.SelectBestAttack()
        // ══════════════════════════════════════════════════════════════════════

        // Minimum seconds between weapon switches in Balanced stance — prevents
        // bow/melee oscillation when distance hovers near the switch threshold.
        // Ranged/Melee stances bypass this entirely and directly enforce the weapon.
        private const float WeaponSwitchCooldown = 4f;

        private ItemDrop.ItemData SelectBestAttack(float dt)
        {
            if (_targetCreature != null || _targetStatic != null)
            {
                _updateWeaponTimer -= dt;
                if (_weaponSwitchCooldown > 0f) _weaponSwitchCooldown -= dt;

                if (_updateWeaponTimer <= 0f && !_character.InAttack()
                    && _weaponSwitchCooldown <= 0f)
                {
                    _updateWeaponTimer = UpdateWeaponInterval;
                    string prevWeapon = _humanoid.GetCurrentWeapon()?.m_shared?.m_name;

                    bool stanceForced = false;
                    var equipped = _humanoid.GetCurrentWeapon();

                    if (_stance == CompanionSetup.StanceRanged)
                    {
                        bool isRanged = equipped != null &&
                            equipped.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow;
                        if (!isRanged)
                        {
                            // Initial equip — let vanilla handle ammo setup, then override
                            _ai.HaveFriendsInRange(_ai.m_viewRange,
                                out Character hurtFriend, out Character friend);
                            _humanoid.EquipBestWeapon(_targetCreature, _targetStatic,
                                hurtFriend, friend);
                            TryForceRangedWeapon();
                            stanceForced = true;
                        }
                        // else: correct weapon already equipped, skip EquipBestWeapon
                    }
                    else if (_stance == CompanionSetup.StanceMelee)
                    {
                        bool isMelee = equipped != null &&
                            (equipped.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon
                          || equipped.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon
                          || equipped.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft);
                        if (!isMelee)
                        {
                            _ai.HaveFriendsInRange(_ai.m_viewRange,
                                out Character hurtFriend, out Character friend);
                            _humanoid.EquipBestWeapon(_targetCreature, _targetStatic,
                                hurtFriend, friend);
                            TryForceMeleeWeapon();
                            stanceForced = true;
                        }
                    }
                    else
                    {
                        // Balanced stance: let vanilla pick the best weapon
                        _ai.HaveFriendsInRange(_ai.m_viewRange,
                            out Character hurtFriend, out Character friend);
                        _humanoid.EquipBestWeapon(_targetCreature, _targetStatic,
                            hurtFriend, friend);
                    }

                    // EquipBestWeapon never equips a shield — it only handles the weapon slot.
                    // If a bow was previously equipped it unequipped the shield. Re-equip it
                    // whenever the current weapon is one-handed and the left hand is empty.
                    TryEquipShield();

                    string newWeapon = _humanoid.GetCurrentWeapon()?.m_shared?.m_name;
                    if (newWeapon != prevWeapon || stanceForced)
                    {
                        _weaponSwitchCooldown = WeaponSwitchCooldown;
                        if (newWeapon != prevWeapon)
                            CompanionsPlugin.Log.LogInfo(
                                $"[CombatAI] Weapon switch: \"{prevWeapon}\" → \"{newWeapon}\"");
                    }
                }
            }
            return _humanoid.GetCurrentWeapon();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Attack — ported from MonsterAI.DoAttack()
        // ══════════════════════════════════════════════════════════════════════

        private static bool IsPassiveAnimal(Character c)
        {
            string name = c.gameObject.name;
            return name.StartsWith("Deer") || name.StartsWith("Hare") || name.StartsWith("Chicken");
        }

        private bool DoAttack(Character target)
        {
            ItemDrop.ItemData weapon = _humanoid.GetCurrentWeapon();
            if (weapon == null) return false;
            if (!_ai.CanUseAttack(weapon)) return false;

            // NPC bow fix: vanilla bows have m_bowDraw=true which requires player
            // hold-and-release. NPCs never draw (0% draw) → worst accuracy + min
            // velocity. Temporarily disable m_bowDraw so the attack clone uses
            // skill-based accuracy and full velocity instead (matches RV approach).
            // Safe because Humanoid.StartAttack clones the Attack before using it.
            var attack = weapon.m_shared?.m_attack;
            bool origBowDraw = false;
            if (attack != null && attack.m_bowDraw)
            {
                origBowDraw = true;
                attack.m_bowDraw = false;
            }

            bool started = _character.StartAttack(target, charge: false);

            if (origBowDraw && attack != null)
                attack.m_bowDraw = true;

            if (started)
            {
                _timeSinceAttacking = 0f;
            }
            return started;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Blocking / Parry — reactive block when enemy swings
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Raise block when the target creature is mid-attack and in melee range.
        /// Resets m_blockTimer to 0 each frame during enemy swing so the block
        /// is always a perfect block (parry). Holds block briefly after the
        /// enemy's attack ends so late-hitting attacks still get blocked.
        /// GetCurrentBlocker() returns shield if equipped, weapon otherwise.
        /// </summary>
        private void UpdateBlocking(float dt)
        {
            // Block/parry with any weapon (melee, bow, etc.) — all stances can parry
            // Suppress blocking during parry counter-attacks — press the advantage
            if (_targetCreature == null
                || _character.InAttack() || _character.IsStaggering()
                || _parryAttackCount > 0)
            {
                ClearBlock();
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetCreature.transform.position);
            bool enemySwinging = _targetCreature.InAttack() && dist < 8f;

            if (enemySwinging)
            {
                _enemySwingTimer += dt;
                _ai.LookAtPoint(_targetCreature.GetTopPoint());

                // Wait until late in the swing to snap the shield up — looks reactive
                if (_enemySwingTimer >= ParryDelay)
                {
                    ReflectionHelper.TrySetBlocking(_character, true);
                    ReflectionHelper.TrySetBlockTimer(_humanoid, 0f);
                    _blockHoldTimer = 0.2f;
                }
            }
            else if (_blockHoldTimer > 0f)
            {
                // Brief hold after swing ends — catches late-hitting attacks
                _blockHoldTimer -= dt;
                ReflectionHelper.TrySetBlocking(_character, true);
                _ai.LookAtPoint(_targetCreature.GetTopPoint());
                _enemySwingTimer = 0f;
            }
            else
            {
                // _blockHoldTimer < 0 means it just drained out this frame (first time entering else).
                // If the enemy is staggering, the parry connected — queue 2 counter-attacks.
                if (_blockHoldTimer < 0f && _targetCreature != null && _targetCreature.IsStaggering())
                {
                    _parryAttackCount = 2;
                    CompanionsPlugin.Log.LogInfo("[CombatAI] Parry! Queuing 2 counter-attacks");
                }
                ClearBlock();
            }
        }

        private void ClearBlock()
        {
            _enemySwingTimer = 0f;
            if (ReflectionHelper.GetBlocking(_character))
                ReflectionHelper.TrySetBlocking(_character, false);
            _blockHoldTimer = 0f;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Weapon forcing helpers (stance overrides)
        // ══════════════════════════════════════════════════════════════════════

        private bool TryForceRangedWeapon()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;
            foreach (var item in inv.GetAllItems())
            {
                if (item.IsEquipable()
                    && item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Bow)
                {
                    _humanoid.EquipItem(item);
                    return true;
                }
            }
            return false;
        }

        private bool TryForceMeleeWeapon()
        {
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;
            foreach (var item in inv.GetAllItems())
            {
                if (item.IsEquipable() && item.m_shared.m_aiAttackRange <= 10f
                    && (item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.OneHandedWeapon
                     || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeapon
                     || item.m_shared.m_itemType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft))
                {
                    _humanoid.EquipItem(item);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Re-equips the best shield from inventory when the companion has a one-handed
        /// weapon in the right hand but nothing in the left hand. Called after every
        /// weapon selection because EquipBestWeapon never equips shields — if a bow
        /// was previously equipped it unequipped the shield via the two-handed slot rule.
        /// </summary>
        private bool TryEquipShield()
        {
            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon == null) return false;

            // Only relevant for one-handed weapons — two-handed and bows can't hold a shield
            if (weapon.m_shared.m_itemType != ItemDrop.ItemData.ItemType.OneHandedWeapon)
                return false;

            // Already has a shield in left hand
            var leftItem = _humanoid.LeftItem;
            if (leftItem != null && leftItem.m_shared.m_itemType == ItemDrop.ItemData.ItemType.Shield)
                return false;

            var inv = _humanoid.GetInventory();
            if (inv == null) return false;

            ItemDrop.ItemData bestShield = null;
            float bestBlock = -1f;
            foreach (var item in inv.GetAllItems())
            {
                if (!item.IsEquipable()) continue;
                if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Shield) continue;
                if (item.m_shared.m_blockPower > bestBlock)
                {
                    bestBlock = item.m_shared.m_blockPower;
                    bestShield = item;
                }
            }

            if (bestShield == null) return false;
            _humanoid.EquipItem(bestShield);
            CompanionsPlugin.Log.LogInfo($"[CombatAI] Shield equipped: \"{bestShield.m_shared.m_name}\"");
            return true;
        }
    }
}
