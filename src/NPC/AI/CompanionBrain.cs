using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Central AI coordinator. Replaces the monolithic CompanionAI as the single MonoBehaviour.
    /// Creates and owns all subsystems (plain C# classes). Orchestrates a deterministic
    /// update order and exposes shared state for Harmony patches and other components.
    /// </summary>
    public class CompanionBrain : MonoBehaviour
    {
        // ── Enums (used by Harmony patches) ──────────────────────────
        internal enum FollowZone { Inner, Comfort, CatchUp, Sprint }

        // ── References ───────────────────────────────────────────────
        private Character      _character;
        private Humanoid       _humanoid;
        private MonsterAI      _ai;
        private ZNetView       _nview;
        private CompanionStamina _stamina;
        private CompanionSetup _setup;
        private CompanionTalk  _talk;
        private ZSyncAnimation _zanim;
        private Rigidbody      _body;

        // ── Subsystems ───────────────────────────────────────────────
        internal PositionTracker         Tracker      { get; private set; }
        internal EnemyCache              Enemies      { get; private set; }
        internal CombatState             Combat       { get; private set; }
        internal BlockingSystem          Blocking     { get; private set; }
        internal DodgeSystem             Dodge        { get; private set; }
        internal CombatBrain             CombatBrain  { get; private set; }
        internal EncumbranceSystem       Encumbrance  { get; private set; }
        internal AutoPickup              Pickup       { get; private set; }
        internal DoorSystem              Doors        { get; private set; }
        internal FuelingSystem           Fueling      { get; private set; }
        internal IdleFacing              Facing       { get; private set; }
        internal FollowStuckEscalation   FollowStuck  { get; private set; }

        // ── Follow zone state (used by FollowPatches) ────────────────
        internal FollowZone CurrentFollowZone;

        // ══════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════

        private void Awake()
        {
            _character = GetComponent<Character>();
            _humanoid  = GetComponent<Humanoid>();
            _ai        = GetComponent<MonsterAI>();
            _nview     = GetComponent<ZNetView>();
            _stamina   = GetComponent<CompanionStamina>();
            _setup     = GetComponent<CompanionSetup>();
            _talk      = GetComponent<CompanionTalk>();
            _zanim     = GetComponent<ZSyncAnimation>();
            _body      = GetComponent<Rigidbody>();

            // Create subsystems
            Tracker     = new PositionTracker(20);
            Enemies     = new EnemyCache();
            Combat      = new CombatState(_setup);
            Doors       = new DoorSystem(_humanoid, _character, transform, _zanim);
            Blocking    = new BlockingSystem(_character, _humanoid, transform);
            Dodge       = new DodgeSystem(_character, transform, _zanim, _body, _stamina);
            CombatBrain = new CombatBrain(_character, _humanoid, _ai, _nview,
                                          _stamina, _setup, _talk, transform);
            Encumbrance = new EncumbranceSystem(_character, _humanoid, _zanim);
            Pickup      = new AutoPickup(_humanoid, _nview, transform);
            Fueling     = new FuelingSystem(_humanoid, _nview, transform);
            Facing      = new IdleFacing(_character, _ai, transform);
            FollowStuck = new FollowStuckEscalation(_character, Tracker, Doors);

            // Wire up harvest active check (avoids circular dependency)
            var harvest = GetComponent<HarvestController>();
            if (harvest != null)
                CombatBrain.IsHarvestActive = () => harvest.IsActivelyHarvesting;
        }

        private void Start()
        {
            Pickup.Init();
        }

        private void OnDestroy()
        {
            Blocking?.ForceRelease();
            Encumbrance?.RestoreOnDestroy();
        }

        // ══════════════════════════════════════════════════════════════
        //  Update — Deterministic order
        // ══════════════════════════════════════════════════════════════

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;

            Encumbrance.StoreOriginalSpeeds();
            float dt = Time.deltaTime;

            // 1. Position tracking
            Tracker.Update(transform.position, Time.time, dt);

            // 2. Enemy scan (single pass feeds everything)
            Enemies.Update(dt, _character, transform.position);

            // 3. Combat state
            Combat.Update(dt, Enemies, Blocking.IsBlocking, Dodge.InDodge);

            // 4. Combat brain (retreat, consumables, weapon tactics)
            CombatBrain.Update(dt, Enemies, Combat);

            // 5. Proactive doors
            Doors.Update(dt);

            // 6. Encumbrance
            Encumbrance.Update();

            // 7. Dodge tick (cooldown + duration)
            Dodge.Update(dt);

            // 8. Early-out: retreating
            if (CombatBrain.IsRetreating)
            {
                if (Blocking.IsBlocking) Blocking.ForceRelease();
                Facing.Update(dt, false);
                Pickup.Update(dt);
                Fueling.Update(dt);
                return;
            }

            // 9. Early-out: dodging
            if (Dodge.InDodge)
                return;

            // 10. Blocking
            Blocking.Update(dt, Enemies, Dodge);

            // 10a. Counter-attack: after a successful parry, immediately swing
            if (Blocking.WantsCounterAttack && _humanoid != null && !_character.InAttack())
            {
                var target = ReflectionHelper.GetTargetCreature(_ai);
                _humanoid.StartAttack(target, false);
            }

            // 11. Idle facing
            Facing.Update(dt, Blocking.IsBlocking);

            // 12. Auto pickup
            Pickup.Update(dt);

            // 13. Fueling
            Fueling.Update(dt);
        }

        // ══════════════════════════════════════════════════════════════
        //  Mode Change / Teleport Notifications
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// Called when action mode changes. Resets all transient state.
        /// Bug #9 fix: resets CurrentFollowZone to Comfort.
        /// </summary>
        internal void OnActionModeChanged(int oldMode, int newMode)
        {
            FollowStuck.Reset();
            CombatBrain.ResetState();
            Tracker.Reset();
            CurrentFollowZone = FollowZone.Comfort;
        }

        /// <summary>
        /// Called after a teleport. Resets tracking and follow state.
        /// </summary>
        internal void OnTeleported()
        {
            Tracker.Reset();
            CurrentFollowZone = FollowZone.Comfort;
            FollowStuck.Reset();
        }
    }
}
