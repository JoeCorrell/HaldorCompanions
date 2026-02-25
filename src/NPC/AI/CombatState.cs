namespace Companions
{
    /// <summary>
    /// Tracks whether the companion is "in combat" using a 5-second exit delay.
    /// Triggers equipment sync during combat to keep loadout current.
    /// </summary>
    internal class CombatState
    {
        internal bool InCombat { get; private set; }

        private float _combatTimer;
        private float _loadoutTimer;

        private const float ExitDelay       = 5f;
        private const float CombatRange     = 10f;
        private const float LoadoutInterval = 1f;

        private readonly CompanionSetup _setup;

        internal CombatState(CompanionSetup setup)
        {
            _setup = setup;
        }

        internal void Update(float dt, EnemyCache enemies, bool isBlocking, bool isDodging)
        {
            bool active = isBlocking || isDodging ||
                         (enemies.NearestEnemy != null && enemies.NearestEnemyDist < CombatRange);

            if (active)
            {
                InCombat = true;
                _combatTimer = ExitDelay;

                _loadoutTimer -= dt;
                if (_loadoutTimer <= 0f)
                {
                    _loadoutTimer = LoadoutInterval;
                    _setup?.SyncEquipmentToInventory();
                }
            }
            else
            {
                _loadoutTimer = 0f;
                _combatTimer -= dt;
                if (_combatTimer <= 0f)
                    InCombat = false;
            }
        }

        internal void Reset()
        {
            InCombat = false;
            _combatTimer = 0f;
            _loadoutTimer = 0f;
        }
    }
}
