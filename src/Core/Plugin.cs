using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace HaldorCompanions
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency("com.haldor.overhaul")]
    [BepInDependency("com.haldor.bounties", BepInDependency.DependencyFlags.SoftDependency)]
    public class HaldorCompanions : BaseUnityPlugin
    {
        public const string PluginGUID = "com.haldor.companions";
        public const string PluginName = "Haldor Companions";
        public const string PluginVersion = "1.0.0";

        private static Harmony _harmony;
        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"{PluginName} v{PluginVersion} loading...");

            _harmony = new Harmony(PluginGUID);
            try
            {
                _harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception ex)
            {
                Log.LogError($"[HaldorCompanions] Harmony PatchAll failed: {ex}");
            }

            int count = 0;
            foreach (var _ in _harmony.GetPatchedMethods()) count++;
            Log.LogInfo($"{PluginName} loaded successfully! ({count} methods patched)");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            Log?.LogInfo($"{PluginName} unloaded.");
        }
    }
}
