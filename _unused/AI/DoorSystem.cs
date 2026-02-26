using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Proactive door opening. Triggers when the companion's velocity is low
    /// but it is trying to move. Also called by stuck escalation systems.
    /// Uses per-instance scan buffer for multi-companion safety.
    /// </summary>
    internal class DoorSystem
    {
        private readonly Humanoid _humanoid;
        private readonly Character _character;
        private readonly Transform _transform;
        private readonly ZSyncAnimation _zanim;

        private float _cooldown;
        private float _scanTimer;
        private readonly Collider[] _scanBuffer = new Collider[48];
        private readonly HashSet<int> _seenIds = new HashSet<int>();
        private readonly int _scanMask;

        private const float CooldownTime       = 2f;
        private const float ScanInterval        = 1f;
        private const float ScanRadius          = 2.5f;
        private const float VelocityThreshold   = 0.3f;

        internal DoorSystem(Humanoid humanoid, Character character,
                            Transform transform, ZSyncAnimation zanim)
        {
            _humanoid  = humanoid;
            _character = character;
            _transform = transform;
            _zanim     = zanim;

            // Doors are on "piece" layer; also include Default as fallback
            int piece = LayerMask.NameToLayer("piece");
            int def   = LayerMask.NameToLayer("Default");
            int mask = 0;
            if (piece >= 0) mask |= (1 << piece);
            if (def   >= 0) mask |= (1 << def);
            _scanMask = mask != 0 ? mask : ~0;
        }

        /// <summary>
        /// Called every frame. Opens doors proactively when the companion
        /// is trying to move but is blocked (low velocity).
        /// </summary>
        internal void Update(float dt)
        {
            _cooldown  -= dt;
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            if (_character == null) return;
            float speed = _character.GetVelocity().magnitude;
            if (speed > VelocityThreshold) return;

            bool isMoving = _character.GetMoveDir().sqrMagnitude > 0.01f;
            if (!isMoving) return;

            TryOpenNearbyDoor();
        }

        /// <summary>
        /// Scan for and open a nearby closed door. Public for use by stuck escalation.
        /// </summary>
        internal bool TryOpenNearbyDoor()
        {
            if (_humanoid == null) return false;
            if (_cooldown > 0f) return false;

            _seenIds.Clear();
            int hitCount = Physics.OverlapSphereNonAlloc(
                _transform.position, ScanRadius, _scanBuffer, _scanMask, QueryTriggerInteraction.Ignore);
            if (hitCount > _scanBuffer.Length) hitCount = _scanBuffer.Length;

            for (int i = 0; i < hitCount; i++)
            {
                if (TryOpenDoor(_scanBuffer[i]))
                    return true;
            }

            _cooldown = CooldownTime * 0.5f;
            return false;
        }

        private bool TryOpenDoor(Collider col)
        {
            if (col == null) return false;

            var door = col.GetComponentInParent<Door>();
            if (door == null) return false;
            if (!_seenIds.Add(door.GetInstanceID())) return false;

            bool result = door.Interact(_humanoid, false, false);
            if (!result) return false;

            _cooldown = CooldownTime;
            if (_zanim != null) _zanim.SetTrigger("interact");
            return true;
        }
    }
}
