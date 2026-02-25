using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Attached to every companion prefab. Provides custom hover text via
    /// Harmony patches on Character. The Container component on the same GO
    /// handles the actual E/Use interaction (Interactable) natively —
    /// Character does not implement Interactable, so Container is found first
    /// by Player.Interact → GetComponentInParent&lt;Interactable&gt;().
    /// </summary>
    public class CompanionInteract : MonoBehaviour
    {
        private ZNetView  _nview;
        private Character _character;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
        }

        internal string GetHoverText()
        {
            string name = GetCompanionName();
            return Localization.instance.Localize(
                $"{name}\n[<color=yellow><b>$KEY_Use</b></color>] Open");
        }

        internal string GetHoverName()
        {
            return GetCompanionName();
        }

        private string GetCompanionName()
        {
            if (_nview != null && _nview.GetZDO() != null)
            {
                string custom = _nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (!string.IsNullOrEmpty(custom))
                    return custom;
            }
            return _character != null ? _character.m_name : "Companion";
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Harmony patches — ownership check on Container.Interact
        // ═══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static class ContainerInteract_Patch
        {
            static bool Prefix(Container __instance, Humanoid character)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) return true;

                string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
                var player = character as Player;
                if (player == null) return false;

                if (owner.Length > 0 && owner != player.GetPlayerID().ToString())
                {
                    player.Message(MessageHud.MessageType.Center, "This is not your companion.");
                    return false;
                }
                return true;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Harmony patches — intercept Character hover text for companions
        // ═══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
        private static class CharacterGetHoverText_Patch
        {
            static bool Prefix(Character __instance, ref string __result)
            {
                var ci = __instance.GetComponent<CompanionInteract>();
                if (ci == null) return true;
                __result = ci.GetHoverText();
                return false;
            }
        }

        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverName))]
        private static class CharacterGetHoverName_Patch
        {
            static bool Prefix(Character __instance, ref string __result)
            {
                var ci = __instance.GetComponent<CompanionInteract>();
                if (ci == null) return true;
                __result = ci.GetHoverName();
                return false;
            }
        }
    }
}
