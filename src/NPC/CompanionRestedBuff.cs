using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Manages the companion's Rested buff. When a companion rests (sits by fire or
    /// sleeps in bed), they receive a Rested buff with duration based on comfort level,
    /// identical to the player's rested system.
    ///
    /// Bonus values are read at runtime from the vanilla SE_Rested asset (which is an
    /// SE_Stats ScriptableObject). This ensures companions get exactly the same bonuses
    /// as the player, and automatically picks up any balance changes or mod overrides.
    ///
    /// The buff is displayed in the HUD status effect bar via a Harmony patch on Hud
    /// (see HudPatches.cs).
    /// </summary>
    public class CompanionRestedBuff : MonoBehaviour
    {
        // ── Cached vanilla values (read from SE_Rested asset at runtime) ──
        // All values are populated once from the actual game data so we match the player exactly.
        // Fallback defaults are used only if the asset lookup fails.
        private static bool  _bonusesCached;
        private static float _baseTTL = 300f;                     // fallback: 5 min
        private static float _ttlPerComfortLevel = 60f;           // fallback: +1 min per comfort
        private static float _healthRegenMultiplier = 1.5f;       // fallback: +50%
        private static float _staminaRegenMultiplier = 2.0f;      // fallback: +100%
        private static float _eitrRegenMultiplier = 1f;           // fallback: no bonus
        private static float _raiseSkillModifier = 0.5f;          // fallback: +50%

        /// <summary>Health regen multiplier from vanilla SE_Rested (1.5 = +50%).</summary>
        public static float HealthRegenMultiplier => _healthRegenMultiplier;

        /// <summary>
        /// Additive stamina regen bonus from vanilla SE_Rested.
        /// Vanilla applies: regenMult += (m_staminaRegenMultiplier - 1f).
        /// With default 2.0 this adds 1.0 to the base 1.0 multiplier = 2x regen.
        /// </summary>
        public static float StaminaRegenAdditiveBonus => _staminaRegenMultiplier - 1f;

        /// <summary>Eitr regen multiplier from vanilla SE_Rested.</summary>
        public static float EitrRegenMultiplier => _eitrRegenMultiplier;

        /// <summary>Skill XP additive modifier from vanilla SE_Rested (0.5 = +50%).</summary>
        public static float SkillXPModifier => _raiseSkillModifier;

        // ── Static tracking for HUD patch ──
        private static readonly List<CompanionRestedBuff> _instances = new List<CompanionRestedBuff>();
        internal static IReadOnlyList<CompanionRestedBuff> Instances => _instances;

        private static Sprite _restedIcon;

        // ── Resting warmup ──
        // Sitting by fire requires a warmup period before Rested applies (like vanilla).
        // Sleeping in a bed applies Rested instantly on wakeup.
        private const float RestingWarmupTime = 20f;   // seconds of sitting before buff applies

        // ── Instance state ──
        private ZNetView  _nview;
        private Character _character;

        private bool  _isRested;
        private float _totalDuration;
        private float _elapsedTime;
        private int   _comfortLevel;

        // Resting accumulation state (for fire sitting warmup)
        private bool  _isAccumulatingRest;
        private float _restingAccumTimer;

        // Display-only StatusEffect (not in any SEMan, used by HudPatches)
        internal CompanionRestedSE DisplaySE { get; private set; }

        public bool IsRested => _isRested;
        public int ComfortLevel => _comfortLevel;
        public float RemainingTime => _isRested ? Mathf.Max(0f, _totalDuration - _elapsedTime) : 0f;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();

            // Create a display-only StatusEffect for the HUD
            DisplaySE = ScriptableObject.CreateInstance<CompanionRestedSE>();
            DisplaySE.m_name = "$se_rested";
            DisplaySE.m_tooltip = "$se_rested_tooltip";
        }

        private void OnEnable()
        {
            if (!_instances.Contains(this))
                _instances.Add(this);
        }

        private void OnDisable()
        {
            _instances.Remove(this);
        }

        private void OnDestroy()
        {
            _instances.Remove(this);
            if (DisplaySE != null)
                Destroy(DisplaySE);
        }

        /// <summary>
        /// Begin accumulating rest (for fire sitting). After RestingWarmupTime seconds
        /// of continuous resting, the Rested buff is applied automatically.
        /// </summary>
        public void StartResting()
        {
            if (_isAccumulatingRest) return;
            _isAccumulatingRest = true;
            _restingAccumTimer = 0f;

            CacheVanillaBonuses();

            string name = GetCompanionName();
            CompanionsPlugin.Log.LogDebug(
                $"[Rested] \"{name}\" started resting — warmup {RestingWarmupTime:F0}s");
        }

        /// <summary>
        /// Stop accumulating rest (companion stood up or was interrupted).
        /// If the warmup hasn't completed, no Rested buff is applied.
        /// </summary>
        public void StopResting()
        {
            if (!_isAccumulatingRest) return;
            _isAccumulatingRest = false;
            _restingAccumTimer = 0f;

            CompanionsPlugin.Log.LogDebug(
                $"[Rested] \"{GetCompanionName()}\" stopped resting before warmup completed");
        }

        /// <summary>
        /// Apply the rested buff immediately. Called on bed wakeup or when
        /// the fire-sitting warmup timer completes.
        /// </summary>
        public void ApplyRestedBuff()
        {
            if (_character == null || _character.IsDead()) return;

            // Cache vanilla bonus values on first use
            CacheVanillaBonuses();

            int comfort = CalculateComfort();
            _comfortLevel = comfort;

            float duration = _baseTTL + (comfort - 1) * _ttlPerComfortLevel;

            // Only extend, never shorten (same as vanilla)
            float remaining = RemainingTime;
            if (_isRested && duration <= remaining)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Rested] Already active with longer remaining time " +
                    $"({remaining:F0}s > {duration:F0}s) — skipping");
                return;
            }

            _isRested = true;
            _totalDuration = duration;
            _elapsedTime = 0f;

            // Update display SE
            DisplaySE.m_icon = _restedIcon;
            DisplaySE.RemainingTime = duration;

            // Update the display name — plain text so HUD doesn't show raw localization keys
            string name = GetCompanionName();
            DisplaySE.m_name = name + " " + ModLocalization.Loc("hc_msg_rested_suffix");

            // Show message to local player
            Player.m_localPlayer?.Message(MessageHud.MessageType.Center,
                ModLocalization.LocFmt("hc_msg_rested", name, comfort.ToString()));

            CompanionsPlugin.Log.LogInfo(
                $"[Rested] Applied to \"{name}\" — comfort={comfort} duration={duration:F0}s " +
                $"hpRegen={_healthRegenMultiplier:F2} stamRegen={_staminaRegenMultiplier:F2} " +
                $"skillXP=+{_raiseSkillModifier:F2}");
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character != null && _character.IsDead()) return;

            // ── Resting warmup accumulation (fire sitting) ──
            if (_isAccumulatingRest)
            {
                _restingAccumTimer += Time.deltaTime;
                if (_restingAccumTimer >= RestingWarmupTime)
                {
                    _isAccumulatingRest = false;
                    _restingAccumTimer = 0f;
                    ApplyRestedBuff();
                }
            }

            // ── Rested buff countdown ──
            if (!_isRested) return;

            _elapsedTime += Time.deltaTime;

            // Keep display SE in sync
            if (DisplaySE != null)
                DisplaySE.RemainingTime = Mathf.Max(0f, _totalDuration - _elapsedTime);

            if (_elapsedTime >= _totalDuration)
            {
                _isRested = false;
                _elapsedTime = 0f;
                _totalDuration = 0f;

                if (DisplaySE != null)
                    DisplaySE.RemainingTime = 0f;

                CompanionsPlugin.Log.LogDebug(
                    $"[Rested] Buff expired for \"{GetCompanionName()}\"");
            }
        }

        private int CalculateComfort()
        {
            // Use transform.position (ground level), NOT GetCenterPoint() (elevated collider center).
            // Vanilla Player.InShelter() uses m_coverPercentage which is computed from transform.position.
            // Using the elevated center point causes Cover.GetCoverForPoint raycasts to miss the roof,
            // returning coverPct < 0.8 and thus comfort = 1.
            Vector3 pos = transform.position;
            Cover.GetCoverForPoint(pos, out float coverPct, out bool underRoof);
            bool inShelter = coverPct >= 0.8f && underRoof;

            int comfort = SE_Rested.CalculateComfortLevel(inShelter, pos);

            CompanionsPlugin.Log.LogDebug(
                $"[Rested] Comfort check — cover={coverPct:P0} underRoof={underRoof} " +
                $"inShelter={inShelter} comfort={comfort}");

            return comfort;
        }

        private string GetCompanionName()
        {
            string name = _character != null ? _character.m_name : "Companion";
            if (_nview?.GetZDO() != null)
            {
                string custom = _nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (!string.IsNullOrEmpty(custom)) name = custom;
            }
            return name;
        }

        /// <summary>
        /// Read the actual bonus values from the vanilla SE_Rested asset at runtime.
        /// This ensures we match exactly what the player gets, including any balance
        /// changes or mod overrides to the Rested buff.
        /// </summary>
        /// <summary>
        /// Reset cached static state. Must be called on world change / ObjectDB reload
        /// to avoid stale Sprite references and allow re-caching from the new world's data.
        /// </summary>
        internal static void ResetCache()
        {
            _bonusesCached = false;
            _restedIcon = null;
            _baseTTL = 300f;
            _ttlPerComfortLevel = 60f;
            _healthRegenMultiplier = 1.5f;
            _staminaRegenMultiplier = 2.0f;
            _eitrRegenMultiplier = 1f;
            _raiseSkillModifier = 0.5f;
        }

        private static void CacheVanillaBonuses()
        {
            if (_bonusesCached) return;

            // Don't set _bonusesCached until we actually succeed — if ObjectDB
            // isn't ready yet, we'll retry on the next call.
            if (ObjectDB.instance == null) return;

            var vanillaRested = ObjectDB.instance.GetStatusEffect(SEMan.s_statusEffectRested);
            if (vanillaRested == null)
            {
                CompanionsPlugin.Log.LogWarning(
                    "[Rested] Could not find vanilla SE_Rested — using fallback bonus values");
                _bonusesCached = true;
                return;
            }

            _restedIcon = vanillaRested.m_icon;

            if (vanillaRested is SE_Rested rested)
            {
                _baseTTL                = rested.m_baseTTL;
                _ttlPerComfortLevel     = rested.m_TTLPerComfortLevel;
                _healthRegenMultiplier  = rested.m_healthRegenMultiplier;
                _staminaRegenMultiplier = rested.m_staminaRegenMultiplier;
                _eitrRegenMultiplier    = rested.m_eitrRegenMultiplier;
                _raiseSkillModifier     = rested.m_raiseSkillModifier;

                CompanionsPlugin.Log.LogInfo(
                    $"[Rested] Cached vanilla SE_Rested values — " +
                    $"baseTTL={_baseTTL:F0} ttlPerComfort={_ttlPerComfortLevel:F0} " +
                    $"healthRegen={_healthRegenMultiplier:F2} " +
                    $"staminaRegen={_staminaRegenMultiplier:F2} " +
                    $"eitrRegen={_eitrRegenMultiplier:F2} " +
                    $"skillXP=+{_raiseSkillModifier:F2} " +
                    $"icon={_restedIcon?.name ?? "null"}");
            }
            else if (vanillaRested is SE_Stats stats)
            {
                _healthRegenMultiplier  = stats.m_healthRegenMultiplier;
                _staminaRegenMultiplier = stats.m_staminaRegenMultiplier;
                _eitrRegenMultiplier    = stats.m_eitrRegenMultiplier;
                _raiseSkillModifier     = stats.m_raiseSkillModifier;

                CompanionsPlugin.Log.LogInfo(
                    $"[Rested] Cached vanilla SE_Stats bonuses (not SE_Rested) — " +
                    $"healthRegen={_healthRegenMultiplier:F2} " +
                    $"staminaRegen={_staminaRegenMultiplier:F2} " +
                    $"eitrRegen={_eitrRegenMultiplier:F2} " +
                    $"skillXP=+{_raiseSkillModifier:F2} " +
                    $"icon={_restedIcon?.name ?? "null"}");
            }
            else
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Rested] Vanilla SE_Rested is {vanillaRested.GetType().Name}, " +
                    $"not SE_Stats — using fallback bonus values");
            }

            _bonusesCached = true;
        }

        // ── Custom SE subclass for HUD display ──

        /// <summary>
        /// Lightweight StatusEffect subclass that overrides GetIconText to display
        /// our own remaining time, avoiding access to protected m_time field.
        /// This SE is never added to any SEMan — it's only used by HudPatches
        /// for the icon display.
        /// </summary>
        internal class CompanionRestedSE : StatusEffect
        {
            internal float RemainingTime;

            public override string GetIconText()
            {
                if (RemainingTime <= 0f) return "";
                int total = (int)RemainingTime;
                int min = total / 60;
                int sec = total % 60;
                if (min > 0) return min + "m " + sec + "s";
                return sec + "s";
            }
        }
    }
}
