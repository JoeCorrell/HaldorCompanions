using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Companions
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.profmags.traderoverhaul")]
    [BepInDependency("com.haldor.bounties", BepInDependency.DependencyFlags.SoftDependency)]
    public class CompanionsPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "com.profmags.companions";
        public const string PluginName = "Offline Companions";
        public const string PluginVersion = "0.0.3";

        private static Harmony _harmony;
        internal static ManualLogSource Log;
        internal static ConfigEntry<KeyCode> DirectTargetKey;
        private bool _fontFixWarned;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            DirectTargetKey = Config.Bind("Controls", "DirectTargetKey", KeyCode.Z,
                "Press while looking at an enemy to direct companions to focus-fire that target.");

            _harmony = new Harmony(PluginGUID);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                int count = 0;
                foreach (var _ in _harmony.GetPatchedMethods()) count++;
                Log.LogInfo($"{PluginName} loaded successfully! ({count} methods patched)");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Companions] Harmony PatchAll failed: {ex}");
            }

            SceneManager.sceneLoaded += OnSceneLoaded;
            EnsureTmpDefaultFont("awake");
        }

        private void Start()
        {
            // Run once after initial scene bootstraps to catch late-loaded UI fonts.
            EnsureTmpDefaultFont("start");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _harmony?.UnpatchSelf();
            Log?.LogInfo($"{PluginName} unloaded.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            EnsureTmpDefaultFont($"scene:{scene.name}");
        }

        private static bool IsBrokenTmpFont(TMP_FontAsset font)
        {
            return font == null ||
                   font.name.IndexOf("LiberationSans", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static TMP_FontAsset FindReplacementTmpFont()
        {
            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null) continue;
                var font = text.font;
                if (!IsBrokenTmpFont(font))
                    return font;
            }

            var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
            TMP_FontAsset best = null;
            for (int i = 0; i < fonts.Length; i++)
            {
                var font = fonts[i];
                if (IsBrokenTmpFont(font)) continue;
                string name = font.name.ToLowerInvariant();
                if (name.Contains("averia") || name.Contains("norse") || name.Contains("valheim"))
                    return font;
                if (best == null) best = font;
            }

            return best;
        }

        private void EnsureTmpDefaultFont(string source)
        {
            if (!IsBrokenTmpFont(TMP_Settings.defaultFontAsset))
                return;

            var replacement = FindReplacementTmpFont();
            if (replacement == null)
            {
                if (!_fontFixWarned)
                {
                    _fontFixWarned = true;
                    Log?.LogWarning("[Fonts] Could not find a replacement TMP font asset yet.");
                }
                return;
            }

            TMP_Settings.defaultFontAsset = replacement;
            _fontFixWarned = false;

            int reassigned = 0;
            var texts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            for (int i = 0; i < texts.Length; i++)
            {
                var text = texts[i];
                if (text == null) continue;
                if (!IsBrokenTmpFont(text.font)) continue;
                text.font = replacement;
                reassigned++;
            }

            Log?.LogInfo(
                $"[Fonts] TMP default font repaired to '{replacement.name}' ({source}), reassigned={reassigned}.");
        }
    }
}
