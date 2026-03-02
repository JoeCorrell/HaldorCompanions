using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Companions
{
    [Serializable]
    public class SpeechConfig
    {
        public string[] Action;
        public string[] Gather;
        public string[] Forage;
        public string[] Combat;
        public string[] Follow;
        public string[] Hungry;
        public string[] Repair;
        public string[] Overweight;
        public string[] Smelt;
        public string[] Idle;

        private static SpeechConfig _instance;

        public static SpeechConfig Instance
        {
            get
            {
                if (_instance == null) Load();
                return _instance;
            }
        }

        /// <summary>Reload speech lines for the current language. Called on language change.</summary>
        public static void Reload()
        {
            _instance = null;
            Load();
        }

        public static void Load()
        {
            string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            // 1. Try language-specific speech file: Translations/speech/{Language}.json
            string language = "English";
            try
            {
                if (Localization.instance != null)
                    language = Localization.instance.GetSelectedLanguage();
            }
            catch { }

            string langPath = Path.Combine(dir, "Translations", "speech", language + ".json");
            if (TryLoadFrom(langPath)) return;

            // 2. Try root speech.json (backward compatible)
            string rootPath = Path.Combine(dir, "speech.json");
            if (TryLoadFrom(rootPath)) return;

            // 3. Hardcoded English defaults
            _instance = Defaults();

            try
            {
                string json = JsonUtility.ToJson(_instance, true);
                File.WriteAllText(rootPath, json);
                CompanionsPlugin.Log.LogInfo($"[Speech] Created default {rootPath}");
            }
            catch (Exception ex)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Speech] Failed to write defaults to {rootPath}: {ex.Message}");
            }
        }

        private static bool TryLoadFrom(string path)
        {
            if (!File.Exists(path)) return false;

            try
            {
                string json = File.ReadAllText(path);
                _instance = JsonUtility.FromJson<SpeechConfig>(json);
                CompanionsPlugin.Log.LogInfo($"[Speech] Loaded {path}");
                return true;
            }
            catch (Exception ex)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Speech] Failed to parse {path}: {ex.Message}");
                return false;
            }
        }

        private static SpeechConfig Defaults()
        {
            return new SpeechConfig
            {
                Action = new[] {
                    "By your word!", "So it shall be.", "The gods guide my hand.",
                    "Aye, consider it done!", "I heed your call."
                },
                Gather = new[] {
                    "The land provides, if you know where to look.",
                    "Good timber here.", "Odin's bounty is plentiful.",
                    "I'll strip this land bare if I must.",
                    "These arms were made for more than swinging axes... but it'll do.",
                    "The earth yields its riches."
                },
                Forage = new[] {
                    "The meadows offer their gifts.", "Freya's garden blooms well here.",
                    "Even warriors must forage.", "Ripe for the picking.",
                    "The wild provides for those who seek."
                },
                Combat = new[] {
                    "For Odin!", "Taste my steel!", "To Valhalla!",
                    "Stand and fight!", "Skål! Come meet your end!",
                    "They shall not pass!", "By Thor's hammer!"
                },
                Follow = new[] {
                    "Lead on, I am your shield.", "Where the path takes us, I follow.",
                    "A fine day to wander the wilds.", "My blade is yours to command.",
                    "The road calls to us.", "I walk beside you, friend."
                },
                Hungry = new[] {
                    "My belly roars like a troll...", "Even Fenrir ate better than this.",
                    "A warrior fights on mead and meat, not empty guts.",
                    "I'd trade my axe for a leg of boar right now.",
                    "The hunger gnaws at my strength..."
                },
                Repair = new[] {
                    "This blade has seen better days.", "My armor holds by thread and prayer.",
                    "A dull edge brings a swift death.", "The smithy calls to my gear.",
                    "Even the gods mend their weapons."
                },
                Overweight = new[] {
                    "By Odin's beard, my back is breaking!",
                    "I carry the weight of Jotunheim on my shoulders...",
                    "Not even Thor could haul this much further!",
                    "My legs buckle beneath this burden!",
                    "We must lighten this load before I collapse."
                },
                Smelt = new[] {
                    "The forge fire burns bright.", "Good ore makes good steel.",
                    "The bellows sing their song.", "Another ingot for the hoard.",
                    "Dwarven work, this smelting.", "The flames hunger for more."
                },
                Idle = new[] {
                    "The winds whisper of adventure...",
                    "Skål!",
                    "A calm before the storm, perhaps.",
                    "I could use a horn of mead.",
                    "I sense something stirring in the mist.",
                    "What tales will they sing of us, I wonder?"
                }
            };
        }
    }
}
