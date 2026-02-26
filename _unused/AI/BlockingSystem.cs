using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Always-parry blocking system. When an enemy attacks within range:
    /// 1. React with a short delay (0.06-0.12s based on distance)
    /// 2. Raise block (perfect parry via CombatPatches forces m_blockTimer=0)
    /// 3. Hold block through the hit
    /// 4. Release and immediately counter-attack
    ///
    /// The companion ALWAYS blocks when it has a shield/weapon with block power.
    /// No RNG — pure reaction to enemy InAttack() state.
    /// </summary>
    internal class BlockingSystem
    {
        internal bool IsBlocking => _isBlocking;

        /// <summary>True for one frame after parry release — signals CompanionBrain to attack.</summary>
        internal bool WantsCounterAttack { get; private set; }

        private readonly Character _character;
        private readonly Humanoid _humanoid;
        private readonly Transform _transform;

        private bool      _isBlocking;
        private float     _holdTimer;
        private float     _cooldown;
        private float     _delayTimer;
        private Character _blockTarget;

        private const float HoldDurationMin  = 0.3f;
        private const float HoldDurationMax  = 0.6f;
        private const float CooldownTime     = 0.6f;   // Short cooldown — we want to parry often
        private const float ReactDelayClose  = 0.06f;
        private const float ReactDelayFar    = 0.12f;
        private const float DetectRange      = 6f;

        internal BlockingSystem(Character character, Humanoid humanoid, Transform transform)
        {
            _character = character;
            _humanoid  = humanoid;
            _transform = transform;
        }

        internal void Update(float dt, EnemyCache enemies, DodgeSystem dodge)
        {
            _cooldown -= dt;
            WantsCounterAttack = false;

            // Clear dead/invalid block target
            if (_blockTarget != null && (!_blockTarget || _blockTarget.IsDead()))
                _blockTarget = null;

            // Can't block during attacks or stagger
            if (_character.InAttack() || _character.IsStaggering())
            {
                if (_isBlocking) ForceRelease();
                return;
            }

            // Currently holding block — count down then release into counter-attack
            if (_isBlocking)
            {
                _holdTimer -= dt;
                if (_holdTimer <= 0f)
                {
                    ForceRelease();
                    WantsCounterAttack = true;  // Signal counter-attack opportunity
                }
                else
                {
                    FaceTarget();
                }
                return;
            }

            // Pending block (waiting for reaction delay)
            if (_blockTarget != null)
            {
                float distSq = (_blockTarget.transform.position - _transform.position).sqrMagnitude;
                if (distSq > DetectRange * DetectRange * 2.25f)
                {
                    _blockTarget = null;
                    _delayTimer = 0f;
                    return;
                }

                _delayTimer -= dt;
                if (_delayTimer <= 0f)
                {
                    StartBlock();
                    if (!_isBlocking) _blockTarget = null;
                }
                else
                {
                    FaceTarget();
                }
                return;
            }

            if (_cooldown > 0f) return;

            // Detect incoming attack — ALWAYS parry if we can.
            // Primary: EnemyCache's NearestAttackingEnemy (updated every 0.25s).
            // Fallback: direct InAttack() check on nearest enemy to catch fast
            // attacks between cache updates (enemy swings are often 0.3-0.5s).
            var attacker = enemies.NearestAttackingEnemy;
            if (attacker == null && enemies.NearestEnemy != null &&
                enemies.NearestEnemyDist < DetectRange &&
                enemies.NearestEnemy.InAttack())
            {
                attacker = enemies.NearestEnemy;
            }
            if (attacker == null) return;

            if (HasBlocker())
            {
                float dist = Vector3.Distance(_transform.position, attacker.transform.position);
                _blockTarget = attacker;
                _delayTimer = Mathf.Lerp(ReactDelayClose, ReactDelayFar,
                    Mathf.InverseLerp(1.5f, DetectRange, dist));
            }
            else if (dodge != null)
            {
                // No blocker — dodge instead
                dodge.TryDodge(attacker);
                _cooldown = CooldownTime;
            }
        }

        private void StartBlock()
        {
            if (_character == null) return;

            if (!ReflectionHelper.TrySetBlocking(_character, true))
                return;

            _isBlocking = true;

            float holdDuration = HoldDurationMax;
            if (_blockTarget != null && _blockTarget)
            {
                float dist = Vector3.Distance(_transform.position, _blockTarget.transform.position);
                holdDuration = Mathf.Lerp(HoldDurationMax, HoldDurationMin,
                    Mathf.InverseLerp(1.5f, DetectRange, dist));
            }
            _holdTimer = holdDuration;
            _cooldown  = CooldownTime;
        }

        internal void ForceRelease()
        {
            _isBlocking  = false;
            _blockTarget = null;
            ReflectionHelper.TrySetBlocking(_character, false);
        }

        private void FaceTarget()
        {
            if (_blockTarget == null || !_blockTarget) return;
            Vector3 dir = _blockTarget.transform.position - _transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
                _transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        private bool HasBlocker()
        {
            if (_humanoid == null) return false;
            var left = ReflectionHelper.GetLeftItem(_humanoid);
            if (left != null && left.m_shared != null && left.m_shared.m_blockPower > 0f)
                return true;
            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon != null && weapon.m_shared != null && weapon.m_shared.m_blockPower > 0f)
                return true;
            return false;
        }
    }
}
