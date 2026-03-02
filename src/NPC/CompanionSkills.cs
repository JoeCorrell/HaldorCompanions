using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Custom skill system for companions. Mirrors the vanilla Skills formula exactly
    /// but stores data in ZDO instead of the Player save system.
    ///
    /// Vanilla Skills component can't be reused because Skills.Awake() calls
    /// GetComponent&lt;Player&gt;() and all methods reference m_player (would NRE).
    ///
    /// Integrates via Harmony patches on Character.RaiseSkill, GetSkillFactor,
    /// and GetRandomSkillFactor (see SkillPatches.cs). Since Player overrides all
    /// three, the patches only fire for non-Player characters (companions).
    /// </summary>
    public class CompanionSkills : MonoBehaviour
    {
        internal static readonly int SkillsHash =
            StringExtensionMethods.GetStableHashCode("HC_Skills");

        private const float SaveInterval = 10f;
        private const float DeathLowerFactor = 0.25f;

        // ── Static SkillDef cache (shared across all companions) ──

        private static List<Skills.SkillDef> _skillDefs;

        /// <summary>
        /// Cache SkillDefs from the Player prefab. Called once during prefab creation.
        /// </summary>
        public static void InitSkillDefs(GameObject playerPrefab)
        {
            var skills = playerPrefab.GetComponent<Skills>();
            if (skills == null)
            {
                CompanionsPlugin.Log.LogError("[CompanionSkills] Player prefab has no Skills component!");
                return;
            }

            _skillDefs = new List<Skills.SkillDef>(skills.m_skills);
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionSkills] Cached {_skillDefs.Count} SkillDefs from Player prefab.");
        }

        /// <summary>Get a cached SkillDef by type. Returns null if not found.</summary>
        private static Skills.SkillDef GetSkillDef(Skills.SkillType type)
        {
            if (_skillDefs == null) return null;
            for (int i = 0; i < _skillDefs.Count; i++)
            {
                if (_skillDefs[i].m_skill == type)
                    return _skillDefs[i];
            }
            return null;
        }

        // ── Per-skill data ──

        private class SkillData
        {
            public Skills.SkillDef Def;
            public float Level;
            public float Accumulator;

            public SkillData(Skills.SkillDef def)
            {
                Def = def;
                Level = 0f;
                Accumulator = 0f;
            }
        }

        private readonly Dictionary<Skills.SkillType, SkillData> _skills =
            new Dictionary<Skills.SkillType, SkillData>();

        private ZNetView  _nview;
        private Character _character;
        private float     _saveTimer;
        private bool      _dirty;
        private bool      _initialized;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
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

            string saved = _nview.GetZDO().GetString(SkillsHash, "");
            if (saved.Length > 0)
                LoadFromString(saved);

            _initialized = true;
        }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }
            if (_nview == null || _nview.GetZDO() == null) return;
            if (!_nview.IsOwner()) return;

            if (_dirty)
            {
                _saveTimer += Time.deltaTime;
                if (_saveTimer >= SaveInterval)
                {
                    _saveTimer = 0f;
                    SaveToZDO();
                }
            }
        }

        private void OnDestroy()
        {
            if (_initialized && _dirty) SaveToZDO();
        }

        // ── Core methods (vanilla formula) ──

        /// <summary>
        /// Raise a skill by factor. Uses the exact vanilla formula from Skills.Skill.Raise.
        /// </summary>
        public void RaiseSkill(Skills.SkillType type, float factor = 1f)
        {
            if (type == Skills.SkillType.None) return;
            if (!_initialized) TryInit();

            var skill = GetOrCreateSkill(type);
            if (skill == null) return;

            if (skill.Level >= 100f) return;

            float increseStep = skill.Def?.m_increseStep ?? 1f;
            float num = increseStep * factor * Game.m_skillGainRate;

            // Rested buff: skill XP modifier read from vanilla SE_Rested asset at runtime
            var restedBuff = GetComponent<CompanionRestedBuff>();
            if (restedBuff != null && restedBuff.IsRested)
                num += num * CompanionRestedBuff.SkillXPModifier;

            skill.Accumulator += num;

            // Vanilla formula: Pow(Floor(level + 1), 1.5) * 0.5 + 0.5
            float requirement = Mathf.Pow(Mathf.Floor(skill.Level + 1f), 1.5f) * 0.5f + 0.5f;

            if (skill.Accumulator >= requirement)
            {
                skill.Level += 1f;
                skill.Level = Mathf.Clamp(skill.Level, 0f, 100f);
                skill.Accumulator = 0f;
                _dirty = true;

                // Show level-up message to local player
                ShowLevelUpMessage(type, skill);
            }

            _dirty = true;
        }

        /// <summary>
        /// Get skill factor (0-1) for a given skill type.
        /// </summary>
        public float GetSkillFactor(Skills.SkillType type)
        {
            if (type == Skills.SkillType.None) return 0f;
            if (!_initialized) TryInit();

            if (_skills.TryGetValue(type, out var skill))
                return Mathf.Clamp01(skill.Level / 100f);

            return 0f;
        }

        /// <summary>
        /// Get random skill factor for damage calculation. Same formula as vanilla.
        /// </summary>
        public float GetRandomSkillFactor(Skills.SkillType type)
        {
            float skillFactor = GetSkillFactor(type);
            float center = Mathf.Lerp(0.4f, 1f, skillFactor);
            float min = Mathf.Clamp01(center - 0.15f);
            float max = Mathf.Clamp01(center + 0.15f);
            return Mathf.Lerp(min, max, Random.value);
        }

        /// <summary>
        /// Apply death penalty — lower all skills by DeathLowerFactor (25%).
        /// </summary>
        public void OnDeath()
        {
            foreach (var kvp in _skills)
            {
                float loss = kvp.Value.Level * DeathLowerFactor * Game.m_skillReductionRate;
                kvp.Value.Level -= loss;
                kvp.Value.Accumulator = 0f;
            }
            _dirty = true;
            SaveToZDO();

            string name = _character != null ? _character.m_name : "Companion";
            CompanionsPlugin.Log.LogInfo(
                $"[CompanionSkills] Death penalty applied to \"{name}\" — " +
                $"all skills lowered by {DeathLowerFactor * Game.m_skillReductionRate * 100f:F0}%");
        }

        // ── Serialization ──

        /// <summary>
        /// Serialize all skills to a string for ZDO storage or death→respawn transfer.
        /// Format: "type:level:accumulator;type:level:accumulator;..."
        /// </summary>
        public string SerializeSkills()
        {
            if (_skills.Count == 0) return "";

            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var kvp in _skills)
            {
                if (kvp.Value.Level <= 0f && kvp.Value.Accumulator <= 0f) continue;

                if (!first) sb.Append(';');
                first = false;

                sb.Append(((int)kvp.Key).ToString(CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(kvp.Value.Level.ToString("F2", CultureInfo.InvariantCulture));
                sb.Append(':');
                sb.Append(kvp.Value.Accumulator.ToString("F2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Load skills from a serialized string (ZDO or respawn data).
        /// </summary>
        public void LoadFromString(string data)
        {
            _skills.Clear();
            if (string.IsNullOrEmpty(data)) return;

            var entries = data.Split(';');
            for (int i = 0; i < entries.Length; i++)
            {
                var parts = entries[i].Split(':');
                if (parts.Length < 3) continue;

                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeInt))
                    continue;
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float level))
                    continue;
                if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float acc))
                    continue;

                var type = (Skills.SkillType)typeInt;
                var def = GetSkillDef(type);
                if (def == null) continue;

                _skills[type] = new SkillData(def)
                {
                    Level = Mathf.Clamp(level, 0f, 100f),
                    Accumulator = Mathf.Max(0f, acc)
                };
            }

            CompanionsPlugin.Log.LogDebug(
                $"[CompanionSkills] Loaded {_skills.Count} skills from string");
        }

        // ── Private helpers ──

        private SkillData GetOrCreateSkill(Skills.SkillType type)
        {
            if (_skills.TryGetValue(type, out var existing))
                return existing;

            var def = GetSkillDef(type);
            if (def == null)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[CompanionSkills] No SkillDef for {type} — skipping");
                return null;
            }

            var skill = new SkillData(def);
            _skills[type] = skill;
            return skill;
        }

        private void ShowLevelUpMessage(Skills.SkillType type, SkillData skill)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            string name = _character != null ? _character.m_name : "Companion";
            if (_nview?.GetZDO() != null)
            {
                string custom = _nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (!string.IsNullOrEmpty(custom)) name = custom;
            }

            Sprite icon = skill.Def?.m_icon;

            // Format: "CompanionName — $msg_skillup $skill_swords: 5"
            string msg = name + " \u2014 $msg_skillup $skill_" +
                         type.ToString().ToLower() + ": " + (int)skill.Level;

            player.Message(MessageHud.MessageType.TopLeft, msg, 0, icon);

            CompanionsPlugin.Log.LogInfo(
                $"[CompanionSkills] \"{name}\" leveled up {type} to {(int)skill.Level}");
        }

        private void SaveToZDO()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            string data = SerializeSkills();
            _nview.GetZDO().Set(SkillsHash, data);
            _dirty = false;
        }
    }
}
