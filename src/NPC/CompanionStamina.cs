using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Custom stamina system for companions. Valheim's stamina is Player-only,
    /// so this provides a lightweight equivalent with regen/drain and ZDO persistence.
    ///
    /// Stamina is consumed by:
    /// - Combat attacks (via Harmony patch on Character.UseStamina → Drain)
    /// - Blocking (via Harmony patch on Character.UseStamina → Drain)
    /// - Harvesting (via CompanionHarvest calling UseStamina directly)
    ///
    /// When stamina reaches 0, attacks are blocked (Harmony patch on Character.HaveStamina)
    /// and blocks fail (vanilla BlockAttack checks HaveStamina).
    /// </summary>
    public class CompanionStamina : MonoBehaviour
    {
        public float MaxStamina = 100f;
        public float Stamina    { get; private set; }

        private const float RegenRate    = 6f;    // per second when idle
        private const float RegenDelay   = 1f;    // seconds after stamina use before regen starts
        private const float SaveInterval = 5f;

        private ZNetView  _nview;
        private float     _saveTimer;
        private float     _regenDelayTimer;
        private bool      _initialized;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
        }

        private void Start()
        {
            if (!_initialized) TryInit();
        }

        private void TryInit()
        {
            if (_initialized) return;
            if (_nview == null || _nview.GetZDO() == null) return;

            float saved = _nview.GetZDO().GetFloat(CompanionSetup.StaminaHash, MaxStamina);
            Stamina = Mathf.Clamp(saved, 0f, MaxStamina);
            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }
            if (_nview == null || !_nview.IsOwner()) return;

            float dt = Time.deltaTime;

            // Regen after delay expires
            _regenDelayTimer -= dt;
            if (_regenDelayTimer <= 0f && Stamina < MaxStamina)
                Stamina = Mathf.Min(MaxStamina, Stamina + RegenRate * dt);

            // Periodic ZDO save
            _saveTimer += dt;
            if (_saveTimer >= SaveInterval)
            {
                _saveTimer = 0f;
                SaveToZDO();
            }
        }

        private void OnDestroy()
        {
            if (_initialized) SaveToZDO();
        }

        /// <summary>
        /// Drain stamina by a fixed amount. Called by the Harmony UseStamina patch
        /// for combat attacks and blocking. Sets the regen delay.
        /// </summary>
        public void Drain(float amount)
        {
            Stamina = Mathf.Max(0f, Stamina - amount);
            _regenDelayTimer = RegenDelay;
        }

        /// <summary>
        /// Try to consume stamina. Returns false if insufficient.
        /// Used by CompanionHarvest for harvest swings.
        /// </summary>
        public bool UseStamina(float amount)
        {
            if (Stamina < amount) return false;
            Stamina -= amount;
            _regenDelayTimer = RegenDelay;
            return true;
        }

        public float GetStaminaPercentage()
        {
            return MaxStamina > 0f ? Stamina / MaxStamina : 0f;
        }

        private void SaveToZDO()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            _nview.GetZDO().Set(CompanionSetup.StaminaHash, Stamina);
        }
    }
}
