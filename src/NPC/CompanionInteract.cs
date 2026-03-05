using System.Reflection;
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
    ///
    /// When RadialMenuKey == E (default):
    ///   Tap E  → opens inventory panel (via Container → InventoryGui)
    ///   Hold E → opens radial command wheel
    ///
    /// When RadialMenuKey != E:
    ///   E      → opens inventory directly (no tap/hold)
    ///   Key    → opens radial when hovering a companion
    /// </summary>
    public class CompanionInteract : MonoBehaviour
    {
        private ZNetView  _nview;
        private Character _character;

        // ── Pending-tap state (static — only one interact at a time) ──
        private static Container _pendingTapContainer;
        private static Player    _pendingTapPlayer;
        private static float     _pendingTapTime;

        // ── Gamepad hold detection (prefix-based, legacy) ──
        private static bool _pendingIsGamepad;
        private static bool _gamepadReleaseDetected;

        // ── Independent gamepad hold detection ──
        // Bypasses the Container.Interact prefix chain entirely.
        // Monitors ZInput.GetButton("JoyUse") directly while hovering a companion.
        private static bool           _gpHoldActive;
        private static float          _gpHoldStart;
        private static CompanionSetup _gpHoldTarget;

        private const float HoldThreshold = 0.2f;

        // ── Player hover reflection (for separate-key radial detection) ──
        private static readonly FieldInfo _playerHoveringField =
            AccessTools.Field(typeof(Player), "m_hovering");

        // ── Direct command (Z key / gamepad) — hold-to-follow, tap-to-command ──
        private static int   _directCommandFrame = -1;
        private static float _cmdCooldown;
        private static bool  _holdActive;
        private static float _holdTimer;
        private static bool  _holdFired;
        private const  float CmdHoldThreshold = 0.4f;

        // ── Diagnostic logging ──
        private static bool _loggedPendingStart;

        /// <summary>True when RadialMenuKey is the same as the vanilla Use key (E).</summary>
        private static bool IsRadialKeyUse =>
            ModConfig.RadialMenuKey.Value == KeyCode.E;

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _character = GetComponent<Character>();
        }

        private void Update()
        {
            // ── Direct command (Z key) — runs once per frame via frame guard ──
            HandleDirectCommand();

            // ── Independent gamepad hold detection (runs for all modes) ──
            // Only the instance whose companion is being hovered will act.
            if (ZInput.IsGamepadActive())
                HandleGamepadHold();

            // ── Separate-key radial: press configured key while hovering companion ──
            if (!IsRadialKeyUse)
            {
                HandleSeparateKeyRadial();
                // Still process any lingering pending taps from the prefix
                if (!_gpHoldActive) ProcessPendingTap();
                return;
            }

            // ── Same-key mode (E): tap/hold detection ──
            // Suppress prefix-based ProcessPendingTap while independent gamepad hold is active
            if (!_gpHoldActive) ProcessPendingTap();
        }

        // ── Speech pools (lazy-loaded, localized) ──
        private static string[] _comeHereLines;
        private static string[] ComeHereLines => _comeHereLines ?? (_comeHereLines = new[] {
            ModLocalization.Loc("hc_cmd_comehere_1"),
            ModLocalization.Loc("hc_cmd_comehere_2"),
            ModLocalization.Loc("hc_cmd_comehere_3")
        });
        private static string[] _attackLines;
        private static string[] AttackLines => _attackLines ?? (_attackLines = new[] {
            ModLocalization.Loc("hc_cmd_attack_1"),
            ModLocalization.Loc("hc_cmd_attack_2"),
            ModLocalization.Loc("hc_cmd_attack_3"),
            ModLocalization.Loc("hc_cmd_attack_4")
        });
        private static string[] _cartPullLines;
        private static string[] CartPullLines => _cartPullLines ?? (_cartPullLines = new[] {
            ModLocalization.Loc("hc_cmd_cart_pull_1"),
            ModLocalization.Loc("hc_cmd_cart_pull_2"),
            ModLocalization.Loc("hc_cmd_cart_pull_3")
        });
        private static string[] _cartReleasedLines;
        private static string[] CartReleasedLines => _cartReleasedLines ?? (_cartReleasedLines = new[] {
            ModLocalization.Loc("hc_cmd_cart_release_1"),
            ModLocalization.Loc("hc_cmd_cart_release_2"),
            ModLocalization.Loc("hc_cmd_cart_release_3")
        });
        private static string[] _doorLines;
        private static string[] DoorLines => _doorLines ?? (_doorLines = new[] {
            ModLocalization.Loc("hc_cmd_door_1"),
            ModLocalization.Loc("hc_cmd_door_2"),
            ModLocalization.Loc("hc_cmd_door_3")
        });
        private static string[] _sitLines;
        private static string[] SitLines => _sitLines ?? (_sitLines = new[] {
            ModLocalization.Loc("hc_cmd_sit_1"),
            ModLocalization.Loc("hc_cmd_sit_2"),
            ModLocalization.Loc("hc_cmd_sit_3")
        });
        private static string[] _sleepLines;
        private static string[] SleepLines => _sleepLines ?? (_sleepLines = new[] {
            ModLocalization.Loc("hc_cmd_sleep_1"),
            ModLocalization.Loc("hc_cmd_sleep_2"),
            ModLocalization.Loc("hc_cmd_sleep_3")
        });
        private static string[] _wakeLines;
        private static string[] WakeLines => _wakeLines ?? (_wakeLines = new[] {
            ModLocalization.Loc("hc_cmd_wake_1"),
            ModLocalization.Loc("hc_cmd_wake_2"),
            ModLocalization.Loc("hc_cmd_wake_3")
        });
        private static string[] _depositLines;
        private static string[] DepositLines => _depositLines ?? (_depositLines = new[] {
            ModLocalization.Loc("hc_cmd_deposit_1"),
            ModLocalization.Loc("hc_cmd_deposit_2"),
            ModLocalization.Loc("hc_cmd_deposit_3")
        });
        private static string[] _depositEmptyLines;
        private static string[] DepositEmptyLines => _depositEmptyLines ?? (_depositEmptyLines = new[] {
            ModLocalization.Loc("hc_cmd_deposit_empty_1"),
            ModLocalization.Loc("hc_cmd_deposit_empty_2")
        });
        private static string[] _harvestLines;
        private static string[] HarvestLines => _harvestLines ?? (_harvestLines = new[] {
            ModLocalization.Loc("hc_cmd_harvest_1"),
            ModLocalization.Loc("hc_cmd_harvest_2"),
            ModLocalization.Loc("hc_cmd_harvest_3")
        });
        private static string[] _cancelLines;
        private static string[] CancelLines => _cancelLines ?? (_cancelLines = new[] {
            ModLocalization.Loc("hc_cmd_cancel_1"),
            ModLocalization.Loc("hc_cmd_cancel_2"),
            ModLocalization.Loc("hc_cmd_cancel_3")
        });
        private static string[] _moveLines;
        private static string[] MoveLines => _moveLines ?? (_moveLines = new[] {
            ModLocalization.Loc("hc_cmd_move_1"),
            ModLocalization.Loc("hc_cmd_move_2"),
            ModLocalization.Loc("hc_cmd_move_3")
        });
        private static string[] _repairLines;
        private static string[] RepairLines => _repairLines ?? (_repairLines = new[] {
            ModLocalization.Loc("hc_cmd_repair_1"),
            ModLocalization.Loc("hc_cmd_repair_2"),
            ModLocalization.Loc("hc_cmd_repair_3")
        });
        private static string[] _repairNothingLines;
        private static string[] RepairNothingLines => _repairNothingLines ?? (_repairNothingLines = new[] {
            ModLocalization.Loc("hc_cmd_repair_nothing_1"),
            ModLocalization.Loc("hc_cmd_repair_nothing_2"),
            ModLocalization.Loc("hc_cmd_repair_nothing_3")
        });
        private static string[] _boardLines;
        private static string[] BoardLines => _boardLines ?? (_boardLines = new[] {
            ModLocalization.Loc("hc_cmd_board_1"),
            ModLocalization.Loc("hc_cmd_board_2"),
            ModLocalization.Loc("hc_cmd_board_3")
        });
        private static string[] _tombstoneLines;
        private static string[] TombstoneLines => _tombstoneLines ?? (_tombstoneLines = new[] {
            ModLocalization.Loc("hc_cmd_tombstone_1"),
            ModLocalization.Loc("hc_cmd_tombstone_2"),
            ModLocalization.Loc("hc_cmd_tombstone_3")
        });
        private static string[] _smeltLines;
        private static string[] SmeltLines => _smeltLines ?? (_smeltLines = new[] {
            ModLocalization.Loc("hc_cmd_smelt_1"),
            ModLocalization.Loc("hc_cmd_smelt_2"),
            ModLocalization.Loc("hc_cmd_smelt_3")
        });

        /// <summary>Clear all cached speech arrays so they re-resolve on next access.</summary>
        internal static void ResetCachedLines()
        {
            _comeHereLines = null; _attackLines = null; _cartPullLines = null;
            _cartReleasedLines = null; _doorLines = null; _sitLines = null;
            _sleepLines = null; _wakeLines = null; _depositLines = null;
            _depositEmptyLines = null; _harvestLines = null; _cancelLines = null;
            _moveLines = null; _repairLines = null; _repairNothingLines = null;
            _boardLines = null; _tombstoneLines = null; _smeltLines = null;
        }

        /// <summary>
        /// Handles the DirectTargetKey (default Z) with full context-sensitive targeting.
        /// Tap: aim at enemy → attack, cart → attach, door → open, fire → sit,
        ///      bed → sleep, station → repair, smelter → smelt, ship → board,
        ///      tombstone → recover, chest → deposit, harvestable → gather,
        ///      ground → move, nothing → cancel all.
        /// Hold: cancel all actions, force all companions to follow ("come to me").
        /// </summary>
        private static void HandleDirectCommand()
        {
            // Per-frame guard — only one CompanionInteract instance acts
            if (Time.frameCount == _directCommandFrame) return;
            _directCommandFrame = Time.frameCount;

            var player = Player.m_localPlayer;
            if (player == null) { _holdActive = false; return; }

            // Don't fire while UI panels are open
            if (InventoryGui.IsVisible() || Minimap.IsOpen() || Menu.IsVisible())
                { _holdActive = false; return; }
            if (TextInput.IsVisible() || Console.IsVisible() || StoreGui.IsVisible())
                { _holdActive = false; return; }
            if (Chat.instance != null && Chat.instance.HasFocus())
                { _holdActive = false; return; }
            if (Hud.IsPieceSelectionVisible())
                { _holdActive = false; return; }
            if (CompanionRadialMenu.Instance != null && CompanionRadialMenu.Instance.IsVisible)
                { _holdActive = false; return; }

            float dt = Time.deltaTime;
            _cmdCooldown -= dt;

            // Detect button state
            bool isGamepad = ZInput.IsGamepadActive();
            bool buttonDown = isGamepad
                ? ZInput.GetButtonDown("JoyUse")
                : Input.GetKeyDown(ModConfig.DirectTargetKey.Value);
            bool buttonHeld = isGamepad
                ? ZInput.GetButton("JoyUse")
                : Input.GetKey(ModConfig.DirectTargetKey.Value);

            // Suppress gamepad when interact prompt or radial is visible
            if (isGamepad && (buttonDown || buttonHeld))
            {
                var hoverObj = player.GetHoverObject();
                if (hoverObj != null || Hud.InRadial())
                {
                    _holdActive = false;
                    _holdTimer = 0f;
                    return;
                }
            }

            // Hold tracking
            if (buttonDown && _cmdCooldown <= 0f)
            {
                _holdActive = true;
                _holdTimer = 0f;
                _holdFired = false;
            }

            if (_holdActive && buttonHeld)
            {
                _holdTimer += dt;
                if (_holdTimer >= CmdHoldThreshold && !_holdFired)
                {
                    _holdFired = true;
                    _cmdCooldown = 0.5f;
                    FireComeToMe(player);
                    return;
                }
                return; // still holding — wait
            }

            // Button released or not held
            if (_holdActive)
            {
                _holdActive = false;
                if (_holdFired) return; // hold already fired cancel-all
                if (_cmdCooldown > 0f) return;
                _cmdCooldown = 0.5f;
            }
            else
            {
                return; // no hold was active, no button pressed
            }

            // ── Tap: context-sensitive raycast ──
            Camera mainCam = Utils.GetMainCamera();
            if (mainCam == null) return;

            Ray ray = new Ray(mainCam.transform.position, mainCam.transform.forward);
            int layerMask = LayerMask.GetMask(
                "character", "character_net", "character_ghost", "character_noenv",
                "Default", "Default_small", "piece", "piece_nonsolid",
                "static_solid", "terrain", "vehicle", "item");

            RaycastHit[] hits = Physics.RaycastAll(ray, 50f, layerMask);
            if (hits.Length > 1)
                System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            var setups = Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None);
            string localId = player.GetPlayerID().ToString();

            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i].collider;
                if (col == null) continue;

                // Skip self and companions
                if (col.GetComponentInParent<Player>() != null) continue;
                if (col.GetComponentInParent<CompanionSetup>() != null) continue;

                // Enemy Character
                var character = col.GetComponentInParent<Character>();
                if (character != null)
                {
                    if (!character.IsDead() && BaseAI.IsEnemy(player, character))
                    {
                        DirectAttack(setups, localId, character);
                        return;
                    }
                    continue;
                }

                // Vagon (Cart)
                var vagon = col.GetComponentInParent<Vagon>();
                if (vagon != null)
                {
                    DirectCart(setups, localId, vagon);
                    return;
                }

                // Door
                var door = col.GetComponentInParent<Door>();
                if (door != null)
                {
                    DirectDoor(setups, localId, door);
                    return;
                }

                // Fireplace
                var fire = col.GetComponentInParent<Fireplace>();
                if (fire != null)
                {
                    DirectSit(setups, localId, fire);
                    return;
                }

                // Bed
                var bed = col.GetComponentInParent<Bed>();
                if (bed != null)
                {
                    DirectSleep(setups, localId, bed);
                    return;
                }

                // CraftingStation (forge, workbench)
                var station = col.GetComponentInParent<CraftingStation>();
                if (station != null)
                {
                    DirectRepair(setups, localId, station);
                    return;
                }

                // Smelter / Kiln
                var smelter = col.GetComponentInParent<Smelter>();
                if (smelter != null)
                {
                    DirectSmelt(setups, localId);
                    return;
                }

                // Ship (boat)
                var ship = col.GetComponentInParent<Ship>();
                if (ship != null)
                {
                    DirectBoard(setups, localId, ship);
                    return;
                }

                // TombStone
                var tombstone = col.GetComponentInParent<TombStone>();
                if (tombstone != null)
                {
                    DirectTombstoneRecovery(setups, localId, tombstone);
                    return;
                }

                // Container (chest)
                var container = col.GetComponentInParent<Container>();
                if (container != null && container.GetComponent<CompanionSetup>() == null)
                {
                    DirectDeposit(setups, localId, container);
                    return;
                }

                // Harvestable (tree, rock, ore)
                var harvestGO = GetHarvestable(col);
                if (harvestGO != null)
                {
                    DirectGatherMode(setups, localId, harvestGO);
                    return;
                }

                // Ground / terrain / building surface
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

            // No valid target found — cancel all
            CancelAll(setups, localId);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Hold → "Come to me" (cancel all, force follow)
        // ═══════════════════════════════════════════════════════════════════

        private static void FireComeToMe(Player player)
        {
            var setups = Object.FindObjectsByType<CompanionSetup>(FindObjectsSortMode.None);
            string localId = player.GetPlayerID().ToString();

            CancelAll(setups, localId);

            CompanionTalk firstTalk = null;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;

                if (!setup.GetFollow())
                {
                    setup.SetFollow(true);
                    CompanionsPlugin.Log.LogDebug(
                        $"[Direct] Come-to-me: forced Follow ON for \"{setup.GetComponent<Character>()?.m_name ?? "?"}\"");
                }

                var ai = setup.GetComponent<CompanionAI>();
                if (ai != null && Player.m_localPlayer != null)
                    ai.SetFollowTarget(Player.m_localPlayer.gameObject);

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
            }

            SayRandom(firstTalk, ComeHereLines, "Action");
            CompanionsPlugin.Log.LogDebug("[Direct] Come-to-me triggered");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Context-sensitive command dispatchers
        // ═══════════════════════════════════════════════════════════════════

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
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }
            SayRandom(firstTalk, AttackLines, "Combat");
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → attack \"{enemy.m_name}\"");
        }

        private static void DirectCart(CompanionSetup[] setups, string localId, Vagon vagon)
        {
            // Check if any owned companion is already attached — detach them
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                var character = setup.GetComponent<Character>();
                if (character != null && vagon.IsAttached(character))
                {
                    var humanoid = setup.GetComponent<Humanoid>();
                    if (humanoid != null)
                    {
                        vagon.Interact(humanoid, false, false);
                        var ai = setup.GetComponent<CompanionAI>();
                        if (ai != null && Player.m_localPlayer != null)
                            ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                        SayRandom(setup.GetComponent<CompanionTalk>(), CartReleasedLines, "Action");
                        CompanionsPlugin.Log.LogDebug("[Direct] Companion → detach from cart");
                    }
                    return;
                }
            }

            // Find closest commandable companion to the cart
            CompanionSetup closest = null;
            float closestDist = float.MaxValue;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                float d = Vector3.Distance(setup.transform.position, vagon.transform.position);
                if (d < closestDist) { closestDist = d; closest = setup; }
            }
            if (closest == null) return;

            var closestHumanoid = closest.GetComponent<Humanoid>();
            var closestAI = closest.GetComponent<CompanionAI>();
            if (closestHumanoid == null || closestAI == null) return;

            CancelExistingActions(closest);

            Vector3 attachWorldPos = vagon.m_attachPoint.position - vagon.m_attachOffset;
            attachWorldPos.y = closest.transform.position.y;
            float distToAttach = Vector3.Distance(closest.transform.position, attachWorldPos);

            if (distToAttach < 3f)
            {
                closest.transform.position = attachWorldPos;
                var body = closest.GetComponent<Rigidbody>();
                if (body != null) { body.position = attachWorldPos; body.linearVelocity = Vector3.zero; }

                Vector3 toCart = vagon.transform.position - closest.transform.position;
                toCart.y = 0f;
                if (toCart.sqrMagnitude > 0.01f)
                    closest.transform.rotation = Quaternion.LookRotation(toCart.normalized);

                closestAI.FreezeTimer = 1f;
                closestAI.SetFollowTarget(vagon.gameObject);
                vagon.Interact(closestHumanoid, false, false);
            }
            else
            {
                closestAI.SetPendingCart(vagon, closestHumanoid);
            }

            SayRandom(closest.GetComponent<CompanionTalk>(), CartPullLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] Companion → cart attach (dist={closestDist:F1}m)");
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
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → open door \"{door.m_name}\"");
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
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → sit near fire");
        }

        private static void DirectSleep(CompanionSetup[] setups, string localId, Bed bed)
        {
            int started = 0, wokeUp = 0;
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
            if (wokeUp > 0) SayRandom(firstTalk, WakeLines, "Action");
            else if (started > 0) SayRandom(firstTalk, SleepLines, "Idle");
            else SayRandom(firstTalk, CancelLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] Sleep — started={started}, wokeUp={wokeUp}");
        }

        private static void DirectRepair(CompanionSetup[] setups, string localId, CraftingStation station)
        {
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
                if (repair.DirectRepairAt(station)) directed++;
            }
            if (directed > 0) SayRandom(firstTalk, RepairLines, "Repair");
            else SayRandom(firstTalk, RepairNothingLines, "Repair");
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → repair at \"{station.m_name}\"");
        }

        private static void DirectSmelt(CompanionSetup[] setups, string localId)
        {
            CompanionTalk firstTalk = null;
            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                CancelExistingActions(setup);
                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) continue;
                nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeSmelt);
                setup.GetComponent<SmeltController>()?.NotifyActionModeChanged();
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }
            SayRandom(firstTalk, SmeltLines, "Smelt");
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → smelt mode");
        }

        private static void DirectBoard(CompanionSetup[] setups, string localId, Ship ship)
        {
            var allChairs = ship.GetComponentsInChildren<Chair>();
            var availableChairs = new System.Collections.Generic.List<Chair>();
            var allChars = Character.GetAllCharacters();

            foreach (var chair in allChairs)
            {
                if (!chair.m_inShip || chair.m_attachPoint == null) continue;
                Vector3 seatPos = chair.m_attachPoint.position;
                bool occupied = false;
                foreach (var c in allChars)
                {
                    if (Vector3.Distance(c.transform.position, seatPos) < 0.5f)
                    { occupied = true; break; }
                }
                if (!occupied) availableChairs.Add(chair);
            }

            CompanionTalk firstTalk = null;
            int boarded = 0;
            var claimedChairs = new System.Collections.Generic.HashSet<Chair>();

            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null || ai.IsOnShip) continue;
                CancelExistingActions(setup);

                Chair bestChair = null;
                float bestDist = float.MaxValue;
                foreach (var chair in availableChairs)
                {
                    if (claimedChairs.Contains(chair)) continue;
                    float dist = Vector3.Distance(setup.transform.position, chair.m_attachPoint.position);
                    if (dist < bestDist) { bestDist = dist; bestChair = chair; }
                }

                if (bestChair != null)
                {
                    claimedChairs.Add(bestChair);
                    ai.SetPendingShipBoard(ship, bestChair);
                }
                else
                {
                    Vector3 deckPos = ship.transform.position + ship.transform.up * 1.5f;
                    setup.transform.position = deckPos;
                    var body = setup.GetComponent<Rigidbody>();
                    if (body != null) { body.position = deckPos; body.linearVelocity = Vector3.zero; }
                    ai.SetFollowTarget(ship.gameObject);
                    ai.FreezeTimer = 1f;
                }

                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                boarded++;
            }

            SayRandom(firstTalk, BoardLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] {boarded} companion(s) → board ship");
        }

        private static void DirectTombstoneRecovery(CompanionSetup[] setups, string localId, TombStone tombstone)
        {
            CompanionSetup closest = null;
            float closestDist = float.MaxValue;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                float d = Vector3.Distance(setup.transform.position, tombstone.transform.position);
                if (d < closestDist) { closestDist = d; closest = setup; }
            }
            if (closest == null) return;
            CancelExistingActions(closest);
            var ai = closest.GetComponent<CompanionAI>();
            if (ai == null) return;
            ai.SetDirectedTombstoneRecovery(tombstone);
            SayRandom(closest.GetComponent<CompanionTalk>(), TombstoneLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] Companion → recover tombstone");
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

                bool hasDepositable = false;
                foreach (var item in compInv.GetAllItems())
                {
                    if (!ShouldKeep(item, humanoid)) { hasDepositable = true; break; }
                }
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                if (!hasDepositable) continue;

                CancelExistingActions(setup);
                var ai = setup.GetComponent<CompanionAI>();
                if (ai == null) continue;
                ai.SetPendingDeposit(chest, humanoid);
                dispatched++;
            }

            if (dispatched > 0) SayRandom(firstTalk, DepositLines, "Action");
            else SayRandom(firstTalk, DepositEmptyLines, "Action");
            CompanionsPlugin.Log.LogDebug($"[Direct] {dispatched} companion(s) → deposit");
        }

        private static void DirectGatherMode(CompanionSetup[] setups, string localId, GameObject target)
        {
            int harvestMode = HarvestController.DetermineHarvestModeStatic(target);
            if (harvestMode < 0) return;

            CompanionTalk firstTalk = null;
            int directed = 0;
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                CancelExistingActions(setup);
                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) continue;
                nview.GetZDO().Set(CompanionSetup.ActionModeHash, harvestMode);
                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null && directed == 0)
                    harvest.SetDirectedTarget(target);
                if (firstTalk == null) firstTalk = setup.GetComponent<CompanionTalk>();
                directed++;
            }
            SayRandom(firstTalk, HarvestLines, "Gather");
            CompanionsPlugin.Log.LogDebug($"[Direct] {directed} companion(s) → gather mode {harvestMode}");
        }

        private static void DirectGround(CompanionSetup[] setups, string localId, Vector3 point)
        {
            // If any companion is in gather mode, exit gather instead of moving
            foreach (var setup in setups)
            {
                if (!IsOwned(setup, localId)) continue;
                if (!setup.GetIsCommandable()) continue;
                var harvest = setup.GetComponent<HarvestController>();
                if (harvest != null && harvest.IsInGatherMode)
                {
                    ExitGatherMode(setups, localId);
                    return;
                }
            }

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
                nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
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
                    ai.CancelPendingCart();
                    ai.CancelMoveTarget();
                    ai.CancelPendingDeposit();
                    ai.CancelPendingShipBoard();
                    ai.CancelTombstoneRecovery();
                    ai.ClearTargets();
                    ai.StopMoving();
                    if (ai.IsOnShip) ai.DetachFromShip();

                    if (Player.m_localPlayer != null && setup.GetFollow())
                        ai.SetFollowTarget(Player.m_localPlayer.gameObject);
                    else if (!setup.GetFollow())
                        ai.SetFollowTarget(null);
                }

                var nview = setup.GetComponent<ZNetView>();
                if (nview?.GetZDO() != null)
                {
                    int mode = nview.GetZDO().GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                    if (mode != CompanionSetup.ModeFollow)
                        nview.GetZDO().Set(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
                }

                setup.GetComponent<HarvestController>()?.NotifyActionModeChanged();
                var rest = setup.GetComponent<CompanionRest>();
                if (rest != null) rest.CancelDirected();
                var repair = setup.GetComponent<RepairController>();
                if (repair != null && repair.IsActive) repair.CancelDirected();
                var smelt = setup.GetComponent<SmeltController>();
                if (smelt != null) smelt.NotifyActionModeChanged();
                var homestead = setup.GetComponent<HomesteadController>();
                if (homestead != null && homestead.IsActive) homestead.CancelDirected();
            }
            SayRandom(firstTalk, CancelLines, "Action");
            CompanionsPlugin.Log.LogDebug("[Direct] Cancelled all directed commands");
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Helpers
        // ═══════════════════════════════════════════════════════════════════

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

        private static void CancelExistingActions(CompanionSetup setup, bool cancelRest = true)
        {
            if (cancelRest)
            {
                var rest = setup.GetComponent<CompanionRest>();
                if (rest != null && (rest.IsResting || rest.IsNavigating))
                    rest.CancelDirected();
            }

            var ai = setup.GetComponent<CompanionAI>();
            if (ai != null)
            {
                ai.CancelPendingCart();
                ai.CancelMoveTarget();
                ai.CancelPendingDeposit();
                ai.CancelPendingShipBoard();
                ai.CancelTombstoneRecovery();
                if (ai.IsOnShip) ai.DetachFromShip();
            }

            setup.GetComponent<HarvestController>()?.CancelDirectedTarget();

            var repair = setup.GetComponent<RepairController>();
            if (repair != null && repair.IsActive) repair.CancelDirected();
            var smelt = setup.GetComponent<SmeltController>();
            if (smelt != null && smelt.IsActive) smelt.CancelDirected();
            var homestead = setup.GetComponent<HomesteadController>();
            if (homestead != null && homestead.IsActive) homestead.CancelDirected();
        }

        /// <summary>
        /// Returns true for items the companion should keep (not deposit).
        /// Keeps: equipped items, food, weapons, armor, shields, utility.
        /// </summary>
        internal static bool ShouldKeep(ItemDrop.ItemData item, Humanoid humanoid)
        {
            if (item == null || item.m_shared == null) return true;
            if (humanoid.IsItemEquiped(item)) return true;
            var t = item.m_shared.m_itemType;
            if (t == ItemDrop.ItemData.ItemType.Consumable &&
                (item.m_shared.m_food > 0f || item.m_shared.m_foodStamina > 0f ||
                 item.m_shared.m_foodEitr > 0f))
                return true;
            if (t == ItemDrop.ItemData.ItemType.OneHandedWeapon ||
                t == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                t == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft ||
                t == ItemDrop.ItemData.ItemType.Bow ||
                t == ItemDrop.ItemData.ItemType.Torch)
                return true;
            if (t == ItemDrop.ItemData.ItemType.Shield ||
                t == ItemDrop.ItemData.ItemType.Helmet ||
                t == ItemDrop.ItemData.ItemType.Chest ||
                t == ItemDrop.ItemData.ItemType.Legs ||
                t == ItemDrop.ItemData.ItemType.Hands ||
                t == ItemDrop.ItemData.ItemType.Shoulder ||
                t == ItemDrop.ItemData.ItemType.Utility)
                return true;
            return false;
        }

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
                if (dest.m_damages.m_chop != HitData.DamageModifier.Immune &&
                    dest.gameObject.name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return dest.gameObject;
                if (dest.m_damages.m_pickaxe != HitData.DamageModifier.Immune &&
                    dest.m_damages.m_chop == HitData.DamageModifier.Immune)
                    return dest.gameObject;
            }
            return null;
        }

        /// <summary>
        /// When RadialMenuKey is NOT E, detect the configured key press independently.
        /// Opens the radial when the player presses the key while hovering a companion.
        /// </summary>
        private void HandleSeparateKeyRadial()
        {
            if (!Input.GetKeyDown(ModConfig.RadialMenuKey.Value)) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Check what the player is hovering over
            var hovering = _playerHoveringField?.GetValue(player) as GameObject;
            if (hovering == null) return;

            // The hover target could be a child — walk up to find CompanionSetup
            var setup = hovering.GetComponentInParent<CompanionSetup>();
            if (setup == null) return;

            // Ownership check
            var nview = setup.GetComponent<ZNetView>();
            if (nview?.GetZDO() == null) return;

            string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
            if (owner.Length > 0 && owner != player.GetPlayerID().ToString())
            {
                player.Message(MessageHud.MessageType.Center, ModLocalization.Loc("hc_msg_not_yours"));
                return;
            }

            CompanionsPlugin.Log.LogInfo(
                $"[Interact] Separate key ({ModConfig.RadialMenuKey.Value}) — opening radial");
            CompanionRadialMenu.EnsureInstance();
            if (CompanionRadialMenu.Instance != null && !CompanionRadialMenu.Instance.IsVisible)
                CompanionRadialMenu.Instance.Show(setup);
        }

        /// <summary>
        /// Independent gamepad hold detection. Monitors ZInput.GetButton("JoyUse")
        /// directly while the player hovers THIS companion instance.
        /// Bypasses the Container.Interact prefix chain entirely — avoids timing
        /// issues with vanilla's 0.2s debounce and m_hovering validity.
        /// </summary>
        private void HandleGamepadHold()
        {
            var player = Player.m_localPlayer;
            if (player == null) { ResetGamepadHold(); return; }

            // Only the instance whose companion is being hovered should act
            var hovering = _playerHoveringField?.GetValue(player) as GameObject;
            var setup = hovering != null ? hovering.GetComponentInParent<CompanionSetup>() : null;
            var mySetup = GetComponent<CompanionSetup>();

            // Not hovering this companion — reset if we were tracking
            if (setup == null || setup != mySetup)
            {
                if (_gpHoldActive && _gpHoldTarget == mySetup)
                    ResetGamepadHold();
                return;
            }

            // Radial already visible — nothing to do
            if (CompanionRadialMenu.Instance != null && CompanionRadialMenu.Instance.IsVisible)
            {
                ResetGamepadHold();
                return;
            }

            if (ZInput.GetButton("JoyUse"))
            {
                if (!_gpHoldActive)
                {
                    // First frame of hold
                    _gpHoldActive = true;
                    _gpHoldStart = Time.time;
                    _gpHoldTarget = setup;
                    CompanionsPlugin.Log.LogDebug(
                        $"[Interact] Gamepad independent hold START on \"{setup.name}\"");
                }
                else if (_gpHoldTarget == setup && Time.time - _gpHoldStart >= HoldThreshold)
                {
                    // Hold threshold reached — open radial
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad independent hold → RADIAL " +
                        $"(held {Time.time - _gpHoldStart:F3}s)");

                    // Clear any prefix-based pending state to avoid double-handling
                    ClearPendingTap();
                    ResetGamepadHold();

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                }
            }
            else if (_gpHoldActive && _gpHoldTarget == setup)
            {
                float held = Time.time - _gpHoldStart;
                ClearPendingTap();
                ResetGamepadHold();

                if (held >= HoldThreshold)
                {
                    // Released at or after threshold — treat as hold → open radial
                    // (handles edge case where GetButton returns false on the
                    //  same frame the threshold is crossed)
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad hold → RADIAL on release " +
                        $"(held {held:F3}s, threshold={HoldThreshold}s)");

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                }
                else
                {
                    // Released before threshold — genuine tap → open inventory
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Gamepad hold → TAP " +
                        $"(released after {held:F3}s), opening inventory");

                    var container = setup.GetComponent<Container>();
                    if (container != null)
                        OpenCompanionInventory(setup, container, player);
                }
            }
        }

        private static void ResetGamepadHold()
        {
            _gpHoldActive = false;
            _gpHoldTarget = null;
        }

        /// <summary>
        /// Process any pending tap from the Container.Interact prefix.
        /// Keyboard: detects hold via Input.GetKey on the configured key.
        /// Gamepad: tracks ZInput.GetButtonUp("JoyUse") as a positive release
        /// signal — assumes held until ButtonUp fires, because GetButton is
        /// unreliable (returns false on frames where the button IS still held
        /// due to ZInput internal state timing vs Player.Interact debounce).
        /// </summary>
        private void ProcessPendingTap()
        {
            if (_pendingTapContainer == null) return;

            float elapsed = Time.time - _pendingTapTime;

            // Gamepad: detect release via discrete ButtonUp event
            if (_pendingIsGamepad && !_gamepadReleaseDetected &&
                ZInput.GetButtonUp("JoyUse"))
            {
                _gamepadReleaseDetected = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Gamepad ButtonUp detected at {elapsed:F3}s");
            }

            // Determine hold state based on input source
            bool useHeld;
            if (_pendingIsGamepad)
                useHeld = !_gamepadReleaseDetected;
            else
                useHeld = Input.GetKey(ModConfig.RadialMenuKey.Value);

            // Log once when we start processing a pending tap
            if (!_loggedPendingStart)
            {
                _loggedPendingStart = true;
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — processing pending tap: " +
                    $"useHeld={useHeld} gamepad={_pendingIsGamepad} " +
                    $"key={ModConfig.RadialMenuKey.Value} " +
                    $"elapsed={elapsed:F3}s");
            }

            if (!useHeld)
            {
                // Button released → genuine tap → open inventory
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — RELEASED after {elapsed:F3}s " +
                    $"(gamepad={_pendingIsGamepad}), opening inventory");

                var container = _pendingTapContainer;
                var player    = _pendingTapPlayer;
                var setup     = container?.GetComponent<CompanionSetup>();
                _pendingTapContainer    = null;
                _pendingTapPlayer       = null;
                _loggedPendingStart     = false;
                _gamepadReleaseDetected = false;

                if (container != null && player != null && setup != null)
                    OpenCompanionInventory(setup, container, player);
                return;
            }

            // Key/button still held — once past the threshold, it's a hold → open radial
            if (elapsed >= HoldThreshold)
            {
                CompanionsPlugin.Log.LogInfo(
                    $"[Interact] Update — hold threshold reached ({elapsed:F3}s), " +
                    $"gamepad={_pendingIsGamepad}, opening radial");

                var setup = _pendingTapContainer.GetComponent<CompanionSetup>();
                _pendingTapContainer    = null;
                _pendingTapPlayer       = null;
                _loggedPendingStart     = false;
                _gamepadReleaseDetected = false;

                if (setup != null)
                {
                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                    {
                        CompanionRadialMenu.Instance.Show(setup);
                        CompanionsPlugin.Log.LogInfo("[Interact] Radial menu Show() called");
                    }
                }
            }
        }

        /// <summary>Clear pending tap (e.g. when radial closes or companion dies).</summary>
        internal static void ClearPendingTap()
        {
            if (_pendingTapContainer != null)
                CompanionsPlugin.Log.LogDebug("[Interact] ClearPendingTap called while pending");
            _pendingTapContainer    = null;
            _pendingTapPlayer       = null;
            _loggedPendingStart     = false;
            _gamepadReleaseDetected = false;
        }

        internal string GetHoverText()
        {
            string name = GetCompanionName();
            string inv = ModLocalization.Loc("hc_hover_inventory");
            string cmd = ModLocalization.Loc("hc_hover_commands");

            // Resolve the interact button name explicitly — $KEY_Use localization
            // can fail to show the gamepad glyph in some timing scenarios.
            string useBtn;
            if (ZInput.IsGamepadActive())
                useBtn = ZInput.instance.GetBoundKeyString("JoyUse");
            else
                useBtn = ZInput.instance.GetBoundKeyString("Use");

            if (IsRadialKeyUse)
            {
                return $"{name}\n" +
                    $"[<color=yellow><b>{useBtn}</b></color>] {inv}\n" +
                    $"[<color=yellow>Hold <b>{useBtn}</b></color>] {cmd}";
            }

            string radialBtn;
            if (ZInput.IsGamepadActive())
                radialBtn = useBtn; // On gamepad, radial uses same button (hold)
            else
                radialBtn = ModConfig.RadialMenuKey.Value.ToString();

            return $"{name}\n" +
                $"[<color=yellow><b>{useBtn}</b></color>] {inv}\n" +
                $"[<color=yellow><b>{radialBtn}</b></color>] {cmd}";
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
            return _character != null ? _character.m_name : ModLocalization.Loc("hc_msg_name_default");
        }

        /// <summary>
        /// Opens the companion inventory by showing InventoryGui with the real
        /// companion Container, then overlaying our custom CompanionInteractPanel.
        /// Vanilla handles drag-drop, gamepad groups, SetInUse, and IsContainerOpen
        /// natively — no Harmony patches on InventoryGui needed.
        /// </summary>
        internal static void OpenCompanionInventory(CompanionSetup setup, Container container, Player player)
        {
            if (InventoryGui.instance == null) return;

            // Claim ZDO ownership before Show() — vanilla's UpdateContainer checks
            // IsOwner() every frame, and SetInUse() also requires ownership.
            // The normal RPC chain (Container.Interact → RequestOpen → OpenResponse)
            // transfers ownership via ZDO.SetOwner(), but we bypass that chain.
            var nview = container.GetComponent<ZNetView>();
            if (nview != null && !nview.IsOwner())
                nview.ClaimOwnership();

            InventoryGui.instance.Show(container);
            CompanionInteractPanel.EnsureInstance();
            CompanionInteractPanel.Instance?.Show(setup);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Harmony patches — ownership check + defer tap on Container.Interact
        // ═══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Container), nameof(Container.Interact))]
        private static class ContainerInteract_Patch
        {
            static bool Prefix(Container __instance, Humanoid character, bool hold, ref bool __result)
            {
                var setup = __instance.GetComponent<CompanionSetup>();
                if (setup == null) return true;

                var nview = __instance.GetComponent<ZNetView>();
                if (nview?.GetZDO() == null) return true;

                var player = character as Player;
                if (player == null)
                {
                    __result = false;
                    return false;
                }

                // Ownership check
                string owner = nview.GetZDO().GetString(CompanionSetup.OwnerHash, "");
                string playerId = player.GetPlayerID().ToString();
                if (owner.Length > 0 && owner != playerId)
                {
                    player.Message(MessageHud.MessageType.Center, ModLocalization.Loc("hc_msg_not_yours"));
                    __result = false;
                    return false;
                }

                // ── Gamepad: independent hold detection handles everything ──
                // Must be checked BEFORE separate-key mode — gamepad always uses
                // HandleGamepadHold() for tap/hold split, regardless of radial key config.
                if (ZInput.IsGamepadActive())
                {
                    __result = false;
                    return false;
                }

                // ── Separate-key mode: no tap/hold deferral, open inventory directly ──
                if (!IsRadialKeyUse)
                {
                    if (hold)
                    {
                        // Suppress vanilla hold repeats (they'd re-open the container)
                        __result = false;
                        return false;
                    }
                    // hold=false (tap) → open inventory directly
                    CompanionsPlugin.Log.LogDebug(
                        "[Interact] Prefix — separate key mode, opening inventory");
                    OpenCompanionInventory(setup, __instance, player);
                    __result = true;
                    return false;
                }

                // ── Same-key mode (E) — keyboard only below this point ──
                if (hold)
                {
                    // Hold call arrived (after Valheim's 0.2s debounce)
                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Prefix — hold=true arrived, opening radial");

                    _pendingTapContainer    = null;
                    _pendingTapPlayer       = null;
                    _loggedPendingStart     = false;
                    _gamepadReleaseDetected = false;

                    CompanionRadialMenu.EnsureInstance();
                    if (CompanionRadialMenu.Instance != null &&
                        !CompanionRadialMenu.Instance.IsVisible)
                        CompanionRadialMenu.Instance.Show(setup);
                    __result = true;
                    return false;
                }

                // Tap E → defer. Update() will check raw input to distinguish tap vs hold.
                // Guard: only set if not already pending (prevents timer reset).
                if (_pendingTapContainer == null)
                {
                    _pendingTapContainer    = __instance;
                    _pendingTapPlayer       = player;
                    _pendingTapTime         = Time.time;
                    _loggedPendingStart     = false;
                    _pendingIsGamepad       = false; // keyboard only at this point
                    _gamepadReleaseDetected = false;

                    CompanionsPlugin.Log.LogInfo(
                        $"[Interact] Prefix — tap deferred, " +
                        $"InputKey={Input.GetKey(ModConfig.RadialMenuKey.Value)} " +
                        $"time={Time.time:F3}");
                }
                else
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Interact] Prefix — already pending, keeping existing timer " +
                        $"(elapsed={Time.time - _pendingTapTime:F3}s)");
                }

                __result = false;
                return false;
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
