using System.Collections.Generic;
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
    ///   Container        → deposit resources
    ///   Harvestable      → harvest (TreeBase, TreeLog, MineRock, MineRock5, Destructible)
    ///   Nothing          → cancel all directed commands
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    internal static class DirectedTargetPatch
    {
        private static float _cooldown;

        // ── Hold-to-cancel detection ────────────────────────────────────────
        private static bool  _holdActive;       // tracking a press
        private static float _holdTimer;         // how long held so far
        private static bool  _holdFired;         // already fired cancel-all
        private const  float HoldThreshold = 0.4f; // seconds to trigger come-to-me

        private static readonly string[] ComeHereLines = {
            "Coming!", "On my way back!", "Right behind you."
        };

        // ── Speech pools ──────────────────────────────────────────────────────
        private static readonly string[] AttackLines = {
            "On it!", "Going in!", "I'll take them down!", "For Odin!"
        };
        private static readonly string[] CartPullLines = {
            "I'll haul this.", "Got the cart!", "Let me pull."
        };
        private static readonly string[] CartReleasedLines = {
            "Letting go.", "Cart's free.", "Released!"
        };
        private static readonly string[] DoorLines = {
            "Getting the door.", "I'll get it.", "Door's open!"
        };
        private static readonly string[] SitLines = {
            "Nice and warm.", "Good spot to rest.", "I'll sit here."
        };
        private static readonly string[] SleepLines = {
            "Time for some rest.", "I could use some sleep.", "Wake me if you need me."
        };
        private static readonly string[] WakeLines = {
            "I'm up!", "Already?", "Right, let's go."
        };
        private static readonly string[] DepositLines = {
            "Dropping off my haul.", "Storing the goods.", "Lightening my load."
        };
        private static readonly string[] DepositEmptyLines = {
            "I've got nothing to drop off.", "Already empty."
        };
        private static readonly string[] HarvestLines = {
            "I'll get that.", "On it!", "Looks like good stuff."
        };
        private static readonly string[] CancelLines = {
            "Standing by.", "Awaiting orders.", "Ready when you are."
        };
        private static readonly string[] MoveLines = {
            "Heading over.", "On my way.", "Moving out."
        };
        private static readonly string[] RepairLines = {
            "I'll fix my gear up.", "Time for repairs.", "This needs some work."
        };
        private static readonly string[] BoardLines = {
            "Coming aboard!", "All aboard!", "I'll hop on."
        };

        private static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            if (__instance.IsDead()) return;
            if (InventoryGui.IsVisible() || Minimap.IsOpen() || Menu.IsVisible()) return;
            if (TextInput.IsVisible() || Console.IsVisible() || StoreGui.IsVisible()) return;
            if (Chat.instance != null && Chat.instance.HasFocus()) return;
            if (Hud.IsPieceSelectionVisible()) return;

            float dt = Time.deltaTime;
            _cooldown -= dt;

            // ── Detect button state ──────────────────────────────────────
            bool isGamepad = ZInput.IsGamepadActive();

            bool buttonDown = isGamepad
                ? ZInput.GetButtonDown("JoyUse")
                : Input.GetKeyDown(CompanionsPlugin.DirectTargetKey.Value);

            bool buttonHeld = isGamepad
                ? ZInput.GetButton("JoyUse")
                : Input.GetKey(CompanionsPlugin.DirectTargetKey.Value);

            // Suppress gamepad when interact prompt or radial is visible
            if (isGamepad && (buttonDown || buttonHeld))
            {
                var hoverObj = __instance.GetHoverObject();
                if (hoverObj != null || Hud.InRadial())
                {
                    _holdActive = false;
                    _holdTimer = 0f;
                    return;
                }
            }

            // ── Hold tracking ────────────────────────────────────────────
            if (buttonDown && _cooldown <= 0f)
            {
                _holdActive = true;
                _holdTimer = 0f;
                _holdFired = false;
            }

            if (_holdActive && buttonHeld)
            {
                _holdTimer += dt;

                // Long press — cancel all, come to me
                if (_holdTimer >= HoldThreshold && !_holdFired)
                {
                    _holdFired = true;
                    _cooldown = 0.5f;
                    FireComeToMe(__instance, isGamepad);
                    return;
                }
                return; // still holding — wait
            }

            // Button released or not held
            if (_holdActive)
            {
                _holdActive = false;

                // If hold already fired cancel-all, don't also fire a tap command
                if (_holdFired)
                    return;

                // Short press — normal directed command
                if (_cooldown > 0f) return;
                _cooldown = 0.5f;
            }
            else
            {
                // No hold was active, no button pressed
                return;
            }

            string source = isGamepad ? "gamepad" : "keyboard";
            CompanionsPlugin.Log.LogDebug($"[Direct] Command triggered via {source}");

            // Raycast from camera — broad layer mask to hit all interactable objects
            Camera mainCam = Utils.GetMainCamera();
            if (mainCam == null) return;

            Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
            // Matches Player.m_interactMask + character_ghost/character_noenv
            int layerMask = LayerMask.GetMask(
                "character", "character_net", "character_ghost", "character_noenv",
                "Default", "Default_small", "piece", "piece_nonsolid",
                "static_solid", "terrain", "vehicle", "item");

            RaycastHit[] hits = Physics.RaycastAll(ray, 50f, layerMask);
            if (hits.Length > 1)
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            CompanionsPlugin.Log.LogDebug(
                $"[Direct] Raycast hits={hits.Length}, pos={mainCam.transform.position:F1}, dir={mainCam.transform.forward:F2}");

            // Gather owned companions
            var setups = Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None);
            string localId = __instance.GetPlayerID().ToString();

            // Scan hits in distance order — first meaningful target wins
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;

                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Hit[{i}]: \"{col.gameObject.name}\" dist={hits[i].distance:F2} " +
                    $"layer={LayerMask.LayerToName(col.gameObject.layer)} " +
                    $"pos={hits[i].point:F2}");

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

                // ── CraftingStation (forge, workbench) ────────────
                var station = col.GetComponentInParent<CraftingStation>();
                if (station != null)
                {
                    DirectRepair(setups, localId, station);
                    return;
                }

                // ── Ship (boat) ───────────────────────────────────
                var ship = col.GetComponentInParent<Ship>();
                if (ship != null)
                {
                    DirectBoard(setups, localId, ship);
                    return;
                }

                // ── Container (chest) ──────────────────────────────
                var container = col.GetComponentInParent<Container>();
                if (container != null && container.GetComponent<CompanionSetup>() == null)
                {
                    DirectDeposit(setups, localId, container);
                    return;
                }

                // ── Harvestable (tree, rock, ore) ───────────────────
                var harvestGO = GetHarvestable(col);
                if (harvestGO != null)
                {
                    DirectGatherMode(setups, localId, harvestGO);
                    return;
                }

                // ── Ground / terrain / building surface — move to position or exit gather ──
                int layer = col.gameObject.layer;
                if (layer == LayerMask.NameToLayer("terrain") ||
                    layer == LayerMask.NameToLayer("Default") ||
                    layer == LayerMask.NameToLayer("static_solid") ||
                    layer == LayerMask.NameToLayer("piece") ||
                    layer == LayerMask.NameToLayer("piece_nonsolid"))
                {
                    DirectGround(setups, localId, hits[i].point);
                    return;
                }
            }

            // No valid target found — cancel all directed commands
            CancelAll(setups, localId);
        }

        /// <summary>
        /// Hold/long-press handler — cancel all actions and restore follow.
        /// </summary>
        private static void FireComeToMe(Player player, bool isGamepad)
        {
            var setups = Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None);
            string localId = player.GetPlayerID().ToString();

            CancelAll(setups, localId);

            // Override the CancelAll speech with "come here" speech
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                firstTalk = setup.GetComponent<CompanionTalk>();
                if (firstTalk != null) break;
            }
            SayRandom(firstTalk, ComeHereLines, "Action");

            string inputSource = isGamepad ? "gamepad hold" : "keyboard hold";
            CompanionsPlugin.Log.LogDebug($"[Direct] Come-to-me triggered via {inputSource}");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Command dispatchers
        // ═════════════════════════════════════════════════════════════════════

        private static void DirectAttack(CompanionSetup[] setups, string localId, Character enemy)
        {
            int directed = 0;
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;
                CancelExistingActions(setup);
                ai.m_targetCreature = enemy;
                ai.SetAlerted(true);
                ai.DirectedTargetLockTimer = 10f;
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }

            SayRandom(firstTalk, AttackLines, "Combat");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] {directed} companion(s) → attack \"{enemy.m_name}\"");
        }

        private static void DirectCart(CompanionSetup[] setups, string localId, Vagon vagon)
        {
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] DirectCart called — vagon=\"{vagon.name}\" " +
                $"attachPoint={vagon.m_attachPoint.position:F2} " +
                $"attachOffset={vagon.m_attachOffset:F2} " +
                $"detachDist={vagon.m_detachDistance:F2}");

            // Check if any owned companion is already attached — detach them
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var character = setup.GetComponent<Character>();
                bool attached = character != null && vagon.IsAttached(character);
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Cart detach check: \"{character?.m_name}\" attached={attached}");
                if (attached)
                {
                    var humanoid = setup.GetComponent<Humanoid>();
                    if (humanoid != null)
                    {
                        vagon.Interact(humanoid, false, false);

                        // Restore follow target to player
                        var ai = setup.GetComponent<CompanionAI>();
                        if (ai != null && Player.m_localPlayer != null)
                            ai.SetFollowTarget(Player.m_localPlayer.gameObject);

                        SayRandom(setup.GetComponent<CompanionTalk>(), CartReleasedLines, "Action");
                        CompanionsPlugin.Log.LogDebug("[Direct] Companion → detach from cart");
                    }
                    return;
                }
            }

            // Find closest commandable owned companion to the cart
            CompanionSetup closest = null;
            float closestDist = float.MaxValue;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                float d = Vector3.Distance(setup.transform.position, vagon.transform.position);
                if (d < closestDist) { closestDist = d; closest = setup; }
            }

            if (closest == null)
            {
                CompanionsPlugin.Log.LogWarning("[Direct] DirectCart — no commandable companion found");
                return;
            }

            var closestHumanoid = closest.GetComponent<Humanoid>();
            if (closestHumanoid == null) return;

            var closestAI = closest.GetComponent<CompanionAI>();
            if (closestAI == null) return;

            CancelExistingActions(closest);

            // Compute attach position
            Vector3 attachWorldPos = vagon.m_attachPoint.position
                - vagon.m_attachOffset;
            attachWorldPos.y = closest.transform.position.y;
            float distToAttach = Vector3.Distance(closest.transform.position, attachWorldPos);

            if (distToAttach < 3f)
            {
                // Already close — snap and interact immediately
                // Sync both transform and Rigidbody to prevent physics override
                closest.transform.position = attachWorldPos;
                var body = closest.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = attachWorldPos;
                    body.velocity = Vector3.zero;
                }

                Vector3 toCart = vagon.transform.position - closest.transform.position;
                toCart.y = 0f;
                if (toCart.sqrMagnitude > 0.01f)
                    closest.transform.rotation = Quaternion.LookRotation(toCart.normalized);

                closestAI.FreezeTimer = 1f;
                closestAI.SetFollowTarget(vagon.gameObject);
                vagon.Interact(closestHumanoid, false, false);

                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Cart close snap — pos={closest.transform.position:F2}");
            }
            else
            {
                // Navigate to cart — companion walks there first
                closestAI.SetPendingCart(vagon, closestHumanoid);
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Cart navigation started — dist={distToAttach:F1}m");
            }

            SayRandom(closest.GetComponent<CompanionTalk>(), CartPullLines, "Action");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] Companion → cart attach (dist={closestDist:F1}m)");
        }

        private static void DirectDoor(CompanionSetup[] setups, string localId, Door door)
        {
            int directed = 0;
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup);
                var handler = setup.GetComponent<DoorHandler>();
                if (handler == null) continue;

                handler.DirectOpenDoor(door);
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }

            SayRandom(firstTalk, DoorLines, "Action");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] {directed} companion(s) → open door \"{door.m_name}\"");
        }

        private static void DirectSit(CompanionSetup[] setups, string localId, Fireplace fire)
        {
            if (!fire.IsBurning()) return;

            int directed = 0;
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup, cancelRest: false);
                var rest = setup.GetComponent<CompanionRest>();
                if (rest == null) continue;

                rest.DirectSit(fire.gameObject);
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }

            SayRandom(firstTalk, SitLines, "Idle");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] {directed} companion(s) → sit near fire");
        }

        private static void DirectSleep(CompanionSetup[] setups, string localId, Bed bed)
        {
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] DirectSleep called — bed=\"{bed.name}\" " +
                $"pos={bed.transform.position:F2} " +
                $"spawnPoint={bed.m_spawnPoint?.position.ToString("F2") ?? "null"}");

            int started = 0;
            int wokeUp = 0;
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup, cancelRest: false);
                var rest = setup.GetComponent<CompanionRest>();
                if (rest == null) continue;

                bool wasSleeping = rest.IsResting || rest.IsNavigating;

                rest.DirectSleep(bed);

                bool isSleeping = rest.IsResting || rest.IsNavigating;

                if (!wasSleeping && isSleeping) started++;
                else if (wasSleeping && !isSleeping) wokeUp++;

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
            }

            if (wokeUp > 0)
                SayRandom(firstTalk, WakeLines, "Action");
            else if (started > 0)
                SayRandom(firstTalk, SleepLines, "Idle");
            else
                SayRandom(firstTalk, CancelLines, "Action");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] Sleep command — started={started}, wokeUp={wokeUp}");
        }

        private static void DirectDeposit(CompanionSetup[] setups, string localId, Container chest)
        {
            var chestInv = chest.GetInventory();
            if (chestInv == null) return;

            int dispatched = 0;
            CompanionTalk firstTalk = null;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                var humanoid = setup.GetComponent<Humanoid>();
                if (humanoid == null) continue;
                var compInv = humanoid.GetInventory();
                if (compInv == null) continue;

                // Check if companion has anything to deposit before walking
                bool hasDepositable = false;
                foreach (var item in compInv.GetAllItems())
                {
                    if (!ShouldKeep(item, humanoid))
                    {
                        hasDepositable = true;
                        break;
                    }
                }

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();

                if (!hasDepositable)
                    continue;

                CancelExistingActions(setup);

                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;

                ai.SetPendingDeposit(chest, humanoid);
                dispatched++;
            }

            if (dispatched > 0)
            {
                SayRandom(firstTalk, DepositLines, "Action");
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Dispatched {dispatched} companion(s) to deposit in \"{chest.m_name}\"");
            }
            else
            {
                SayRandom(firstTalk, DepositEmptyLines, "Action");
                CompanionsPlugin.Log.LogDebug("[Direct] No companions had items to deposit");
            }
        }

        private static readonly string[] RepairNothingLines = {
            "Nothing to fix here.", "My gear's fine.", "No repairs needed."
        };

        private static void DirectRepair(CompanionSetup[] setups, string localId, CraftingStation station)
        {
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] DirectRepair called — station=\"{station.m_name}\" " +
                $"level={station.GetLevel()} pos={station.transform.position:F1}");

            CompanionTalk firstTalk = null;
            int directed = 0;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                var repair = setup.GetComponent<RepairController>();
                if (repair == null) continue;

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();

                CancelExistingActions(setup);

                if (repair.DirectRepairAt(station))
                    directed++;
            }

            if (directed > 0)
            {
                SayRandom(firstTalk, RepairLines, "Repair");
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] {directed} companion(s) → repair at \"{station.m_name}\"");
            }
            else
            {
                SayRandom(firstTalk, RepairNothingLines, "Repair");
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] No companions can repair at \"{station.m_name}\"");
            }
        }

        private static void DirectBoard(CompanionSetup[] setups, string localId, Ship ship)
        {
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] DirectBoard called — ship=\"{ship.name}\" " +
                $"pos={ship.transform.position:F2} up={ship.transform.up:F2}");

            CompanionTalk firstTalk = null;
            int boarded = 0;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup);

                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;

                Vector3 oldPos = setup.transform.position;

                // Teleport onto ship deck
                Vector3 deckPos = ship.transform.position + ship.transform.up * 1.5f;
                setup.transform.position = deckPos;
                var body = setup.GetComponent<Rigidbody>();
                if (body != null)
                {
                    body.position = deckPos;
                    body.velocity = Vector3.zero;
                }

                // Follow the ship so they stay onboard
                ai.SetFollowTarget(ship.gameObject);
                ai.FreezeTimer = 1f;

                var character = setup.GetComponent<Character>();
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] Board: \"{character?.m_name ?? "?"}\" " +
                    $"teleported {oldPos:F1} → {deckPos:F1}");

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                boarded++;
            }

            SayRandom(firstTalk, BoardLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] {boarded} companion(s) → board ship");
        }

        private static void DirectGatherMode(CompanionSetup[] setups, string localId, GameObject target)
        {
            int harvestMode = HarvestController.DetermineHarvestModeStatic(target);
            if (harvestMode < 0)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] DirectGatherMode — target \"{target.name}\" is not harvestable (mode=-1)");
                return;
            }

            string modeName = harvestMode == CompanionSetup.ModeGatherWood ? "Wood"
                            : harvestMode == CompanionSetup.ModeGatherStone ? "Stone" : "Ore";
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] DirectGatherMode — target=\"{target.name}\" mode={modeName} pos={target.transform.position:F1}");

            CompanionTalk firstTalk = null;
            int directed = 0;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup);

                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) continue;

                // Set ZDO ActionMode to the matching gather mode
                nview.GetZDO().Set(CompanionSetup.ActionModeHash, harvestMode);

                // Direct the first companion to harvest this specific target immediately
                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null && directed == 0)
                    harvest.SetDirectedTarget(target);

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }

            SayRandom(firstTalk, HarvestLines, "Gather");
            CompanionsPlugin.Log.LogDebug(
                $"[Direct] {directed} companion(s) → gather mode {modeName}");
        }

        private static void DirectGround(CompanionSetup[] setups, string localId, Vector3 point)
        {
            CompanionsPlugin.Log.LogDebug($"[Direct] DirectGround called — point={point:F1}");

            // Check if any owned companion is in gather mode
            bool anyGathering = false;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null && harvest.IsInGatherMode) { anyGathering = true; break; }
            }

            if (anyGathering)
            {
                CompanionsPlugin.Log.LogDebug("[Direct] DirectGround — companion(s) in gather mode, exiting gather instead");
                ExitGatherMode(setups, localId);
                return;
            }

            // Move all commandable companions to the ground point
            CompanionTalk firstTalk = null;
            int directed = 0;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                CancelExistingActions(setup);

                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;

                ai.SetMoveTarget(point);
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }

            SayRandom(firstTalk, MoveLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → move to {point:F1}");
        }

        private static void ExitGatherMode(CompanionSetup[] setups, string localId)
        {
            CompanionTalk firstTalk = null;
            int exited = 0;

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;

                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) continue;

                int oldMode = nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);

                var character = setup.GetComponent<Character>();
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] ExitGather: \"{character?.m_name ?? "?"}\" mode {oldMode} → {CompanionSetup.ModeFollow}");

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                exited++;
            }

            SayRandom(firstTalk, CancelLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] {exited} companion(s) exited gather mode → follow");
        }

        private static void CancelAll(CompanionSetup[] setups, string localId)
        {
            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();

                var ai = setup.GetComponent<CompanionAI>();
                if (ai != null)
                {
                    ai.DirectedTargetLockTimer = 0f;
                    ai.CancelPendingCart();
                    ai.CancelMoveTarget();
                    ai.CancelPendingDeposit();

                    // Restore follow target to player if Follow toggle is ON.
                    // Handles ship disembark, move-to leftovers, or any other state
                    // where follow was pointed at something other than the player.
                    if (Player.m_localPlayer != null && setup.GetFollow())
                        ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    else if (!setup.GetFollow())
                        ai.SetFollowTarget(null);
                }

                // Reset action mode to Follow — fully exits gather/smelt modes
                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() != null)
                {
                    int mode = nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                    if (mode != CompanionSetup.ModeFollow)
                        nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                }

                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null)
                    harvest.NotifyActionModeChanged();

                var rest = setup.GetComponent<CompanionRest>();
                if (rest != null)
                    rest.CancelDirected();

                var repair = setup.GetComponent<RepairController>();
                if (repair != null && repair.IsActive)
                    repair.CancelDirected();

                var smelt = setup.GetComponent<SmeltController>();
                if (smelt != null)
                    smelt.NotifyActionModeChanged();
            }

            SayRandom(firstTalk, CancelLines, "Action");
            CompanionsPlugin.Log.LogDebug("[Direct] Cancelled all directed commands");
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Helpers
        // ═════════════════════════════════════════════════════════════════════

        private static void SayRandom(CompanionTalk talk, string[] pool, string audioCategory = null)
        {
            if (talk == null || pool == null || pool.Length == 0) return;
            talk.Say(pool[Random.Range(0, pool.Length)], audioCategory);
        }

        private static bool IsOwned(CompanionSetup setup, string localId)
        {
            var nview = setup.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;
            return nview.GetZDO().GetString(CompanionSetup.OwnerHash, "") == localId;
        }

        /// <summary>
        /// Cancel all existing actions on a companion so a new command can take over.
        /// Called at the start of each DirectXxx method to preempt sleep, sit,
        /// cart navigation, move-to, etc.
        /// </summary>
        private static void CancelExistingActions(CompanionSetup setup, bool cancelRest = true)
        {
            var character = setup.GetComponent<Character>();
            string name = character?.m_name ?? "?";

            if (cancelRest)
            {
                var rest = setup.GetComponent<CompanionRest>();
                if (rest != null && (rest.IsResting || rest.IsNavigating))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Direct] CancelExisting \"{name}\": cancelling rest " +
                        $"(resting={rest.IsResting} nav={rest.IsNavigating})");
                    rest.CancelDirected();
                }
            }

            var ai = setup.GetComponent<CompanionAI>();
            if (ai != null)
            {
                if (ai.PendingCartAttach != null)
                    CompanionsPlugin.Log.LogDebug($"[Direct] CancelExisting \"{name}\": cancelling cart nav");
                ai.CancelPendingCart();

                if (ai.PendingMoveTarget != null)
                    CompanionsPlugin.Log.LogDebug($"[Direct] CancelExisting \"{name}\": cancelling move-to");
                ai.CancelMoveTarget();

                if (ai.PendingDepositContainer != null)
                    CompanionsPlugin.Log.LogDebug($"[Direct] CancelExisting \"{name}\": cancelling deposit nav");
                ai.CancelPendingDeposit();

                ai.DirectedTargetLockTimer = 0f;
            }

            var harvest = setup.GetComponent<HarvestController>();
            if (harvest != null)
                harvest.CancelDirectedTarget();

            var repair = setup.GetComponent<RepairController>();
            if (repair != null && repair.IsActive)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] CancelExisting \"{name}\": cancelling active repair");
                repair.CancelDirected();
            }

            var smelt = setup.GetComponent<SmeltController>();
            if (smelt != null && smelt.IsActive)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Direct] CancelExisting \"{name}\": cancelling active smelt");
                smelt.CancelDirected();
            }
        }

        /// <summary>
        /// Returns true for items the companion should keep (not deposit).
        /// Keeps: equipped items, food, weapons, armor, shields, utility.
        /// Deposits: materials, misc, trophies, tools, etc.
        /// </summary>
        internal static bool ShouldKeep(ItemDrop.ItemData item, Humanoid humanoid)
        {
            if (item == null || item.m_shared == null) return true;

            // Keep anything currently equipped
            if (humanoid.IsItemEquiped(item)) return true;

            var t = item.m_shared.m_itemType;

            // Keep food (consumables with food stats)
            if (t == ItemDrop.ItemData.ItemType.Consumable &&
                (item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f ||
                 item.m_shared.m_foodEitr > 0f))
                return true;

            // Keep weapons (even unequipped backups)
            if (t == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                t == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                t == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                t == ItemDrop.ItemData.ItemType.Bow ||
                t == ItemDrop.ItemData.ItemType.Torch)
                return true;

            // Keep armor and shields
            if (t == ItemDrop.ItemData.ItemType.Shield ||
                t == ItemDrop.ItemData.ItemType.Helmet ||
                t == ItemDrop.ItemData.ItemType.Chest ||
                t == ItemDrop.ItemData.ItemType.Legs ||
                t == ItemDrop.ItemData.ItemType.Hands ||
                t == ItemDrop.ItemData.ItemType.Shoulder ||
                t == ItemDrop.ItemData.ItemType.Utility)
                return true;

            // Deposit everything else (Material, Misc, Trophy, Tool, etc.)
            return false;
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
