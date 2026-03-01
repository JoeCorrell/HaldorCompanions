using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Overhead speech text for companions — uses Chat.SetNpcText (same API as Trader/NpcTalk).
    /// Displays context-aware lines based on action mode, combat, hunger, and gear condition.
    /// </summary>
    public class CompanionTalk : MonoBehaviour
    {
        private ZNetView       _nview;
        private Character      _character;
        private Humanoid       _humanoid;
        private CompanionFood  _food;
        private CompanionSetup _setup;
        private AudioSource    _audioSource;
        private string         _voicePack;

        private float _talkTimer;
        private float _talkInterval;
        private int   _lastActionMode = -1;

        private const float MinInterval    = 20f;
        private const float MaxInterval    = 40f;
        private const float SpeechOffset   = 1.8f;
        private const float CullDistance   = 20f;
        private const float SpeechTTL      = 5f;
        private const float RepairThreshold = 0.25f;
        private const float SayCooldown    = 20f;
        private float _lastSayTime = -SayCooldown;

        // ── Text pools (loaded from speech.json) ─────────────────────────
        private static string[] ActionLines    => SpeechConfig.Instance.Action;
        private static string[] GatherLines    => SpeechConfig.Instance.Gather;
        private static string[] ForageLines    => SpeechConfig.Instance.Forage;
        private static string[] CombatLines    => SpeechConfig.Instance.Combat;
        private static string[] FollowLines    => SpeechConfig.Instance.Follow;
        private static string[] HungryLines    => SpeechConfig.Instance.Hungry;
        private static string[] RepairLines    => SpeechConfig.Instance.Repair;
        private static string[] OverweightLines => SpeechConfig.Instance.Overweight;
        private static string[] SmeltLines     => SpeechConfig.Instance.Smelt;
        private static string[] IdleLines      => SpeechConfig.Instance.Idle;

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _humanoid  = GetComponent<Humanoid>();
            _food      = GetComponent<CompanionFood>();
            _setup     = GetComponent<CompanionSetup>();

            // AudioSource for voice clips (3D positional)
            _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.spatialBlend = 1f;
            _audioSource.rolloffMode  = AudioRolloffMode.Linear;
            _audioSource.minDistance   = 2f;
            _audioSource.maxDistance   = CullDistance;
            _audioSource.volume        = 1.5f;
            _audioSource.playOnAwake   = false;

            // Determine voice pack from appearance (0=male, 1=female)
            _voicePack = "MaleCompanion";
            if (_nview != null)
            {
                var zdo = _nview.GetZDO();
                if (zdo != null)
                {
                    string serial = zdo.GetString(CompanionSetup.AppearanceHash, "");
                    var appearance = CompanionAppearance.Deserialize(serial);
                    _voicePack = appearance.ModelIndex == 0 ? "MaleCompanion" : "FemaleCompanion";
                }
            }

            ResetTimer();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;
            if (_character == null || _character.IsDead()) return;
            if (Chat.instance == null) return;

            // Suppress speech while radial menu or interact panel is open for this companion
            if (_setup != null && CompanionRadialMenu.IsOpenFor(_setup)) return;
            if (_setup != null && CompanionInteractPanel.IsOpenFor(_setup)) return;

            // Check for action mode change (immediate speech)
            var zdo = _nview.GetZDO();
            if (zdo != null)
            {
                int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                if (_lastActionMode >= 0 && mode != _lastActionMode)
                    SayRandom(ActionLines, "Action");
                _lastActionMode = mode;
            }

            // Periodic ambient speech
            _talkTimer -= Time.deltaTime;
            if (_talkTimer > 0f) return;
            ResetTimer();

            SayContextual();
        }

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>Display a specific line above the companion and optionally play voice audio.</summary>
        public void Say(string text, string audioCategory = null)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (Time.time - _lastSayTime < SayCooldown) return;
            _lastSayTime = Time.time;
            CompanionsPlugin.Log.LogDebug($"[Talk] \"{text}\"");

            bool isMale = _voicePack == "MaleCompanion";
            bool showText  = isMale ? CompanionsPlugin.MaleShowSpeechText.Value
                                    : CompanionsPlugin.FemaleShowSpeechText.Value;
            bool playAudio = isMale ? CompanionsPlugin.MaleEnableVoiceAudio.Value
                                    : CompanionsPlugin.FemaleEnableVoiceAudio.Value;

            if (showText && Chat.instance != null)
                Chat.instance.SetNpcText(
                    gameObject, Vector3.up * SpeechOffset,
                    CullDistance, SpeechTTL, "", text, false);

            if (audioCategory != null && playAudio)
                CompanionVoice.Instance?.PlayRandom(_audioSource, _voicePack, audioCategory);
        }

        // ── Internals ──────────────────────────────────────────────────────

        private void SayContextual()
        {
            // Priority: combat > overweight > hungry > repair > gathering > following
            if (HasEnemyNearby())
            {
                SayRandom(CombatLines, "Combat");
                return;
            }

            if (IsOverweight())
            {
                SayRandom(OverweightLines, "Overweight");
                return;
            }

            if (IsHungry())
            {
                SayRandom(HungryLines, "Hungry");
                return;
            }

            if (NeedsRepair())
            {
                SayRandom(RepairLines, "Repair");
                return;
            }

            int mode = _nview.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
            if (mode >= CompanionSetup.ModeGatherWood && mode <= CompanionSetup.ModeGatherOre)
            {
                SayRandom(GatherLines, "Gather");
                return;
            }
            if (mode == CompanionSetup.ModeForage)
            {
                SayRandom(ForageLines, "Forage");
                return;
            }
            if (mode == CompanionSetup.ModeSmelt)
            {
                SayRandom(SmeltLines, "Smelt");
                return;
            }
            if (mode == CompanionSetup.ModeStay)
            {
                SayRandom(IdleLines, "Idle");
                return;
            }

            // Mix in random idle chatter ~40% of the time while following
            if (Random.value < 0.4f)
                SayRandom(IdleLines, "Idle");
            else
                SayRandom(FollowLines, "Follow");
        }

        private void SayRandom(string[] pool, string audioCategory = null)
        {
            if (pool == null || pool.Length == 0) return;
            Say(pool[Random.Range(0, pool.Length)], audioCategory);
        }

        private void ResetTimer()
        {
            _talkInterval = Random.Range(MinInterval, MaxInterval);
            _talkTimer    = _talkInterval;
        }

        private bool IsOverweight()
        {
            if (_humanoid == null) return false;
            var inv = _humanoid.GetInventory();
            return inv != null && inv.GetTotalWeight() >= 298f;
        }

        private bool IsHungry()
        {
            // Dvergers don't eat — never report hunger
            if (_setup != null && !_setup.CanWearArmor()) return false;
            if (_food == null) return false;
            for (int i = 0; i < CompanionFood.MaxFoodSlots; i++)
            {
                if (_food.GetFood(i).IsActive) return false;
            }
            return true;
        }

        private bool NeedsRepair()
        {
            if (_humanoid == null) return false;
            var inv = _humanoid.GetInventory();
            if (inv == null) return false;

            foreach (var item in inv.GetAllItems())
            {
                if (!item.m_shared.m_useDurability) continue;
                if (!_humanoid.IsItemEquiped(item)) continue;
                float maxDura = item.GetMaxDurability();
                if (maxDura > 0f && item.m_durability / maxDura < RepairThreshold)
                    return true;
            }
            return false;
        }

        private bool HasEnemyNearby()
        {
            if (_character == null) return false;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(_character, c)) continue;
                float d = Vector3.Distance(transform.position, c.transform.position);
                if (d < 10f) return true;
            }
            return false;
        }
    }
}
