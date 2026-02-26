using UnityEngine;

namespace Companions
{
    /// <summary>
    /// When standing idle near the follow target, smoothly rotates to face
    /// the same direction the player is facing. Activates after a short delay.
    /// </summary>
    internal class IdleFacing
    {
        private readonly Character _character;
        private readonly MonsterAI _ai;
        private readonly Transform _transform;

        private float _idleTimer;
        private const float Delay = 1.5f;

        internal IdleFacing(Character character, MonsterAI ai, Transform transform)
        {
            _character = character;
            _ai        = ai;
            _transform = transform;
        }

        internal void Update(float dt, bool isBlocking)
        {
            if (_ai == null) return;
            var follow = _ai.GetFollowTarget();
            if (follow == null) return;

            float dist = Vector3.Distance(_transform.position, follow.transform.position);
            var moveDir = _character.GetMoveDir();
            bool isMoving = moveDir.sqrMagnitude > 0.01f;

            if (isMoving || dist > 3f) { _idleTimer = 0f; return; }
            if (_character.InAttack() || isBlocking) return;

            _idleTimer += dt;
            if (_idleTimer < Delay) return;

            Vector3 playerForward = follow.transform.forward;
            playerForward.y = 0f;
            if (playerForward.sqrMagnitude < 0.01f) return;

            Quaternion target = Quaternion.LookRotation(playerForward);
            _transform.rotation = Quaternion.Slerp(_transform.rotation, target, dt * 2f);
        }
    }
}
