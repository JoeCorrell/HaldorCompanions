namespace Companions
{
    /// <summary>
    /// Escalation tiers for follow stuck detection.
    /// Tiers: 1.5s → door, 2.5s → jump, 4s → teleport (returned to caller).
    /// Uses PositionTracker for movement analysis.
    /// </summary>
    internal class FollowStuckEscalation
    {
        internal enum StuckAction { None, TryDoor, Jump, Teleport }

        private readonly Character _character;
        private readonly PositionTracker _tracker;
        private readonly DoorSystem _doors;

        private float _timer;
        private int   _tier;

        internal FollowStuckEscalation(Character character, PositionTracker tracker, DoorSystem doors)
        {
            _character = character;
            _tracker   = tracker;
            _doors     = doors;
        }

        /// <summary>
        /// Evaluate follow stuck state. Called from Follow_Patch.
        /// Handles doors and jumps internally; returns Teleport for caller to handle.
        /// </summary>
        internal StuckAction Update(float dt, float distToTarget)
        {
            // Not stuck if close or moving well
            if (distToTarget < 2f)
            {
                Reset();
                return StuckAction.None;
            }

            float moved = _tracker.DistanceOverWindow(2f);
            if (moved > 1f)
            {
                Reset();
                return StuckAction.None;
            }

            _timer += dt;

            // Tier 2: 4s → teleport
            if (_timer >= 4f && _tier < 3)
            {
                _timer = 0f;
                _tier = 0;
                return StuckAction.Teleport;
            }

            // Tier 1: 2.5s → jump
            if (_timer >= 2.5f && _tier < 2)
            {
                _tier = 2;
                if (_character != null) _character.Jump(false);
                return StuckAction.Jump;
            }

            // Tier 0: 1.5s → try door
            if (_timer >= 1.5f && _tier < 1)
            {
                _tier = 1;
                _doors?.TryOpenNearbyDoor();
                return StuckAction.TryDoor;
            }

            return StuckAction.None;
        }

        /// <summary>
        /// Clear stuck escalation state. Called on mode change, teleport, or
        /// when companion is intentionally holding position near player.
        /// Bug #9 fix: exposed for mode change reset.
        /// </summary>
        internal void Reset()
        {
            _timer = 0f;
            _tier = 0;
        }
    }
}
