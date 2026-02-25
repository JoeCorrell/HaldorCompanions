using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Dodge rolling system. Evaluates 3 candidate dodge directions (left, right, backward)
    /// scored by terrain flatness and player visibility.
    /// Bug #6 fix: stamina check happens BEFORE direction computation.
    /// </summary>
    internal class DodgeSystem
    {
        internal bool InDodge => _inDodge;

        internal const float DodgeChance         = 0.15f;
        internal const float DodgeChanceNoShield = 0.35f;

        private readonly Character _character;
        private readonly Transform _transform;
        private readonly ZSyncAnimation _zanim;
        private readonly Rigidbody _body;
        private readonly CompanionStamina _stamina;

        private bool  _inDodge;
        private float _dodgeTimer;
        private float _cooldown;

        private const float CooldownTime  = 2.5f;
        private const float StaminaCost   = 15f;
        private const float Duration      = 0.6f;

        internal DodgeSystem(Character character, Transform transform,
                             ZSyncAnimation zanim, Rigidbody body,
                             CompanionStamina stamina)
        {
            _character = character;
            _transform = transform;
            _zanim     = zanim;
            _body      = body;
            _stamina   = stamina;
        }

        internal void Update(float dt)
        {
            _cooldown -= dt;
            if (_inDodge)
            {
                _dodgeTimer -= dt;
                if (_dodgeTimer <= 0f)
                    _inDodge = false;
            }
        }

        /// <summary>
        /// Attempt a dodge roll away from the attacker.
        /// Returns true if dodge was initiated.
        /// </summary>
        internal bool TryDodge(Character attacker)
        {
            if (attacker == null || _zanim == null) return false;
            if (_cooldown > 0f) return false;

            // Bug #6 fix: check stamina BEFORE computing direction
            if (_stamina != null && !_stamina.UseStamina(StaminaCost))
                return false;

            Vector3 toAttacker = attacker.transform.position - _transform.position;
            toAttacker.y = 0f;
            if (toAttacker.sqrMagnitude < 0.001f)
                toAttacker = _transform.forward;
            else
                toAttacker.Normalize();

            // 4 candidate dodge directions (left, right, backward, diagonal-back)
            Vector3 left     = Vector3.Cross(toAttacker, Vector3.up).normalized;
            Vector3 right    = -left;
            Vector3 backward = -toAttacker;
            // Diagonal: blend backward with the side closer to player
            Vector3 diagBack = (backward + left).normalized;

            Vector3 myPos = _transform.position;
            Vector3 playerDir = Vector3.zero;
            if (Player.m_localPlayer != null)
            {
                playerDir = Player.m_localPlayer.transform.position - myPos;
                playerDir.y = 0f;
                if (playerDir.sqrMagnitude > 0.01f)
                {
                    playerDir.Normalize();
                    // Pick diagonal toward player side
                    diagBack = (backward + (Vector3.Dot(left, playerDir) > 0 ? left : right)).normalized;
                }
            }

            // Score candidates
            Vector3 bestDir = left;
            float bestScore = float.MinValue;
            bool anyGroundValid = false;

            Vector3[] candidates = { left, right, backward, diagBack };
            foreach (var dir in candidates)
            {
                float score = 0f;
                Vector3 candidatePos = myPos + dir * 2f;

                if (ZoneSystem.instance != null)
                {
                    float groundHeight;
                    if (ZoneSystem.instance.GetSolidHeight(candidatePos, out groundHeight))
                    {
                        float heightDiff = Mathf.Abs(groundHeight - myPos.y);
                        score -= heightDiff * 2f;
                        anyGroundValid = true;
                    }
                    else
                    {
                        score -= 10f;
                    }
                }

                if (playerDir.sqrMagnitude > 0.01f)
                    score += Vector3.Dot(dir, playerDir) * 0.5f;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestDir = dir;
                }
            }

            if (!anyGroundValid)
                bestDir = Random.value > 0.5f ? left : right;
            if (bestDir.sqrMagnitude < 0.01f)
                bestDir = _transform.right;
            bestDir.Normalize();

            _transform.rotation = Quaternion.LookRotation(bestDir);
            if (_body != null)
                _body.rotation = _transform.rotation;

            _zanim.SetTrigger("dodge");

            _inDodge    = true;
            _dodgeTimer = Duration;
            _cooldown   = CooldownTime;
            return true;
        }
    }
}
