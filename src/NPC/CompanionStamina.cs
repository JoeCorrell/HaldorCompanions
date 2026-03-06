using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Custom stamina system for companions. Valheim's stamina is Player-only,
    /// so this provides a lightweight equivalent with regen/drain and ZDO persistence.
    ///
    /// Base stamina = 50 by default. Food provides additional stamina via CompanionFood.
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

        /// <summary>Set by CompanionAI.ApplyPlayerMovementMatch when companion crouches.</summary>
        public bool IsCrouching { get; set; }

        private static float RegenRate     => ModConfig.StaminaRegenRate.Value;
        private static float RunDrainRate  => ModConfig.StaminaRunDrain.Value;
        private static float SneakDrainRate => ModConfig.StaminaSneakDrain.Value;
        private static float SwimDrainRate => ModConfig.StaminaSwimDrain.Value;
        private static float RegenDelay   => ModConfig.StaminaRegenDelay.Value;
        private const float SaveInterval = 5f;

        private ZNetView      _nview;
        private CompanionFood _food;
        private Character     _character;
        private float         _saveTimer;
        private float         _regenDelayTimer;
        private float         _remoteSyncTimer;
        private bool          _initialized;

        private CompanionRestedBuff _restedBuff;

        private void Awake()
        {
            _nview      = GetComponent<ZNetView>();
            _food       = GetComponent<CompanionFood>();
            _character  = GetComponent<Character>();
            _restedBuff = GetComponent<CompanionRestedBuff>();
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

            // Use Character.IsRunning() + velocity check. IsRunning() returns m_running
            // which is only computed in FixedUpdate (Character.UpdateMotion → CheckRun).
            // Gather controllers (HarvestController, FarmController, etc.) set SetRun(true)
            // in Update, so m_running can be stale on frames without a FixedUpdate tick.
            // Velocity fallback catches this: if the companion is physically moving at
            // running speed, drain regardless of whether m_running has caught up.
            float velocity = _character != null ? _character.GetVelocity().magnitude : 0f;
            bool isSwimming = _character != null && _character.IsSwimming() && velocity > 0.25f;
            float runVelThreshold = _character != null
                ? (_character.m_speed + _character.m_runSpeed) * 0.5f  // midpoint of jog/run
                : 6f;
            bool isRunning = _character != null && !isSwimming && velocity > 0.5f
                             && (_character.IsRunning()
                                 || (velocity > runVelThreshold && _character.IsOnGround()));

            // Drain for running, sneaking, or swimming.
            bool isSneaking = IsCrouching && !isRunning && !isSwimming && velocity > 0.1f;
            if ((isRunning || isSwimming || isSneaking) && Stamina > 0f)
            {
                float drainRate = isSwimming ? SwimDrainRate
                                : isSneaking ? SneakDrainRate
                                : RunDrainRate;
                Stamina = Mathf.Max(0f, Stamina - drainRate * dt);
                _regenDelayTimer = RegenDelay;
            }
            else
            {
                // Regen after delay expires
                _regenDelayTimer = Mathf.Max(0f, _regenDelayTimer - dt);
                if (_regenDelayTimer <= 0f && Stamina < max)
                {
                    // Additive stacking (matches vanilla SE_Stats.ModifyStaminaRegen):
                    // Base=1x, Resting=+1x, Rested buff=+1x
                    float regenMult = 1f;
                    if (IsResting) regenMult += 1f;
                    if (_restedBuff != null && _restedBuff.IsRested)
                        regenMult += CompanionRestedBuff.StaminaRegenAdditiveBonus;
                    float rate = RegenRate * regenMult;
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
