using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Minimal AI coordinator. Owns only the subsystems needed for pathfinding
    /// and harvesting. Combat systems are shelved in _unused/ until harvest works.
    /// </summary>
    public class CompanionBrain : MonoBehaviour
    {
        private Character  _character;
        private ZNetView   _nview;

        // ── Subsystems (only what harvest needs) ─────────────────────
        internal PositionTracker Tracker { get; private set; }
        internal DoorSystem      Doors   { get; private set; }

        // ══════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════

        private void Awake()
        {
            _character = GetComponent<Character>();
            _nview     = GetComponent<ZNetView>();

            var humanoid = GetComponent<Humanoid>();
            var zanim    = GetComponent<ZSyncAnimation>();

            Tracker = new PositionTracker(20);
            Doors   = new DoorSystem(humanoid, _character, transform, zanim);
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            float dt = Time.deltaTime;
            Tracker.Update(transform.position, Time.time, dt);
            Doors.Update(dt);
        }

        // ══════════════════════════════════════════════════════════════
        //  Notifications
        // ══════════════════════════════════════════════════════════════

        internal void OnActionModeChanged(int oldMode, int newMode)
        {
            Tracker.Reset();
        }

        internal void OnTeleported()
        {
            Tracker.Reset();
        }
    }
}
