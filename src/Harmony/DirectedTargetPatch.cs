using HarmonyLib;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Universal companion command: press the hotkey while looking at an object
    /// to direct all owned companions to interact with it.
    ///
    /// Supported targets (checked in priority order):
    ///   Enemy Character  → attack
    ///   Vagon (cart)     → attach / detach
    ///   Door             → open
    ///   Fireplace        → sit nearby
    ///   Bed              → go to bed
    ///   Harvestable      → harvest (TreeBase, TreeLog, MineRock, MineRock5, Destructible)
    ///   Nothing          → cancel all directed commands
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class DirectedTargetPatch
    {
        private static float _cooldown;

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (__instance.IsDead()) return;
            if (InventoryGui.IsVisible() || Minimap.IsOpen() || Menu.IsVisible()) return;
            if (TextInput.IsVisible()) return;

            _cooldown -= Time.deltaTime;
            if (_cooldown > 0f) return;

            // Keyboard — configurable hotkey (default Z)
            bool keyboardTriggered = !ZInput.IsGamepadActive()
                && Input.GetKeyDown(CompanionsPlugin.DirectTargetKey.Value);

            // Controller — X button, but only when no interact prompt is on screen
            bool gamepadPressed = ZInput.IsGamepadActive()
                && ZInput.GetButtonDown("JoyUse");
            bool gamepadTriggered = false;
            if (gamepadPressed)
            {
                var hoverObj = __instance.GetHoverObject();
                if (hoverObj != null)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Direct] Gamepad X suppressed — interact prompt on \"{hoverObj.name}\"");
                }
                else if (Hud.InRadial())
                {
                    CompanionsPlugin.Log.LogDebug("[Direct] Gamepad X suppressed — radial menu open");
                }
                else
                {
                    gamepadTriggered = true;
                }
            }

            if (!keyboardTriggered && !gamepadTriggered) return;
            _cooldown = 0.5f;

            string inputSource = gamepadTriggered ? "gamepad" : "keyboard";
            CompanionsPlugin.Log.LogInfo($"[Direct] Command triggered via {inputSource}");

            // Raycast from camera — broad layer mask to hit all interactable objects
            Camera mainCam = Utils.GetMainCamera();
            if (mainCam == null) return;

            Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
            int layerMask = LayerMask.GetMask(
                "character", "character_net", "character_ghost", "character_noenv",
                "Default", "piece", "piece_nonsolid", "static_solid", "terrain");

            RaycastHit[] hits = Physics.RaycastAll(ray, 50f, layerMask);
            if (hits.Length > 1)
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            CompanionsPlugin.Log.LogInfo(
                $"[Direct] Raycast hits={hits.Length}, pos={mainCam.transform.position:F1}, dir={mainCam.transform.forward:F2}");

            // Gather owned companions
            var setups = Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None);
            string localId = __instance.GetPlayerID().ToString();

            // Scan hits in distance order — first meaningful target wins
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;

                // ── Enemy Character ─────────────────────────────────
                var character = col.GetComponentInParent<Character>();
                if (character != null)
                {
                    if (character == __instance) continue; // skip self
                    if (character.GetComponent<CompanionSetup>() != null) continue; // skip companions

                    if (!character.IsDead() && BaseAI.IsEnemy(__instance, character))
                    {
                        DirectAttack(setups, localId, character);
                        return;
                    }
                    continue; // non-enemy character — skip, check further hits
                }

                // ── Vagon (Cart) ────────────────────────────────────
                var vagon = col.GetComponentInParent<Vagon>();
                if (vagon != null)
                {
                    DirectCart(setups, localId, vagon);
                    return;
                }

                // ── Door ────────────────────────────────────────────
                var door = col.GetComponentInParent<Door>();
                if (door != null)
                {
                    DirectDoor(setups, localId, door);
                    return;
                }

                // ── Fireplace ───────────────────────────────────────
                var fire = col.GetComponentInParent<Fireplace>();
                if (fire != null)
                {
                    DirectSit(setups, localId, fire);
                    return;
                }

                // ── Bed ─────────────────────────────────────────────
                var bed = col.GetComponentInParent<Bed>();
                if (bed != null)
                {
                    DirectSleep(setups, localId, bed);
                    return;
                }

                // ── Harvestable (tree, rock, ore) ───────────────────
                var harvestGO = GetHarvestable(col);
                if (harvestGO != null)
                {
                    DirectHarvest(setups, localId, harvestGO);
                    return;
                }
            }

            // No valid target found — cancel all directed commands
            CancelAll(setups, localId);
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Command dispatchers
        // ═════════════════════════════════════════════════════════════════════

        private static void DirectAttack(CompanionSetup[] setups, string localId, Character enemy)
        {
            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;
                if (setup.GetCombatStance() == CompanionSetup.StancePassive) continue;

                ai.m_targetCreature = enemy;
                ai.SetAlerted(true);
                ai.DirectedTargetLockTimer = 10f;
                directed++;
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"Attack: {enemy.m_name}!");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] {directed} companion(s) → attack \"{enemy.m_name}\"");
        }

        private static void DirectCart(CompanionSetup[] setups, string localId, Vagon vagon)
        {
            // Find closest owned companion to the cart
            CompanionSetup closest = null;
            float closestDist = float.MaxValue;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                float d = Vector3.Distance(setup.transform.position, vagon.transform.position);
                if (d < closestDist) { closestDist = d; closest = setup; }
            }

            if (closest == null) return;

            var humanoid = closest.GetComponent<Humanoid>();
            if (humanoid == null) return;

            // Vagon.Interact takes a Humanoid — toggles attach/detach
            vagon.Interact(humanoid, false, false);

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Companion: Pull cart");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] Companion → cart interact (dist={closestDist:F1}m)");
        }

        private static void DirectDoor(CompanionSetup[] setups, string localId, Door door)
        {
            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var handler = setup.GetComponent<DoorHandler>();
                if (handler == null) continue;

                handler.DirectOpenDoor(door);
                directed++;
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Companion: Open door");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] {directed} companion(s) → open door \"{door.m_name}\"");
        }

        private static void DirectSit(CompanionSetup[] setups, string localId, Fireplace fire)
        {
            if (!fire.IsBurning())
            {
                MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                    "Fire is not burning");
                return;
            }

            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var rest = setup.GetComponent<CompanionRest>();
                if (rest == null) continue;

                rest.DirectSit(fire.gameObject);
                directed++;
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Companion: Rest here");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] {directed} companion(s) → sit near fire");
        }

        private static void DirectSleep(CompanionSetup[] setups, string localId, Bed bed)
        {
            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var rest = setup.GetComponent<CompanionRest>();
                if (rest == null) continue;

                rest.DirectSleep(bed);
                directed++;
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Companion: Sleep");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] {directed} companion(s) → sleep at bed");
        }

        private static void DirectHarvest(CompanionSetup[] setups, string localId, GameObject target)
        {
            // Find closest owned companion
            CompanionSetup closest = null;
            float closestDist = float.MaxValue;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                float d = Vector3.Distance(setup.transform.position, target.transform.position);
                if (d < closestDist) { closestDist = d; closest = setup; }
            }

            if (closest == null) return;

            var harvest = closest.GetComponent<HarvestController>();
            if (harvest == null) return;

            harvest.SetDirectedTarget(target);

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                $"Harvest: {target.name}");
            CompanionsPlugin.Log.LogInfo(
                $"[Direct] Companion → harvest \"{target.name}\" (dist={closestDist:F1}m)");
        }

        private static void CancelAll(CompanionSetup[] setups, string localId)
        {
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;

                var ai = setup.GetComponent<CompanionAI>();
                if (ai != null)
                    ai.DirectedTargetLockTimer = 0f;

                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null)
                    harvest.CancelDirectedTarget();

                var rest = setup.GetComponent<CompanionRest>();
                if (rest != null)
                    rest.CancelDirected();
            }

            MessageHud.instance?.ShowMessage(MessageHud.MessageType.Center,
                "Companions: Free");
            CompanionsPlugin.Log.LogInfo("[Direct] Cancelled all directed commands");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static bool IsOwned(CompanionSetup setup, string localId)
        {
            var nview = setup.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;
            return nview.GetZDO().GetString(CompanionSetup.OwnerHash, "") == localId;
        }

        /// <summary>
        /// Checks if a collider belongs to a harvestable object.
        /// Matches the same types as HarvestController.GetHarvestCandidateWithType.
        /// </summary>
        private static GameObject GetHarvestable(Collider col)
        {
            var tree = col.GetComponentInParent<TreeBase>();
            if (tree != null) return tree.gameObject;

            var log = col.GetComponentInParent<TreeLog>();
            if (log != null) return log.gameObject;

            var rock5 = col.GetComponentInParent<MineRock5>();
            if (rock5 != null) return rock5.gameObject;

            var rock = col.GetComponentInParent<MineRock>();
            if (rock != null) return rock.gameObject;

            var dest = col.GetComponentInParent<Destructible>();
            if (dest != null)
            {
                // Stumps (wood)
                if (dest.m_damages.m_chop != HitData.DamageModifier.Immune
                    && dest.gameObject.name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return dest.gameObject;

                // Rock/ore destructibles (pickaxe, not chop)
                if (dest.m_damages.m_pickaxe != HitData.DamageModifier.Immune
                    && dest.m_damages.m_chop == HitData.DamageModifier.Immune)
                    return dest.gameObject;
            }

            return null;
        }
    }
}
