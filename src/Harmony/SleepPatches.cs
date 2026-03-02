using HarmonyLib;

namespace Companions
{
    /// <summary>
    /// Prevents the world from skipping to morning unless all active companions
    /// (in Follow mode) are also sleeping in a bed — just like how multiplayer
    /// requires all players to be in bed before the time skip triggers.
    ///
    /// Also wakes companions when the sleep time-skip completes (SleepStop RPC),
    /// and shows a message to the player while they're waiting for companions.
    /// </summary>
    public static class SleepPatches
    {
        private const float MessageInterval = 3f;
        private static float _messageTimer;

        /// <summary>
        /// Block sleep skip if any Follow-mode companion is not in bed.
        /// Companions in Stay/Harvest/other modes are excluded — the player
        /// can't easily reach them to direct them to a bed.
        /// </summary>
        [HarmonyPatch(typeof(Game), "EverybodyIsTryingToSleep")]
        private static class EverybodyIsTryingToSleep_Patch
        {
            static void Postfix(ref bool __result)
            {
                if (!__result) return;

                var companions = CompanionRest.Instances;
                if (companions.Count == 0) return;

                for (int i = 0; i < companions.Count; i++)
                {
                    var rest = companions[i];
                    if (rest == null) continue;

                    // Only Follow-mode companions block the sleep skip
                    if (!IsFollowMode(rest)) continue;

                    if (!rest.IsSleeping)
                    {
                        __result = false;
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// When the sleep time-skip completes, wake all sleeping companions.
        /// Vanilla SleepStop fires Player.SetSleeping(false) + AttachStop() for the player,
        /// but nothing wakes companions. Without this, they stay frozen in bed forever.
        /// </summary>
        [HarmonyPatch(typeof(Game), "SleepStop")]
        private static class SleepStop_Patch
        {
            static void Postfix()
            {
                var companions = CompanionRest.Instances;
                for (int i = companions.Count - 1; i >= 0; i--)
                {
                    var rest = companions[i];
                    if (rest != null && rest.IsSleeping)
                        rest.CancelDirected();
                }
            }
        }

        /// <summary>
        /// Shows a periodic message when the player is in bed but Follow-mode
        /// companions aren't, so they know why the night isn't skipping.
        /// </summary>
        [HarmonyPatch(typeof(Game), "UpdateSleeping")]
        private static class UpdateSleeping_Patch
        {
            static void Postfix()
            {
                var player = Player.m_localPlayer;
                if (player == null || !player.InBed()) return;

                var companions = CompanionRest.Instances;
                if (companions.Count == 0) return;

                bool allGood = true;
                string blockerName = null;
                for (int i = 0; i < companions.Count; i++)
                {
                    var rest = companions[i];
                    if (rest == null) continue;
                    if (!IsFollowMode(rest)) continue;

                    if (!rest.IsSleeping)
                    {
                        allGood = false;
                        blockerName = GetCompanionName(rest);
                        break;
                    }
                }

                if (allGood) return;

                _messageTimer -= UnityEngine.Time.deltaTime;
                if (_messageTimer > 0f) return;
                _messageTimer = MessageInterval;

                string msg = blockerName != null
                    ? ModLocalization.LocFmt("hc_msg_sleep_waiting", blockerName)
                    : ModLocalization.Loc("hc_msg_sleep_waiting_generic");
                player.Message(MessageHud.MessageType.Center, msg);
            }
        }

        /// <summary>Check if a companion is in Follow mode (mode 0).</summary>
        private static bool IsFollowMode(CompanionRest rest)
        {
            var nview = rest.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;
            int mode = nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            return mode == CompanionSetup.ModeFollow;
        }

        private static string GetCompanionName(CompanionRest rest)
        {
            var nview = rest.GetComponent<ZNetView>();
            if (nview != null && nview.GetZDO() != null)
            {
                string custom = nview.GetZDO().GetString(CompanionSetup.NameHash, "");
                if (!string.IsNullOrEmpty(custom)) return custom;
            }
            var character = rest.GetComponent<Character>();
            return character != null ? character.m_name : "Companion";
        }
    }
}
