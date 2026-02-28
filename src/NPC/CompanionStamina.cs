using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Custom stamina system for companions. Valheim's stamina is Player-only,
    /// so this provides a lightweight equivalent with regen/drain and ZDO persistence.
    ///
    /// Base stamina = 25. Food provides additional stamina via CompanionFood.
    /// MaxStamina = BaseStamina + food bonuses (dynamic).
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
        public float MaxStamina
        {
            get
            {
                float bonus = _food != null ? _food.TotalStaminaBonus : 0f;
                return CompanionFood.BaseStamina + bonus;
            }
        }

        public float Stamina { get; private set; }

        /// <summary>When true (sitting by campfire), regen rate is doubled.</summary>
        public bool IsResting { get; set; }

        /// <summary>Set by CompanionAIPatches.Follow_Patch when companion is running.</summary>
        public bool IsRunning { get; set; }

        private const float RegenRate    = 6f;    // per second when idle
        private const float RunDrainRate = 10f;   // stamina/sec while running
        private const float SwimDrainRate = 10f;  // stamina/sec while swimming
        private const float RegenDelay   = 1f;    // seconds after stamina use before regen starts
        private const float SaveInterval = 5f;

        private ZNetView      _nview;
        private CompanionFood _food;
        private Character     _character;
        private float         _saveTimer;
        private float         _regenDelayTimer;
        private float         _remoteSyncTimer;
        private bool          _initialized;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _food      = GetComponent<CompanionFood>();
            _character = GetComponent<Character>();
        }

        private void Start()
        {
            if (!_initialized) TryInit();
        }

        private void TryInit()
        {
            if (_initialized) return;
            if (_nview == null || _nview.GetZDO() == null) return;

            if (_food == null) _food = GetComponent<CompanionFood>();
            if (_character == null) _character = GetComponent<Character>();

            float saved = _nview.GetZDO().GetFloat(CompanionSetup.StaminaHash, MaxStamina);
            Stamina = Mathf.Clamp(saved, 0f, MaxStamina);
            _initialized = true;
            CompanionsPlugin.Log.LogDebug(
                $"[Stamina] Initialized — {Stamina:F1}/{MaxStamina:F1} " +
                $"(saved={saved:F1}) companion=\"{_character?.m_name ?? "?"}\"");
        }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }
            if (_nview == null || _nview.GetZDO() == null) return;
            if (!_nview.IsOwner())
            {
                _remoteSyncTimer -= Time.deltaTime;
                if (_remoteSyncTimer <= 0f)
                {
                    _remoteSyncTimer = 0.5f;
                    float saved = _nview.GetZDO().GetFloat(CompanionSetup.StaminaHash, MaxStamina);
                    Stamina = Mathf.Clamp(float.IsNaN(saved) ? 0f : saved, 0f, MaxStamina);
                }
                return;
            }

            if (_food == null) _food = GetComponent<CompanionFood>();
            if (_character == null) _character = GetComponent<Character>();

            float dt  = Time.deltaTime;
            float max = MaxStamina;
            if (float.IsNaN(max) || max <= 0f) max = CompanionFood.BaseStamina;
            if (float.IsNaN(Stamina)) Stamina = 0f;

            // Clamp if food expired and max dropped below current
            if (Stamina > max)
                Stamina = max;

            bool isMoving = _character != null && _character.GetMoveDir().sqrMagnitude > 0.04f;
            float speed = _character != null ? _character.GetVelocity().magnitude : 0f;
            bool hasMoveSpeed = speed > 0.25f;
            bool isSwimming = _character != null && _character.IsSwimming() && hasMoveSpeed;
            bool runStateActive = IsRunning &&
                                  _character != null &&
                                  isMoving &&
                                  hasMoveSpeed &&
                                  !isSwimming;
            bool isRunning = runStateActive &&
                             speed > (_character.m_walkSpeed * 1.05f);

            // Follow patch owns this flag; clear it when movement state no longer matches.
            if (!runStateActive) IsRunning = false;

            // Drain only for active movement states that should consume stamina.
            if ((isRunning || isSwimming) && Stamina > 0f)
            {
                float drainRate = isSwimming ? SwimDrainRate : RunDrainRate;
                Stamina = Mathf.Max(0f, Stamina - drainRate * dt);
                _regenDelayTimer = RegenDelay;
            }
            else
            {
                // Regen after delay expires
                _regenDelayTimer = Mathf.Max(0f, _regenDelayTimer - dt);
                if (_regenDelayTimer <= 0f && Stamina < max)
                {
                    float rate = IsResting ? RegenRate * 2f : RegenRate;
                    Stamina = Mathf.Min(max, Stamina + rate * dt);
                }
            }

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
            if (float.IsNaN(amount) || amount <= 0f) return;
            float before = Stamina;
            Stamina = Mathf.Max(0f, Stamina - amount);
            _regenDelayTimer = RegenDelay;
            if (before > 0f && Stamina <= 0f)
                CompanionsPlugin.Log.LogDebug(
                    $"[Stamina] DEPLETED — drained {amount:F1} (was {before:F1}) " +
                    $"companion=\"{_character?.m_name ?? "?"}\"");
        }

        /// <summary>
        /// Try to consume stamina. Returns false if insufficient.
        /// Used by CompanionHarvest for harvest swings.
        /// </summary>
        public bool UseStamina(float amount)
        {
            if (float.IsNaN(amount) || amount <= 0f) return true;
            if (Stamina < amount)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Stamina] UseStamina FAILED — need {amount:F1} have {Stamina:F1} " +
                    $"companion=\"{_character?.m_name ?? "?"}\"");
                return false;
            }
            Stamina -= amount;
            _regenDelayTimer = RegenDelay;
            return true;
        }

        /// <summary>
        /// Restore stamina by a fixed amount. Used by mead consumption since
        /// SE_Stats.m_staminaUpFront calls Player.AddStamina which is a no-op
        /// on non-Player characters.
        /// </summary>
        public void Restore(float amount)
        {
            if (float.IsNaN(amount) || amount <= 0f) return;
            float before = Stamina;
            Stamina = Mathf.Min(MaxStamina, Stamina + amount);
            CompanionsPlugin.Log.LogDebug(
                $"[Stamina] Restored {amount:F1} — {before:F1} → {Stamina:F1}/{MaxStamina:F1} " +
                $"companion=\"{_character?.m_name ?? "?"}\"");
        }

        public float GetStaminaPercentage()
        {
            float max = MaxStamina;
            return max > 0f ? Stamina / max : 0f;
        }

        private void SaveToZDO()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            float safe = float.IsNaN(Stamina) ? 0f : Stamina;
            _nview.GetZDO().Set(CompanionSetup.StaminaHash, safe);
        }
    }
}
