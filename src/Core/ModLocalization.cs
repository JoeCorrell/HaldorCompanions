using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Localization framework that integrates with Valheim's built-in Localization system.
    /// Loads translation files from a Translations/ folder alongside the DLL and injects
    /// all hc_* keys into Localization.m_translations so they work with $key patterns.
    /// </summary>
    [HarmonyPatch]
    public static class ModLocalization
    {
        private static readonly FieldInfo TranslationsField =
            AccessTools.Field(typeof(Localization), "m_translations");

        private static Dictionary<string, string> _englishDefaults;
        private static string _translationsDir;
        private static bool _initialized;

        // Regex to extract key/value pairs: { "key": "...", "value": "..." }
        private static readonly Regex EntryRegex = new Regex(
            @"""key""\s*:\s*""((?:[^""\\]|\\.)*)""[^""]*""value""\s*:\s*""((?:[^""\\]|\\.)*)""",
            RegexOptions.Compiled);

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Look up a translation key. Loc("hc_ui_food") → "Food"</summary>
        public static string Loc(string key)
        {
            if (Localization.instance == null) return key;
            return Localization.instance.Localize("$" + key);
        }

        /// <summary>Look up and format: LocFmt("hc_msg_arrived", name) → "Your new companion has arrived!"</summary>
        public static string LocFmt(string key, params object[] args)
        {
            string template = Loc(key);
            try { return string.Format(template, args); }
            catch { return template; }
        }

        /// <summary>Called once from Plugin.Awake after Harmony.PatchAll.</summary>
        public static void Init()
        {
            string dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            _translationsDir = Path.Combine(dllDir, "Translations");
            BuildEnglishDefaults();
            _initialized = true;
            // Don't access Localization.instance here — the singleton getter triggers
            // Initialize() → constructor → SetupLanguage, and our Harmony postfix will
            // handle injection at that point via __instance.
        }

        /// <summary>Auto-generates Translations/English.json if it doesn't exist.</summary>
        public static void EnsureDefaultFile()
        {
            if (_englishDefaults == null) return;

            try
            {
                if (!Directory.Exists(_translationsDir))
                    Directory.CreateDirectory(_translationsDir);

                string path = Path.Combine(_translationsDir, "English.json");
                if (File.Exists(path)) return;

                var entries = new List<KeyValuePair<string, string>>(_englishDefaults);
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("    \"entries\": [");
                for (int i = 0; i < entries.Count; i++)
                {
                    string comma = i < entries.Count - 1 ? "," : "";
                    sb.AppendLine($"        {{ \"key\": \"{EscapeJson(entries[i].Key)}\", \"value\": \"{EscapeJson(entries[i].Value)}\" }}{comma}");
                }
                sb.AppendLine("    ]");
                sb.AppendLine("}");

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                CompanionsPlugin.Log.LogInfo($"[Localization] Created default {path}");
            }
            catch (Exception ex)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Localization] Failed to write English.json: {ex.Message}");
            }
        }

        // ── Harmony: re-inject after every language load ────────────────────

        [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
        [HarmonyPostfix]
        static void SetupLanguage_Postfix(Localization __instance)
        {
            // IMPORTANT: Use __instance, NOT Localization.instance.
            // During construction, m_instance is still null and the getter would
            // trigger Initialize() → new Localization() → SetupLanguage → infinite recursion.
            if (_initialized) InjectTranslations(__instance);
        }

        // ── Core injection ──────────────────────────────────────────────────

        private static void InjectTranslations(Localization loc)
        {
            if (loc == null) return;

            string language = loc.GetSelectedLanguage();
            var translations = LoadTranslationFile(language);
            if (translations == null && language != "English")
                translations = LoadTranslationFile("English");
            if (translations == null)
                translations = _englishDefaults;

            var dict = TranslationsField?.GetValue(loc)
                as Dictionary<string, string>;
            if (dict == null) return;

            int count = 0;
            foreach (var kvp in translations)
            {
                dict[kvp.Key] = kvp.Value;
                count++;
            }

            CompanionsPlugin.Log.LogInfo(
                $"[Localization] Injected {count} keys for language \"{language}\"");
        }

        private static Dictionary<string, string> LoadTranslationFile(string language)
        {
            if (string.IsNullOrEmpty(_translationsDir)) return null;
            string path = Path.Combine(_translationsDir, language + ".json");
            if (!File.Exists(path)) return null;

            try
            {
                string json = File.ReadAllText(path);
                var dict = ParseTranslationJson(json);
                if (dict == null || dict.Count == 0) return null;

                CompanionsPlugin.Log.LogInfo(
                    $"[Localization] Loaded {dict.Count} keys from {path}");
                return dict;
            }
            catch (Exception ex)
            {
                CompanionsPlugin.Log.LogWarning(
                    $"[Localization] Failed to load {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Manual JSON parser for translation files. Unity's JsonUtility cannot
        /// deserialize arrays of serializable objects in Unity 6, so we parse
        /// key/value pairs directly with regex.
        /// </summary>
        private static Dictionary<string, string> ParseTranslationJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            var dict = new Dictionary<string, string>();
            var matches = EntryRegex.Matches(json);
            for (int i = 0; i < matches.Count; i++)
            {
                var m = matches[i];
                string key = UnescapeJson(m.Groups[1].Value);
                string value = UnescapeJson(m.Groups[2].Value);
                if (!string.IsNullOrEmpty(key))
                    dict[key] = value;
            }
            return dict;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\n", "\\n")
                    .Replace("\r", "\\r")
                    .Replace("\t", "\\t");
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\n", "\n")
                    .Replace("\\r", "\r")
                    .Replace("\\t", "\t")
                    .Replace("\\\"", "\"")
                    .Replace("\\\\", "\\");
        }

        // ── English defaults ────────────────────────────────────────────────

        private static void BuildEnglishDefaults()
        {
            _englishDefaults = new Dictionary<string, string>
            {
                // ── UI: CompanionPanel ──
                { "hc_ui_title_companions", "Companions" },
                { "hc_ui_title_choose", "Choose Your Companion" },
                { "hc_ui_btn_spawn", "Spawn Companion" },
                { "hc_ui_btn_buy", "Buy {0} ({1})" },
                { "hc_ui_btn_need", "Need {0}" },
                { "hc_ui_bank", "Bank: {0}" },
                { "hc_ui_label_type", "Type" },
                { "hc_ui_label_gender", "Gender" },
                { "hc_ui_label_hair", "Hair Style" },
                { "hc_ui_label_beard", "Beard Style" },
                { "hc_ui_label_skin", "Skin Tone" },
                { "hc_ui_label_hairtone", "Hair Tone" },
                { "hc_ui_label_hairshade", "Hair Shade" },
                { "hc_ui_label_subtype", "Sub-Type" },
                { "hc_ui_btn_male", "Male" },
                { "hc_ui_btn_female", "Female" },
                { "hc_ui_btn_companion", "Companion" },
                { "hc_ui_btn_dverger", "Dverger" },
                { "hc_ui_beard_none", "None" },
                { "hc_ui_desc_starter",
                    "A loyal companion will join you on your journey. Customise their appearance " +
                    "on the left, then spawn them into the world.\n\n" +
                    "They will follow you, fight by your side, and carry supplies. Equip them " +
                    "with gear and keep them fed to stay battle-ready.\n\n" +
                    "Press Escape to skip \u2014 the panel will appear again next session." },
                { "hc_ui_desc_trader",
                    "Hire a loyal companion to join your journey. They will follow you across " +
                    "the world and fight by your side against any threat.\n\n" +
                    "Each companion has their own health, stamina and inventory. You will need " +
                    "to equip them with gear and keep them fed to stay battle-ready.\n\n" +
                    "Interact with your companion to open their panel, where you can manage " +
                    "their equipment and issue commands.\n\n" +
                    "Customise their appearance on the left, then confirm your purchase." },

                // ── UI: CompanionInteractPanel ──
                { "hc_ui_placeholder_name", "Enter name..." },
                { "hc_ui_label_food", "Food" },

                // ── Radial Menu ──
                { "hc_radial_follow", "Follow" },
                { "hc_radial_wood", "Wood" },
                { "hc_radial_stone", "Stone" },
                { "hc_radial_ore", "Ore" },
                { "hc_radial_forage", "Forage" },
                { "hc_radial_smelt", "Smelt" },
                { "hc_radial_stayhome", "Stay Home" },
                { "hc_radial_sethome", "Set Home" },
                { "hc_radial_wander", "Wander" },
                { "hc_radial_pickup", "Pickup" },
                { "hc_radial_command", "Command" },
                { "hc_radial_balanced", "Balanced" },
                { "hc_radial_aggressive", "Aggressive" },
                { "hc_radial_defensive", "Defensive" },
                { "hc_radial_passive", "Passive" },
                { "hc_radial_melee", "Melee" },
                { "hc_radial_ranged", "Ranged" },
                { "hc_radial_active", "ACTIVE" },
                { "hc_radial_on", "ON" },
                { "hc_radial_off", "OFF" },

                // ── Hover Text ──
                { "hc_hover_inventory", "Inventory" },
                { "hc_hover_commands", "Commands" },
                { "hc_msg_not_yours", "This is not your companion." },

                // ── Messages: CompanionManager ──
                { "hc_msg_need_coins", "Not enough coins in bank! Need {0}" },
                { "hc_msg_arrived", "Your new {0} has arrived!" },
                { "hc_msg_returned", "{0} has returned at {1}!" },
                { "hc_msg_location_home", "home" },
                { "hc_msg_location_spawn", "the world spawn" },
                { "hc_msg_starter_joined", "A companion has joined you on your journey!" },
                { "hc_msg_name_default", "Companion" },

                // ── Messages: Sleep / Rested / Harvest ──
                { "hc_msg_sleep_waiting", "Waiting for {0} to sleep..." },
                { "hc_msg_sleep_waiting_generic", "Waiting for companions to sleep..." },
                { "hc_msg_rested", "{0} is Rested (Comfort: {1})" },
                { "hc_msg_rested_suffix", "Rested" },
                { "hc_msg_tools_weak", "{0}'s tools are not strong enough" },

                // ── Speech: Controllers ──
                { "hc_speech_repair_start", "Time for repairs." },
                { "hc_speech_repair_done", "All fixed up!" },
                { "hc_speech_smelt_monitoring", "Everything's running. I'll keep watch." },
                { "hc_speech_smelt_done", "All done smelting." },
                { "hc_speech_smelt_fuel", "Fetching fuel." },
                { "hc_speech_smelt_materials", "Fetching materials." },
                { "hc_speech_homestead_refuel", "Time to refuel." },
                { "hc_speech_homestead_stoked", "Fire's stoked." },
                { "hc_speech_homestead_repair", "I'll patch that up." },
                { "hc_speech_homestead_repaired", "Good as new." },
                { "hc_speech_homestead_tidy", "Let me tidy up." },
                { "hc_speech_homestead_tidied", "All tidied up." },
                { "hc_speech_no_chest", "No chest nearby to unload!" },
                { "hc_speech_overweight", "My back is hurting from all this weight!" },

                // ── Speech: CompanionAI ──
                { "hc_speech_tomb_found", "My belongings!" },
                { "hc_speech_tomb_recovered", "Got everything back." },
                { "hc_speech_tomb_empty", "Nothing left to take." },
                { "hc_speech_deposit_done", "All stowed away." },
                { "hc_speech_deposit_empty", "Nothing to deposit." },

                // ── Directed commands ──
                { "hc_cmd_comehere_1", "Coming!" },
                { "hc_cmd_comehere_2", "On my way back!" },
                { "hc_cmd_comehere_3", "Right behind you." },
                { "hc_cmd_attack_1", "On it!" },
                { "hc_cmd_attack_2", "Going in!" },
                { "hc_cmd_attack_3", "I'll take them down!" },
                { "hc_cmd_attack_4", "For Odin!" },
                { "hc_cmd_cart_pull_1", "I'll haul this." },
                { "hc_cmd_cart_pull_2", "Got the cart!" },
                { "hc_cmd_cart_pull_3", "Let me pull." },
                { "hc_cmd_cart_release_1", "Letting go." },
                { "hc_cmd_cart_release_2", "Cart's free." },
                { "hc_cmd_cart_release_3", "Released!" },
                { "hc_cmd_door_1", "Getting the door." },
                { "hc_cmd_door_2", "I'll get it." },
                { "hc_cmd_door_3", "Door's open!" },
                { "hc_cmd_sit_1", "Nice and warm." },
                { "hc_cmd_sit_2", "Good spot to rest." },
                { "hc_cmd_sit_3", "I'll sit here." },
                { "hc_cmd_sleep_1", "Time for some rest." },
                { "hc_cmd_sleep_2", "I could use some sleep." },
                { "hc_cmd_sleep_3", "Wake me if you need me." },
                { "hc_cmd_wake_1", "I'm up!" },
                { "hc_cmd_wake_2", "Already?" },
                { "hc_cmd_wake_3", "Right, let's go." },
                { "hc_cmd_deposit_1", "Dropping off my haul." },
                { "hc_cmd_deposit_2", "Storing the goods." },
                { "hc_cmd_deposit_3", "Lightening my load." },
                { "hc_cmd_deposit_empty_1", "I've got nothing to drop off." },
                { "hc_cmd_deposit_empty_2", "Already empty." },
                { "hc_cmd_harvest_1", "I'll get that." },
                { "hc_cmd_harvest_2", "On it!" },
                { "hc_cmd_harvest_3", "Looks like good stuff." },
                { "hc_cmd_cancel_1", "Standing by." },
                { "hc_cmd_cancel_2", "Awaiting orders." },
                { "hc_cmd_cancel_3", "Ready when you are." },
                { "hc_cmd_move_1", "Heading over." },
                { "hc_cmd_move_2", "On my way." },
                { "hc_cmd_move_3", "Moving out." },
                { "hc_cmd_repair_1", "I'll fix my gear up." },
                { "hc_cmd_repair_2", "Time for repairs." },
                { "hc_cmd_repair_3", "This needs some work." },
                { "hc_cmd_board_1", "Coming aboard!" },
                { "hc_cmd_board_2", "All aboard!" },
                { "hc_cmd_board_3", "I'll hop on." },
                { "hc_cmd_repair_nothing_1", "Nothing to fix here." },
                { "hc_cmd_repair_nothing_2", "My gear's fine." },
                { "hc_cmd_repair_nothing_3", "No repairs needed." },
            };
        }
    }
}
