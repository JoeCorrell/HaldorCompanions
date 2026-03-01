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

        private float _talkTimer;
        private float _talkInterval;
        private int   _lastActionMode = -1;

        private const float MinInterval    = 20f;
        private const float MaxInterval    = 40f;
        private const float SpeechOffset   = 1.8f;
        private const float CullDistance   = 20f;
        private const float SpeechTTL      = 5f;
        private const float RepairThreshold = 0.25f;

        // ── Text pools ─────────────────────────────────────────────────────

        private static readonly string[] ActionLines = {
            "Let's go!", "On it!", "I'll handle it.", "As you wish.", "Consider it done."
        };
        private static readonly string[] GatherLines = {
            "Found some!", "Back to work.", "Almost got it.", "This looks promising.",
            "Plenty of resources here.", "I'll keep at it."
        };
        private static readonly string[] ForageLines = {
            "Ooh, berries!", "This one looks ripe.", "Nature provides.",
            "I'll gather what I can.", "There's plenty growing here."
        };
        private static readonly string[] CombatLines = {
            "Take this!", "Watch out!", "For Odin!", "Stand your ground!",
            "Behind you!", "I've got your back!", "They won't get past me!"
        };
        private static readonly string[] FollowLines = {
            "Right behind you.", "Lead the way.", "Nice day for an adventure.",
            "Where to next?", "I'm with you.", "Staying close."
        };
        private static readonly string[] HungryLines = {
            "I'm starving...", "Got any food?", "My stomach is growling.",
            "Could use a meal.", "I'm getting weak from hunger."
        };
        private static readonly string[] RepairLines = {
            "My gear is worn.", "Need repairs.", "This won't hold much longer.",
            "My equipment is damaged.", "Better fix this soon."
        };
        private static readonly string[] OverweightLines = {
            "My back is hurting from all this weight!",
            "I can't carry any more...",
            "Too heavy! I need to drop some off.",
            "My legs are about to give out!",
            "I'm loaded down. Let's head back."
        };

        // ── Lifecycle ──────────────────────────────────────────────────────

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
            _humanoid  = GetComponent<Humanoid>();
            _food      = GetComponent<CompanionFood>();
            _setup     = GetComponent<CompanionSetup>();
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
                    SayRandom(ActionLines);
                _lastActionMode = mode;
            }

            // Periodic ambient speech
            _talkTimer -= Time.deltaTime;
            if (_talkTimer > 0f) return;
            ResetTimer();

            SayContextual();
        }

        // ── Public API ─────────────────────────────────────────────────────

        /// <summary>Display a specific line above the companion.</summary>
        public void Say(string text)
        {
            if (Chat.instance == null || string.IsNullOrEmpty(text)) return;
            CompanionsPlugin.Log.LogDebug($"[Talk] \"{text}\"");
            Chat.instance.SetNpcText(
                gameObject, Vector3.up * SpeechOffset,
                CullDistance, SpeechTTL, "", text, false);
        }

        // ── Internals ──────────────────────────────────────────────────────

        private void SayContextual()
        {
            // Priority: combat > overweight > hungry > repair > gathering > following
            if (HasEnemyNearby())
            {
                SayRandom(CombatLines);
                return;
            }

            if (IsOverweight())
            {
                SayRandom(OverweightLines);
                return;
            }

            if (IsHungry())
            {
                SayRandom(HungryLines);
                return;
            }

            if (NeedsRepair())
            {
                SayRandom(RepairLines);
                return;
            }

            int mode = _nview.GetZDO()?.GetInt(
                CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
            if (mode >= CompanionSetup.ModeGatherWood && mode <= CompanionSetup.ModeGatherOre)
            {
                SayRandom(GatherLines);
                return;
            }
            if (mode == CompanionSetup.ModeForage)
            {
                SayRandom(ForageLines);
                return;
            }

            SayRandom(FollowLines);
        }

        private void SayRandom(string[] pool)
        {
            if (pool == null || pool.Length == 0) return;
            Say(pool[Random.Range(0, pool.Length)]);
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
