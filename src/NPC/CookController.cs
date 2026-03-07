using System;
using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Companion cooking controller. Randomly rotates between:
    ///   1. Cook food at a Cauldron (CraftingStation)
    ///   2. Brew mead bases at the Cauldron
    ///   3. Place mead bases into Fermenters
    ///   4. Tap ready Fermenters and collect finished meads
    ///   5. Store output in nearby chests
    ///
    /// State machine: Idle → [behavior] → ... → Idle
    /// Follows SmeltController pattern for mode checking, claiming, and movement.
    /// </summary>
    public class CookController : MonoBehaviour
    {
        internal enum CookPhase
        {
            Idle,
            // Cook food / brew mead at Cauldron
            MovingToIngredientChest,
            TakingIngredients,
            MovingToCauldron,
            Crafting,
            // Place mead base into Fermenter
            MovingToFermenter,
            InsertingMeadBase,
            // Tap ready Fermenter
            MovingToReadyFermenter,
            TappingFermenter,
            CollectingDrops,
            // Store output
            MovingToOutputChest,
            StoringOutput
        }

        private enum CookTask { CookFood, BrewMead, FillFermenter, TapFermenter, StoreOutput }

        internal CookPhase Phase => _phase;
        public bool IsActive => _phase != CookPhase.Idle;

        // ── Components ──────────────────────────────────────────────────────
        private CompanionAI    _ai;
        private Humanoid       _humanoid;
        private Character      _character;
        private CompanionSetup _setup;
        private ZNetView       _nview;
        private ZSyncAnimation _zanim;
        private CompanionTalk  _talk;
        private CompanionFood  _food;
        private DoorHandler    _doorHandler;

        // ── State ───────────────────────────────────────────────────────────
        private CookPhase _phase;
        private CookTask  _currentTask;
        private float     _scanTimer;
        private float     _actionTimer;
        private float     _stuckTimer;
        private float     _stuckCheckTimer;
        private Vector3   _stuckCheckPos;
        private bool      _chestOpened;
        private bool      _shouldRun;

        // ── Behavior cooldowns ──────────────────────────────────────────────
        private float _cookCooldown;
        private float _brewCooldown;
        private float _fillCooldown;
        private float _tapCooldown;
        private float _storeCooldown;

        // ── Fermenter claiming ──────────────────────────────────────────────
        private static readonly HashSet<int> s_claimedFermenters = new HashSet<int>();
        private int _claimedFermenterId;

        // ── Targets ─────────────────────────────────────────────────────────
        private CraftingStation _targetCauldron;
        private Fermenter       _targetFermenter;
        private Container       _targetChest;
        private Container       _outputChest;
        private Recipe          _selectedRecipe;
        private readonly List<(string prefab, int needed)> _ingredientList = new List<(string, int)>();
        private int    _ingredientIndex;
        private string _meadBasePrefab; // prefab of mead base being handled

        // ── Scan caches ─────────────────────────────────────────────────────
        private readonly List<Fermenter> _nearbyFermenters = new List<Fermenter>();
        private readonly List<Container> _nearbyChests     = new List<Container>();
        private readonly List<Piece>     _tempPieces       = new List<Piece>();
        private static readonly Collider[] _dropScanBuffer = new Collider[32];
        private static int _itemLayerMask = -1;

        // ── Fermenter input cache (lazy) ────────────────────────────────────
        private static HashSet<string> s_fermenterInputPrefabs;

        // ── Config ──────────────────────────────────────────────────────────
        private const float ScanRadius       = 25f;
        private const float ScanInterval     = 5f;
        private const float UseDistance      = 2.5f;
        private const float MoveTimeout      = 10f;
        private const float StuckCheckPeriod = 1f;
        private const float StuckMinDistance  = 0.5f;
        private const float ChestOpenDelay   = 0.8f;
        private const float CraftingTime     = 3f;
        private const float TapWaitTime      = 2f;

        // Behavior weights
        private const float WeightTap   = 50f;
        private const float WeightCook  = 40f;
        private const float WeightStore = 35f;
        private const float WeightFill  = 30f;
        private const float WeightBrew  = 25f;

        // ═════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _ai          = GetComponent<CompanionAI>();
            _humanoid    = GetComponent<Humanoid>();
            _character   = GetComponent<Character>();
            _setup       = GetComponent<CompanionSetup>();
            _nview       = GetComponent<ZNetView>();
            _zanim       = GetComponent<ZSyncAnimation>();
            _talk        = GetComponent<CompanionTalk>();
            _food        = GetComponent<CompanionFood>();
            _doorHandler = GetComponent<DoorHandler>();
        }

        private void OnDestroy()
        {
            UnclaimFermenter();
            CloseAnyChest();
        }

        private void Update()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            if (_character == null || _character.GetHealth() <= 0f) return;

            if (ShouldAbort())
            {
                if (_phase != CookPhase.Idle)
                    Abort("interrupted");
                return;
            }

            if (!IsCookMode() && _phase == CookPhase.Idle) return;
            if (!IsCookMode() && _phase != CookPhase.Idle)
            {
                Abort("mode changed");
                return;
            }

            float dt = Time.deltaTime;

            // Tick cooldowns
            _cookCooldown  = Mathf.Max(0f, _cookCooldown  - dt);
            _brewCooldown  = Mathf.Max(0f, _brewCooldown  - dt);
            _fillCooldown  = Mathf.Max(0f, _fillCooldown  - dt);
            _tapCooldown   = Mathf.Max(0f, _tapCooldown   - dt);
            _storeCooldown = Mathf.Max(0f, _storeCooldown  - dt);

            switch (_phase)
            {
                case CookPhase.Idle:                    UpdateIdle(dt); break;
                case CookPhase.MovingToIngredientChest: UpdateMovingToIngredientChest(dt); break;
                case CookPhase.TakingIngredients:       UpdateTakingIngredients(dt); break;
                case CookPhase.MovingToCauldron:        UpdateMovingToCauldron(dt); break;
                case CookPhase.Crafting:                UpdateCrafting(dt); break;
                case CookPhase.MovingToFermenter:       UpdateMovingToFermenter(dt); break;
                case CookPhase.InsertingMeadBase:       UpdateInsertingMeadBase(dt); break;
                case CookPhase.MovingToReadyFermenter:  UpdateMovingToReadyFermenter(dt); break;
                case CookPhase.TappingFermenter:        UpdateTappingFermenter(dt); break;
                case CookPhase.CollectingDrops:         UpdateCollectingDrops(dt); break;
                case CookPhase.MovingToOutputChest:     UpdateMovingToOutputChest(dt); break;
                case CookPhase.StoringOutput:           UpdateStoringOutput(dt); break;
            }
        }

        private void LateUpdate()
        {
            if (_character == null) return;
            if (_phase == CookPhase.MovingToIngredientChest ||
                _phase == CookPhase.MovingToCauldron ||
                _phase == CookPhase.MovingToFermenter ||
                _phase == CookPhase.MovingToReadyFermenter ||
                _phase == CookPhase.MovingToOutputChest ||
                _phase == CookPhase.CollectingDrops)
            {
                _character.SetRun(_shouldRun);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Public API
        // ═════════════════════════════════════════════════════════════════════

        public void NotifyActionModeChanged()
        {
            if (_phase != CookPhase.Idle)
            {
                Log("NotifyActionModeChanged — aborting cook");
                Abort("mode changed");
            }
        }

        internal bool IsCookMode()
        {
            var zdo = _nview?.GetZDO();
            if (zdo == null) return false;
            return zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                == CompanionSetup.ModeCook;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Idle — pick next behavior
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateIdle(float dt)
        {
            _scanTimer -= dt;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            // Refresh scan data
            ScanNearbyChests();
            ScanNearbyFermenters();
            EnsureFermenterInputCache();

            PickNextBehavior();
        }

        private void PickNextBehavior()
        {
            // Priority 1: if we have storable output, store it first
            if (_storeCooldown <= 0f && HasStorableOutput())
            {
                if (TryStartStore())
                {
                    _currentTask = CookTask.StoreOutput;
                    return;
                }
            }

            // Priority 2: if any fermenter is ready, tap it
            if (_tapCooldown <= 0f)
            {
                if (TryStartTapFermenter())
                {
                    _currentTask = CookTask.TapFermenter;
                    return;
                }
            }

            // Weighted random from remaining behaviors
            float wCook = _cookCooldown <= 0f ? WeightCook : 0f;
            float wBrew = _brewCooldown <= 0f ? WeightBrew : 0f;
            float wFill = _fillCooldown <= 0f ? WeightFill : 0f;
            float total = wCook + wBrew + wFill;
            if (total <= 0f) return;

            float roll = UnityEngine.Random.Range(0f, total);
            float cum = 0f;

            cum += wCook;
            if (roll < cum)
            {
                if (TryStartCookOrBrew(meadOnly: false))
                {
                    _currentTask = CookTask.CookFood;
                    return;
                }
                _cookCooldown = 30f;
                return;
            }

            cum += wBrew;
            if (roll < cum)
            {
                if (TryStartCookOrBrew(meadOnly: true))
                {
                    _currentTask = CookTask.BrewMead;
                    return;
                }
                _brewCooldown = 30f;
                return;
            }

            cum += wFill;
            if (roll < cum)
            {
                if (TryStartFillFermenter())
                {
                    _currentTask = CookTask.FillFermenter;
                    return;
                }
                _fillCooldown = 20f;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Cook Food / Brew Mead at Cauldron
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartCookOrBrew(bool meadOnly)
        {
            // Find cauldron
            _targetCauldron = FindCauldron();
            if (_targetCauldron == null)
            {
                Log("No cauldron found nearby");
                return false;
            }

            // Get viable recipes
            var recipes = ObjectDB.instance?.m_recipes;
            if (recipes == null) return false;

            var viable = new List<Recipe>();
            foreach (var recipe in recipes)
            {
                if (recipe == null || !recipe.m_enabled) continue;
                if (recipe.m_item == null) continue;
                if (recipe.m_craftingStation == null) continue;
                if (recipe.m_craftingStation.m_name != _targetCauldron.m_name) continue;
                if (_targetCauldron.GetLevel() < recipe.GetRequiredStationLevel(1)) continue;

                var shared = recipe.m_item.m_itemData?.m_shared;
                if (shared == null) continue;

                if (meadOnly)
                {
                    // Only mead base recipes (output goes into a fermenter)
                    if (!IsFermenterInput(recipe.m_item.gameObject.name)) continue;
                }
                else
                {
                    // Only food recipes
                    if (shared.m_food <= 0f && shared.m_foodStamina <= 0f) continue;
                    // Skip mead bases from food cooking (they're brewed separately)
                    if (IsFermenterInput(recipe.m_item.gameObject.name)) continue;
                }

                if (!CanCraftFromChests(recipe)) continue;
                viable.Add(recipe);
            }

            if (viable.Count == 0)
            {
                Log(meadOnly ? "No viable mead base recipes" : "No viable food recipes");
                return false;
            }

            _selectedRecipe = viable[UnityEngine.Random.Range(0, viable.Count)];

            // Build ingredient list
            _ingredientList.Clear();
            foreach (var req in _selectedRecipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                int needed = req.GetAmount(1);
                if (needed <= 0) continue;
                _ingredientList.Add((req.m_resItem.gameObject.name, needed));
            }
            _ingredientIndex = 0;

            // Find first ingredient chest
            if (!FindNextIngredientChest())
            {
                Log("Can't find chest with first ingredient");
                return false;
            }

            ClearFollowForMovement();
            ResetStuck();
            _phase = CookPhase.MovingToIngredientChest;
            string recipeName = _selectedRecipe.m_item.m_itemData?.m_shared?.m_name ?? "?";
            Log($"Starting {(meadOnly ? "brew" : "cook")}: {recipeName}");

            if (_talk != null)
            {
                string key = meadOnly ? "hc_speech_cook_brewing" : "hc_speech_cook_cooking";
                _talk.Say(ModLocalization.Loc(key), "Action");
            }
            return true;
        }

        private bool FindNextIngredientChest()
        {
            var compInv = _humanoid?.GetInventory();
            if (compInv == null) return false;

            while (_ingredientIndex < _ingredientList.Count)
            {
                var (prefab, needed) = _ingredientList[_ingredientIndex];
                int have = HomesteadController.CountItemInInventory(compInv, prefab);
                if (have >= needed)
                {
                    _ingredientIndex++;
                    continue;
                }

                // Find chest with this ingredient
                _targetChest = FindChestWithItem(prefab);
                if (_targetChest != null) return true;

                Log($"No chest has ingredient \"{prefab}\"");
                return false;
            }
            return false; // All ingredients already in inventory
        }

        // ── Moving to ingredient chest ──────────────────────────────────────

        private void UpdateMovingToIngredientChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetChest == null)
            {
                Abort("ingredient chest destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetChest.transform.position);
            if (dist < UseDistance)
            {
                _ai?.StopMoving();
                _chestOpened = false;
                _phase = CookPhase.TakingIngredients;
                return;
            }

            _shouldRun = dist > 8f;
            _ai?.MoveToPoint(dt, _targetChest.transform.position, UseDistance, _shouldRun);
            UpdateStuck(dt, "ingredient chest", dist);
        }

        // ── Taking ingredients ──────────────────────────────────────────────

        private void UpdateTakingIngredients(float dt)
        {
            if (_targetChest == null)
            {
                Abort("ingredient chest gone");
                return;
            }

            if (!_chestOpened)
            {
                _targetChest.SetInUse(true);
                _chestOpened = true;
                if (_zanim != null) _zanim.SetTrigger("interact");
                _actionTimer = ChestOpenDelay;
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            var compInv = _humanoid?.GetInventory();
            var chestInv = _targetChest.GetInventory();
            if (compInv == null || chestInv == null)
            {
                CloseCurrentChest();
                Abort("inventory null");
                return;
            }

            // Transfer current ingredient
            var (prefab, needed) = _ingredientList[_ingredientIndex];
            int have = HomesteadController.CountItemInInventory(compInv, prefab);
            int remaining = needed - have;

            if (remaining > 0)
            {
                var item = HomesteadController.FindItemByPrefab(chestInv, prefab);
                if (item != null)
                {
                    HomesteadController.TransferOne(chestInv, compInv, item);
                    return; // transfer one per frame
                }
                // Chest depleted — try another chest
                CloseCurrentChest();
                _targetChest = FindChestWithItem(prefab);
                if (_targetChest != null)
                {
                    ResetStuck();
                    _phase = CookPhase.MovingToIngredientChest;
                    return;
                }
                Abort("no more chests with ingredient");
                return;
            }

            // This ingredient done — advance
            _ingredientIndex++;
            if (FindNextIngredientChest())
            {
                CloseCurrentChest();
                ResetStuck();
                _phase = CookPhase.MovingToIngredientChest;
                return;
            }

            // All ingredients gathered — route based on task
            CloseCurrentChest();
            ResetStuck();

            if (_currentTask == CookTask.FillFermenter)
            {
                // Was fetching mead base from chest — head to fermenter
                if (_targetFermenter == null)
                {
                    Abort("fermenter gone");
                    return;
                }
                _phase = CookPhase.MovingToFermenter;
                Log("Got mead base — heading to fermenter");
            }
            else
            {
                // CookFood / BrewMead — head to cauldron
                if (_targetCauldron == null)
                {
                    Abort("cauldron gone");
                    return;
                }
                _phase = CookPhase.MovingToCauldron;
                Log("All ingredients gathered — heading to cauldron");
            }
        }

        // ── Moving to cauldron ──────────────────────────────────────────────

        private void UpdateMovingToCauldron(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetCauldron == null)
            {
                Abort("cauldron destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetCauldron.transform.position);
            if (dist < UseDistance)
            {
                _ai?.StopMoving();
                _actionTimer = CraftingTime;
                _phase = CookPhase.Crafting;
                Log("At cauldron — crafting");
                return;
            }

            _shouldRun = dist > 8f;
            _ai?.MoveToPoint(dt, _targetCauldron.transform.position, UseDistance, _shouldRun);
            UpdateStuck(dt, "cauldron", dist);
        }

        // ── Crafting ────────────────────────────────────────────────────────

        private void UpdateCrafting(float dt)
        {
            if (_targetCauldron != null)
            {
                _ai?.StopMoving();
                _ai?.LookAtPoint(_targetCauldron.transform.position);
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            // Consume ingredients
            var inv = _humanoid?.GetInventory();
            if (inv == null) { Abort("inventory null"); return; }

            foreach (var req in _selectedRecipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                string prefab = req.m_resItem.gameObject.name;
                int needed = req.GetAmount(1);
                for (int n = 0; n < needed; n++)
                    HomesteadController.ConsumeOneFromInventory(inv, prefab);
            }

            // Produce output
            string outputPrefab = _selectedRecipe.m_item.gameObject.name;
            int amount = _selectedRecipe.m_amount;
            var added = inv.AddItem(outputPrefab, amount, 1, 0, 0L, "");

            if (added == null)
            {
                // Inventory full — drop on ground
                var prefabGO = ObjectDB.instance?.GetItemPrefab(outputPrefab);
                if (prefabGO != null)
                {
                    var drop = UnityEngine.Object.Instantiate(prefabGO,
                        transform.position + Vector3.up * 0.5f, Quaternion.identity);
                    var itemDrop = drop.GetComponent<ItemDrop>();
                    if (itemDrop != null) itemDrop.m_itemData.m_stack = amount;
                    Log($"Inventory full — dropped {outputPrefab} on ground");
                }
            }

            string mealName = _selectedRecipe.m_item.m_itemData?.m_shared?.m_name ?? outputPrefab;
            Log($"Crafted {amount}x {mealName}");

            // Set cooldown based on task
            if (_currentTask == CookTask.BrewMead)
                _brewCooldown = 15f;
            else
                _cookCooldown = 15f;

            // If we brewed a mead base and there's an empty fermenter, go fill it
            if (_currentTask == CookTask.BrewMead && added != null)
            {
                _meadBasePrefab = outputPrefab;
                var fermenter = FindEmptyFermenter();
                if (fermenter != null)
                {
                    _targetFermenter = fermenter;
                    ClaimFermenter(fermenter);
                    ResetStuck();
                    _phase = CookPhase.MovingToFermenter;
                    if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_cook_fermenter"), "Action");
                    Log($"Brewed mead base — heading to fermenter");
                    return;
                }
            }

            // Check if we should store
            if (HasStorableOutput())
            {
                if (TryStartStore())
                {
                    _currentTask = CookTask.StoreOutput;
                    return;
                }
            }

            Finish();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Fill Fermenter with mead base
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartFillFermenter()
        {
            var compInv = _humanoid?.GetInventory();
            if (compInv == null) return false;

            // Check if companion already has a mead base in inventory
            string meadPrefab = FindMeadBaseInInventory(compInv);

            // If not in inventory, check chests
            Container meadChest = null;
            if (meadPrefab == null)
            {
                foreach (var chest in _nearbyChests)
                {
                    var chestInv = chest?.GetInventory();
                    if (chestInv == null) continue;
                    string found = FindMeadBaseInInventoryInternal(chestInv);
                    if (found != null)
                    {
                        meadPrefab = found;
                        meadChest = chest;
                        break;
                    }
                }
            }

            if (meadPrefab == null)
            {
                Log("No mead base available");
                return false;
            }

            // Find empty fermenter that accepts this mead base
            Fermenter target = null;
            foreach (var f in _nearbyFermenters)
            {
                if (f == null) continue;
                if (s_claimedFermenters.Contains(f.GetInstanceID())) continue;
                if (GetFermenterStatus(f) != FermenterStatus.Empty) continue;
                if (!FermenterAccepts(f, meadPrefab)) continue;
                target = f;
                break;
            }

            if (target == null)
            {
                Log("No empty fermenter available");
                return false;
            }

            _targetFermenter = target;
            _meadBasePrefab = meadPrefab;
            ClaimFermenter(target);
            ClearFollowForMovement();
            ResetStuck();

            if (meadChest != null)
            {
                // Need to get mead base from chest first
                _targetChest = meadChest;
                _phase = CookPhase.MovingToIngredientChest;
                // Temporarily set up ingredient list for the chest pickup
                _ingredientList.Clear();
                _ingredientList.Add((meadPrefab, 1));
                _ingredientIndex = 0;
                Log($"Getting mead base \"{meadPrefab}\" from chest, then filling fermenter");
            }
            else
            {
                // Already in inventory — go straight to fermenter
                _phase = CookPhase.MovingToFermenter;
                Log($"Have mead base \"{meadPrefab}\" — heading to fermenter");
            }

            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_cook_fermenter"), "Action");
            return true;
        }

        // ── Moving to fermenter ─────────────────────────────────────────────

        private void UpdateMovingToFermenter(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetFermenter == null)
            {
                Abort("fermenter destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetFermenter.transform.position);
            if (dist < UseDistance)
            {
                _ai?.StopMoving();
                _actionTimer = 0.5f;
                _phase = CookPhase.InsertingMeadBase;
                return;
            }

            _shouldRun = dist > 8f;
            _ai?.MoveToPoint(dt, _targetFermenter.transform.position, UseDistance, _shouldRun);
            UpdateStuck(dt, "fermenter", dist);
        }

        // ── Inserting mead base ─────────────────────────────────────────────

        private void UpdateInsertingMeadBase(float dt)
        {
            if (_targetFermenter == null)
            {
                Abort("fermenter gone");
                return;
            }

            _ai?.StopMoving();
            _ai?.LookAtPoint(_targetFermenter.transform.position);

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            // Verify fermenter is still empty
            if (GetFermenterStatus(_targetFermenter) != FermenterStatus.Empty)
            {
                Log("Fermenter no longer empty — aborting fill");
                UnclaimFermenter();
                Finish();
                return;
            }

            // Remove mead base from companion inventory
            var inv = _humanoid?.GetInventory();
            if (inv == null || !HomesteadController.ConsumeOneFromInventory(inv, _meadBasePrefab))
            {
                Log($"Don't have mead base \"{_meadBasePrefab}\" — aborting");
                UnclaimFermenter();
                Finish();
                return;
            }

            // Invoke fermenter RPC
            var fnview = _targetFermenter.GetComponent<ZNetView>();
            if (fnview != null && fnview.GetZDO() != null)
            {
                fnview.InvokeRPC("RPC_AddItem", _meadBasePrefab);
                Log($"Inserted \"{_meadBasePrefab}\" into fermenter");
            }

            UnclaimFermenter();
            _fillCooldown = 10f;
            Finish();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Tap ready Fermenter
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartTapFermenter()
        {
            foreach (var f in _nearbyFermenters)
            {
                if (f == null) continue;
                if (s_claimedFermenters.Contains(f.GetInstanceID())) continue;
                if (GetFermenterStatus(f) != FermenterStatus.Ready) continue;

                _targetFermenter = f;
                ClaimFermenter(f);
                ClearFollowForMovement();
                ResetStuck();
                _phase = CookPhase.MovingToReadyFermenter;
                Log("Found ready fermenter — heading to tap");
                if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_cook_tapping"), "Action");
                return true;
            }
            return false;
        }

        // ── Moving to ready fermenter ───────────────────────────────────────

        private void UpdateMovingToReadyFermenter(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_targetFermenter == null)
            {
                Abort("ready fermenter destroyed");
                return;
            }

            float dist = Vector3.Distance(transform.position, _targetFermenter.transform.position);
            if (dist < UseDistance)
            {
                _ai?.StopMoving();
                _actionTimer = 0f;
                _phase = CookPhase.TappingFermenter;
                return;
            }

            _shouldRun = dist > 8f;
            _ai?.MoveToPoint(dt, _targetFermenter.transform.position, UseDistance, _shouldRun);
            UpdateStuck(dt, "ready fermenter", dist);
        }

        // ── Tapping fermenter ───────────────────────────────────────────────

        private void UpdateTappingFermenter(float dt)
        {
            if (_targetFermenter == null)
            {
                Abort("fermenter gone during tap");
                return;
            }

            _ai?.StopMoving();
            _ai?.LookAtPoint(_targetFermenter.transform.position);

            if (_actionTimer <= 0f)
            {
                // Verify still ready
                if (GetFermenterStatus(_targetFermenter) != FermenterStatus.Ready)
                {
                    Log("Fermenter no longer ready");
                    UnclaimFermenter();
                    Finish();
                    return;
                }

                var fnview = _targetFermenter.GetComponent<ZNetView>();
                if (fnview != null && fnview.GetZDO() != null)
                {
                    fnview.InvokeRPC("RPC_Tap");
                    Log("Tapped fermenter — waiting for items to spawn");
                }
                _actionTimer = TapWaitTime;
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            // Items should have spawned — collect them
            _phase = CookPhase.CollectingDrops;
        }

        // ── Collecting drops ────────────────────────────────────────────────

        private void UpdateCollectingDrops(float dt)
        {
            if (_targetFermenter == null)
            {
                UnclaimFermenter();
                Finish();
                return;
            }

            Vector3 outputPos = _targetFermenter.m_outputPoint != null
                ? _targetFermenter.m_outputPoint.position
                : _targetFermenter.transform.position;

            float dist = Vector3.Distance(transform.position, outputPos);
            if (dist > UseDistance + 1f)
            {
                _shouldRun = dist > 8f;
                _ai?.MoveToPoint(dt, outputPos, UseDistance, _shouldRun);
                return;
            }

            _ai?.StopMoving();
            PickUpNearbyDrops(outputPos);

            UnclaimFermenter();
            _tapCooldown = 5f;

            // Store the collected items
            if (HasStorableOutput())
            {
                if (TryStartStore())
                {
                    _currentTask = CookTask.StoreOutput;
                    return;
                }
            }

            Finish();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Behavior: Store output in chest
        // ═════════════════════════════════════════════════════════════════════

        private bool TryStartStore()
        {
            ScanNearbyChests();
            _outputChest = FindChestWithSpace();
            if (_outputChest == null)
            {
                Log("No chest with space for output");
                return false;
            }

            ClearFollowForMovement();
            ResetStuck();
            _phase = CookPhase.MovingToOutputChest;
            if (_talk != null) _talk.Say(ModLocalization.Loc("hc_speech_cook_storing"), "Action");
            Log($"Storing output in chest at {_outputChest.transform.position:F1}");
            return true;
        }

        // ── Moving to output chest ──────────────────────────────────────────

        private void UpdateMovingToOutputChest(float dt)
        {
            if (_doorHandler != null && _doorHandler.IsActive) return;

            if (_outputChest == null)
            {
                Finish();
                return;
            }

            float dist = Vector3.Distance(transform.position, _outputChest.transform.position);
            if (dist < UseDistance)
            {
                _ai?.StopMoving();
                _chestOpened = false;
                _phase = CookPhase.StoringOutput;
                return;
            }

            _shouldRun = dist > 8f;
            _ai?.MoveToPoint(dt, _outputChest.transform.position, UseDistance, _shouldRun);
            UpdateStuck(dt, "output chest", dist);
        }

        // ── Storing output ──────────────────────────────────────────────────

        private void UpdateStoringOutput(float dt)
        {
            if (_outputChest == null)
            {
                if (_chestOpened) _chestOpened = false;
                Finish();
                return;
            }

            if (!_chestOpened)
            {
                _outputChest.SetInUse(true);
                _chestOpened = true;
                if (_zanim != null) _zanim.SetTrigger("interact");
                _actionTimer = ChestOpenDelay;
                return;
            }

            _actionTimer -= dt;
            if (_actionTimer > 0f) return;

            var compInv = _humanoid?.GetInventory();
            var chestInv = _outputChest.GetInventory();
            if (compInv == null || chestInv == null)
            {
                _outputChest.SetInUse(false);
                _chestOpened = false;
                Finish();
                return;
            }

            int stored = 0;
            var allItems = compInv.GetAllItems();
            for (int i = allItems.Count - 1; i >= 0; i--)
            {
                var item = allItems[i];
                if (item == null) continue;
                if (!IsStorableOutput(item)) continue;

                if (chestInv.CanAddItem(item, item.m_stack))
                {
                    var clone = item.Clone();
                    clone.m_stack = item.m_stack;
                    if (chestInv.AddItem(clone))
                    {
                        compInv.RemoveItem(item);
                        stored++;
                    }
                }
            }

            _outputChest.SetInUse(false);
            _chestOpened = false;
            _storeCooldown = 10f;

            Log($"Stored {stored} item stacks in chest");
            Finish();
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Scanning
        // ═════════════════════════════════════════════════════════════════════

        private void ScanNearbyChests()
        {
            _nearbyChests.Clear();
            _tempPieces.Clear();
            Piece.GetAllPiecesInRadius(transform.position, ScanRadius, _tempPieces);

            foreach (var piece in _tempPieces)
            {
                if (piece == null) continue;
                var container = piece.GetComponentInChildren<Container>();
                if (container == null) continue;
                if (container.GetInventory() == null) continue;
                if (container.gameObject == gameObject) continue;
                if (container.IsInUse()) continue;
                _nearbyChests.Add(container);
            }
        }

        private void ScanNearbyFermenters()
        {
            _nearbyFermenters.Clear();
            foreach (var piece in _tempPieces)
            {
                if (piece == null) continue;
                var fermenter = piece.GetComponent<Fermenter>();
                if (fermenter == null) continue;
                var fnview = fermenter.GetComponent<ZNetView>();
                if (fnview == null || fnview.GetZDO() == null) continue;
                _nearbyFermenters.Add(fermenter);
            }
        }

        private CraftingStation FindCauldron()
        {
            var stations = ReflectionHelper.GetAllCraftingStations();
            if (stations == null) return null;

            CraftingStation best = null;
            float bestDist = float.MaxValue;

            foreach (var s in stations)
            {
                if (s == null) continue;
                if (!s.m_name.ToLower().Contains("cauldron")) continue;
                float d = Vector3.Distance(transform.position, s.transform.position);
                if (d > ScanRadius) continue;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = s;
                }
            }
            return best;
        }

        private Container FindChestWithItem(string prefab)
        {
            Container best = null;
            float bestDist = float.MaxValue;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null || chest.IsInUse()) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;
                if (HomesteadController.CountItemInInventory(inv, prefab) <= 0) continue;

                float d = Vector3.Distance(transform.position, chest.transform.position);
                if (d < bestDist) { bestDist = d; best = chest; }
            }
            return best;
        }

        private Container FindChestWithSpace()
        {
            Container best = null;
            float bestDist = float.MaxValue;

            foreach (var chest in _nearbyChests)
            {
                if (chest == null || chest.IsInUse()) continue;
                var inv = chest.GetInventory();
                if (inv == null) continue;
                // Check if chest has at least one free slot
                if (inv.GetEmptySlots() <= 0) continue;

                float d = Vector3.Distance(transform.position, chest.transform.position);
                if (d < bestDist) { bestDist = d; best = chest; }
            }
            return best;
        }

        private Fermenter FindEmptyFermenter()
        {
            foreach (var f in _nearbyFermenters)
            {
                if (f == null) continue;
                if (s_claimedFermenters.Contains(f.GetInstanceID())) continue;
                if (GetFermenterStatus(f) != FermenterStatus.Empty) continue;
                if (_meadBasePrefab != null && !FermenterAccepts(f, _meadBasePrefab)) continue;
                return f;
            }
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Recipe / fermenter helpers
        // ═════════════════════════════════════════════════════════════════════

        private bool CanCraftFromChests(Recipe recipe)
        {
            foreach (var req in recipe.m_resources)
            {
                if (req?.m_resItem == null) continue;
                string prefab = req.m_resItem.gameObject.name;
                int needed = req.GetAmount(1);
                if (needed <= 0) continue;

                int available = 0;
                // Check companion inventory first
                var compInv = _humanoid?.GetInventory();
                if (compInv != null)
                    available += HomesteadController.CountItemInInventory(compInv, prefab);

                foreach (var chest in _nearbyChests)
                {
                    var inv = chest?.GetInventory();
                    if (inv == null) continue;
                    available += HomesteadController.CountItemInInventory(inv, prefab);
                }
                if (available < needed) return false;
            }
            return true;
        }

        private enum FermenterStatus { Empty, Fermenting, Ready }

        private FermenterStatus GetFermenterStatus(Fermenter fermenter)
        {
            var fnview = fermenter.GetComponent<ZNetView>();
            if (fnview == null || fnview.GetZDO() == null) return FermenterStatus.Empty;

            string content = fnview.GetZDO().GetString(ZDOVars.s_content);
            if (string.IsNullOrEmpty(content)) return FermenterStatus.Empty;

            long startTicks = fnview.GetZDO().GetLong(ZDOVars.s_startTime, 0L);
            if (startTicks == 0L) return FermenterStatus.Fermenting;

            DateTime startTime = new DateTime(startTicks);
            double elapsed = (ZNet.instance.GetTime() - startTime).TotalSeconds;

            return elapsed > (double)fermenter.m_fermentationDuration
                ? FermenterStatus.Ready
                : FermenterStatus.Fermenting;
        }

        private static bool FermenterAccepts(Fermenter fermenter, string prefabName)
        {
            foreach (var conv in fermenter.m_conversion)
            {
                if (conv?.m_from != null && conv.m_from.gameObject.name == prefabName)
                    return true;
            }
            return false;
        }

        private static void EnsureFermenterInputCache()
        {
            if (s_fermenterInputPrefabs != null) return;
            s_fermenterInputPrefabs = new HashSet<string>();

            var fermenters = Resources.FindObjectsOfTypeAll<Fermenter>();
            foreach (var f in fermenters)
            {
                if (f?.m_conversion == null) continue;
                foreach (var conv in f.m_conversion)
                {
                    if (conv?.m_from != null)
                        s_fermenterInputPrefabs.Add(conv.m_from.gameObject.name);
                }
            }
        }

        private static bool IsFermenterInput(string prefabName)
        {
            if (s_fermenterInputPrefabs == null) return false;
            return s_fermenterInputPrefabs.Contains(prefabName);
        }

        private string FindMeadBaseInInventory(Inventory inv)
        {
            return FindMeadBaseInInventoryInternal(inv);
        }

        private string FindMeadBaseInInventoryInternal(Inventory inv)
        {
            var items = inv.GetAllItems();
            foreach (var item in items)
            {
                string prefab = item.m_dropPrefab?.name;
                if (prefab != null && IsFermenterInput(prefab))
                    return prefab;
            }
            return null;
        }

        private bool HasStorableOutput()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return false;
            var items = inv.GetAllItems();
            foreach (var item in items)
            {
                if (IsStorableOutput(item)) return true;
            }
            return false;
        }

        private bool IsStorableOutput(ItemDrop.ItemData item)
        {
            if (item == null) return false;
            var shared = item.m_shared;
            if (shared == null) return false;

            // Store consumables (food, meads) and materials (mead bases)
            if (shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable &&
                shared.m_itemType != ItemDrop.ItemData.ItemType.Material)
                return false;

            // Don't store equipped items
            if (item.m_equipped) return false;

            // Only store if it's food, mead, or a fermenter input
            bool isFood = shared.m_food > 0f || shared.m_foodStamina > 0f;
            bool isFermenterInput = item.m_dropPrefab != null && IsFermenterInput(item.m_dropPrefab.name);

            return isFood || isFermenterInput;
        }

        private void PickUpNearbyDrops(Vector3 center)
        {
            if (_itemLayerMask < 0) _itemLayerMask = LayerMask.GetMask("item");
            int count = Physics.OverlapSphereNonAlloc(center, 3f, _dropScanBuffer, _itemLayerMask);
            var compInv = _humanoid?.GetInventory();
            if (compInv == null) return;

            for (int i = 0; i < count; i++)
            {
                var col = _dropScanBuffer[i];
                if (col == null) continue;
                var itemDrop = col.GetComponentInParent<ItemDrop>();
                if (itemDrop == null || itemDrop.m_itemData == null) continue;

                var dnview = itemDrop.GetComponent<ZNetView>();
                if (dnview == null || dnview.GetZDO() == null) continue;

                if (compInv.CanAddItem(itemDrop.m_itemData, itemDrop.m_itemData.m_stack))
                {
                    compInv.AddItem(itemDrop.m_itemData);
                    dnview.ClaimOwnership();
                    dnview.Destroy();
                    Log($"Picked up {itemDrop.m_itemData.m_stack}x \"{itemDrop.m_itemData.m_shared?.m_name}\"");
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  Movement / state helpers
        // ═════════════════════════════════════════════════════════════════════

        private bool ShouldAbort()
        {
            if (_ai != null && _ai.IsInCombat) return true;
            if (CompanionInteractPanel.IsOpenFor(_setup) || CompanionRadialMenu.IsOpenFor(_setup)) return true;
            return false;
        }

        private void ClearFollowForMovement()
        {
            if (_ai != null)
            {
                _ai.SetFollowTarget(null);
                Log("Cleared follow target — CookController driving movement");
            }
        }

        private void RestoreFollow()
        {
            if (_ai == null) return;
            bool follow = _setup != null && _setup.GetFollow();
            bool stayHome = _setup != null && _setup.GetStayHome() && _setup.HasHomePosition();
            if (stayHome)
            {
                _ai.SetFollowTarget(null);
                _ai.SetPatrolPointAt(_setup.GetHomePosition());
            }
            else if (follow && Player.m_localPlayer != null)
            {
                _ai.SetFollowTarget(Player.m_localPlayer.gameObject);
            }
        }

        private void ClaimFermenter(Fermenter f)
        {
            UnclaimFermenter();
            _claimedFermenterId = f?.GetInstanceID() ?? 0;
            if (_claimedFermenterId != 0) s_claimedFermenters.Add(_claimedFermenterId);
        }

        private void UnclaimFermenter()
        {
            if (_claimedFermenterId != 0)
            {
                s_claimedFermenters.Remove(_claimedFermenterId);
                _claimedFermenterId = 0;
            }
        }

        private void ResetStuck()
        {
            _stuckTimer = 0f;
            _stuckCheckTimer = 0f;
            _stuckCheckPos = transform.position;
        }

        private void UpdateStuck(float dt, string target, float dist)
        {
            _stuckCheckTimer += dt;
            if (_stuckCheckTimer < StuckCheckPeriod) return;
            _stuckCheckTimer = 0f;

            float moved = Vector3.Distance(transform.position, _stuckCheckPos);
            _stuckCheckPos = transform.position;

            if (moved < StuckMinDistance)
            {
                _stuckTimer += StuckCheckPeriod;
                if (_stuckTimer > MoveTimeout)
                {
                    Log($"Stuck moving to {target} (dist={dist:F1}m) — aborting");
                    Abort($"stuck moving to {target}");
                }
            }
            else
            {
                _stuckTimer = 0f;
            }
        }

        private void Finish()
        {
            Log("Finish — restoring follow target");
            UnclaimFermenter();
            CloseAnyChest();
            _targetCauldron = null;
            _targetFermenter = null;
            _targetChest = null;
            _outputChest = null;
            _selectedRecipe = null;
            _meadBasePrefab = null;
            _ingredientList.Clear();
            _phase = CookPhase.Idle;
            _scanTimer = ScanInterval;
            RestoreFollow();
        }

        private void Abort(string reason)
        {
            Log($"Aborted — {reason} (phase was {_phase})");
            UnclaimFermenter();
            CloseAnyChest();
            _targetCauldron = null;
            _targetFermenter = null;
            _targetChest = null;
            _outputChest = null;
            _selectedRecipe = null;
            _meadBasePrefab = null;
            _ingredientList.Clear();
            _phase = CookPhase.Idle;
            _scanTimer = ScanInterval;
            RestoreFollow();
        }

        private void CloseCurrentChest()
        {
            if (_chestOpened && _targetChest != null)
                _targetChest.SetInUse(false);
            _chestOpened = false;
        }

        private void CloseAnyChest()
        {
            if (!_chestOpened) return;
            if (_phase == CookPhase.StoringOutput && _outputChest != null)
                _outputChest.SetInUse(false);
            else if (_targetChest != null)
                _targetChest.SetInUse(false);
            _chestOpened = false;
        }

        private void Log(string msg)
        {
            string name = _character?.m_name ?? "?";
            CompanionsPlugin.Log.LogInfo($"[Cook|{name}] {msg}");
        }
    }
}
