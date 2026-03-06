using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Companion fishing controller. Active in ModeFish.
    ///
    /// State machine:
    ///   Idle           — check for rod + bait, periodic rescan
    ///   FindingSpot    — scan surroundings for fishable water
    ///   MovingToSpot   — pathfind to shore position
    ///   Casting        — play cast animation (StartAttack)
    ///   Waiting        — timer for nibble (20-50s), miss/hook rolls
    ///   Reeling        — pull in fish (3-8s), stamina drain
    ///   Collecting     — add fish to inventory, loop or idle
    ///
    /// Uses simulated fishing — no real FishingFloat interaction.
    /// Bait is consumed by the rod's attack on cast.
    /// Fish type determined by bait → fish probability tables from Fish prefabs.
    /// </summary>
    public class FishController : MonoBehaviour
    {
        // ══════════════════════════════════════════════════════════════════════
        //  Configuration
        // ══════════════════════════════════════════════════════════════════════

        private static float ScanRadius     => ModConfig.FishScanRadius.Value;
        private static float WaitTimeMin    => ModConfig.FishWaitTimeMin.Value;
        private static float WaitTimeMax    => ModConfig.FishWaitTimeMax.Value;
        private static float ReelTimeMin    => ModConfig.FishReelTimeMin.Value;
        private static float ReelTimeMax    => ModConfig.FishReelTimeMax.Value;
        private static float HookChance     => ModConfig.FishHookChance.Value;
        private static float MissChance     => ModConfig.FishMissChance.Value;
        private static float ReelStamDrain  => ModConfig.FishReelStaminaDrain.Value;

        private const float ScanInterval       = 5f;
        private const float StuckTimeout       = 6f;
        private const float MinFishingDepth    = 2f;
        private const float SpeechCooldown     = 30f;
        private const int   MaxConsecutiveMiss = 3;

        // ══════════════════════════════════════════════════════════════════════
        //  Components
        // ══════════════════════════════════════════════════════════════════════

        private ZNetView         _nview;
        private CompanionAI      _ai;
        private CompanionCombatAI _combatAI;
        private CompanionSetup   _setup;
        private Humanoid         _humanoid;
        private Character        _character;
        private CompanionStamina _stamina;
        private CompanionTalk    _talk;
        private ZSyncAnimation   _zanim;

        // ══════════════════════════════════════════════════════════════════════
        //  State
        // ══════════════════════════════════════════════════════════════════════

        private enum FishState
        {
            Idle,
            FindingSpot,
            MovingToSpot,
            Casting,
            Waiting,
            Reeling,
            Collecting
        }

        private FishState _state = FishState.Idle;
        private float     _stateTimer;
        private float     _scanTimer;
        private float     _stuckTimer;
        private int       _stuckCount;
        private float     _lastSpeechTime;
        private Vector3   _lastPos;
        private int       _missCount;
        private bool      _castInitiated;

        // Fishing spot
        private Vector3 _shorePos;
        private Vector3 _waterPos;
        private WaterVolume _cachedWaterVolume;

        // Current catch info
        private string _currentBaitName;

        // ── Bait→Fish lookup (static, built once per session) ──────────────

        private static Dictionary<string, List<FishEntry>> s_baitFishMap;
        private static HashSet<string> s_baitNames;
        private static bool s_mapBuilt;

        private struct FishEntry
        {
            public string PickupPrefabName;
            public float  Chance;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════

        public bool IsActive => GetMode() == CompanionSetup.ModeFish
                             && _state != FishState.Idle;

        public void NotifyActionModeChanged()
        {
            if (_state != FishState.Idle)
                Log($"NotifyActionModeChanged — resetting from {_state}");
            ResetToIdle();
            // Reset speech cooldown so user gets immediate feedback on mode entry
            _lastSpeechTime = 0f;
            // Unequip the fishing rod so it isn't used as a combat weapon
            UnequipFishingRod();
        }

        private void UnequipFishingRod()
        {
            if (_humanoid == null) return;
            var weapon = _humanoid.GetCurrentWeapon();
            if (weapon != null &&
                weapon.m_shared.m_animationState == ItemDrop.ItemData.AnimationState.FishingRod)
            {
                _humanoid.UnequipItem(weapon);
                Log("Unequipped fishing rod on mode change");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ══════════════════════════════════════════════════════════════════════

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _ai        = GetComponent<CompanionAI>();
            _combatAI  = GetComponent<CompanionCombatAI>();
            _setup     = GetComponent<CompanionSetup>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _stamina   = GetComponent<CompanionStamina>();
            _talk      = GetComponent<CompanionTalk>();
            _zanim     = GetComponent<ZSyncAnimation>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsOwner()) return;

            int mode = GetMode();
            if (mode != CompanionSetup.ModeFish)
            {
                if (_state != FishState.Idle)
                    ResetToIdle();
                return;
            }

            // UI guard — freeze while panel is open
            if (IsCompanionUIOpen())
            {
                _ai.StopMoving();
                return;
            }

            // Yield to combat AI during self-defence
            if (_combatAI != null && _combatAI.IsEngaged)
                return;

            switch (_state)
            {
                case FishState.Idle:         UpdateIdle();         break;
                case FishState.FindingSpot:  UpdateFindingSpot();  break;
                case FishState.MovingToSpot: UpdateMovingToSpot(); break;
                case FishState.Casting:      UpdateCasting();      break;
                case FishState.Waiting:      UpdateWaiting();      break;
                case FishState.Reeling:      UpdateReeling();      break;
                case FishState.Collecting:   UpdateCollecting();   break;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: Idle — verify rod + bait, then start looking for water
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateIdle()
        {
            _scanTimer -= Time.deltaTime;
            if (_scanTimer > 0f) return;
            _scanTimer = ScanInterval;

            BuildBaitFishMapIfNeeded();

            var rod = FindFishingRod();
            if (rod == null)
            {
                SayThrottled(ModLocalization.Loc("hc_speech_fish_need_rod"));
                return;
            }

            var bait = FindBait();
            if (bait == null)
            {
                SayThrottled(ModLocalization.Loc("hc_speech_fish_need_bait"));
                return;
            }

            _state = FishState.FindingSpot;
            CompanionsPlugin.Log.LogInfo(
                $"[FishController] Idle → FindingSpot (rod + bait found) companion=\"{_character?.m_name}\"");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: FindingSpot — scan for nearby fishable water
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateFindingSpot()
        {
            if (FindWaterSpot(out Vector3 shore, out Vector3 water))
            {
                _shorePos = shore;
                _waterPos = water;
                _lastPos = transform.position;
                _stuckTimer = 0f;
                _state = FishState.MovingToSpot;
                Log($"FindingSpot → MovingToSpot (shore={_shorePos:F1}, water={_waterPos:F1})");
            }
            else
            {
                SayThrottled(ModLocalization.Loc("hc_speech_fish_no_water"));
                _state = FishState.Idle;
                _scanTimer = ScanInterval * 2f;
                CompanionsPlugin.Log.LogInfo(
                    $"[FishController] FindingSpot → Idle (no water found within {ScanRadius}m) " +
                    $"pos={transform.position:F1} companion=\"{_character?.m_name}\"");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: MovingToSpot — walk to shore position
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateMovingToSpot()
        {
            float dist = Vector3.Distance(transform.position, _shorePos);
            if (dist < 3f)
            {
                _ai.LookAtPoint(_waterPos);
                _state = FishState.Casting;
                _stateTimer = 0f;
                Log("MovingToSpot → Casting (arrived at shore)");
                return;
            }

            _ai.MoveToPoint(Time.deltaTime, _shorePos, 2f, true);

            // Stuck detection — check once per second
            _stuckTimer += Time.deltaTime;
            if (_stuckTimer >= 1f)
            {
                _stuckTimer = 0f;
                float moved = Vector3.Distance(transform.position, _lastPos);
                _lastPos = transform.position;

                if (moved < 0.3f)
                {
                    _stuckCount++;
                    if (_stuckCount >= 5)
                    {
                        Log("MovingToSpot → FindingSpot (stuck)");
                        _state = FishState.FindingSpot;
                        _stuckCount = 0;
                    }
                }
                else
                {
                    _stuckCount = 0;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: Casting — equip rod, play cast animation
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCasting()
        {
            if (!_castInitiated)
            {
                // Equip rod and initiate cast — retries each frame until successful
                var rod = FindFishingRod();
                if (rod == null)
                {
                    Log("Casting → Idle (rod missing)");
                    ResetToIdle();
                    return;
                }

                var bait = FindBait();
                if (bait == null)
                {
                    Log("Casting → Idle (bait missing)");
                    ResetToIdle();
                    return;
                }

                _currentBaitName = bait.m_dropPrefab?.name ?? "";

                // Suppress auto-equip so bait consumption doesn't trigger weapon swap
                if (_setup != null) _setup.SuppressAutoEquip = true;

                // Equip the fishing rod if not already equipped
                var weapon = _humanoid.GetCurrentWeapon();
                if (weapon == null || weapon.m_shared.m_animationState != ItemDrop.ItemData.AnimationState.FishingRod)
                {
                    _humanoid.EquipItem(rod);
                    return; // retry next frame after equip
                }

                _ai.StopMoving();
                _ai.LookAtPoint(_waterPos);

                // Wait until companion is actually facing the water before casting
                if (!_ai.IsLookingAtPoint(_waterPos, 30f))
                    return; // keep rotating, retry next frame

                // StartAttack plays the cast animation; the Attack system will
                // consume 1 bait and try to spawn a FishingFloat (which self-destructs
                // because GetOwner can't find a Player match).
                bool attacked = _humanoid.StartAttack(null, false);
                if (!attacked)
                    return; // retry next frame

                Log("Casting — cast animation started");
                _castInitiated = true;

                // Kill forward lunge momentum immediately
                var rb = _character?.GetComponent<Rigidbody>();
                if (rb != null)
                    rb.linearVelocity = Vector3.zero;
                return;
            }

            // Wait for attack animation to fully finish
            // Don't zero velocity here — it prevents root motion and makes the cast look incomplete
            _ai.StopMoving();

            if (!_humanoid.InAttack())
            {
                _castInitiated = false;
                float waitTime = Random.Range(WaitTimeMin, WaitTimeMax);
                _stateTimer = waitTime;
                _state = FishState.Waiting;
                Log($"Casting → Waiting ({waitTime:F1}s)");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: Waiting — timer ticks down, then nibble/hook rolls
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateWaiting()
        {
            _ai.StopMoving();
            _ai.LookAtPoint(_waterPos);
            _stateTimer -= Time.deltaTime;
            if (_stateTimer > 0f) return;

            // Miss check — no nibble, try again
            if (Random.value < MissChance)
            {
                _missCount++;
                if (_missCount >= MaxConsecutiveMiss)
                {
                    // Too many misses at this spot — find a new one
                    _missCount = 0;
                    _state = FishState.FindingSpot;
                    Log("Waiting → FindingSpot (too many misses)");
                    return;
                }

                _stateTimer = Random.Range(WaitTimeMin, WaitTimeMax);
                Log($"Waiting — miss #{_missCount}, waiting {_stateTimer:F1}s more");
                return;
            }

            // Nibble! Hook check
            if (Random.value > HookChance)
            {
                // Hook failed
                _talk?.Say(ModLocalization.Loc("hc_speech_fish_got_away"));
                _stateTimer = Random.Range(WaitTimeMin * 0.5f, WaitTimeMax * 0.5f);
                Log($"Waiting — hook failed, waiting {_stateTimer:F1}s more");
                return;
            }

            // Hooked! Start reeling
            float reelTime = Random.Range(ReelTimeMin, ReelTimeMax);
            _stateTimer = reelTime;
            _state = FishState.Reeling;
            _missCount = 0;
            Log($"Waiting → Reeling ({reelTime:F1}s)");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: Reeling — pull the fish in, drain stamina
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateReeling()
        {
            _ai.StopMoving();
            _ai.LookAtPoint(_waterPos);
            _stateTimer -= Time.deltaTime;

            // Drain stamina while reeling
            if (_stamina != null)
            {
                _stamina.Drain(ReelStamDrain * Time.deltaTime);

                // Out of stamina — fish escapes
                if (_stamina.Stamina <= 0f)
                {
                    _talk?.Say(ModLocalization.Loc("hc_speech_fish_got_away"));
                    _stateTimer = Random.Range(WaitTimeMin * 0.5f, WaitTimeMax * 0.5f);
                    _state = FishState.Waiting;
                    Log("Reeling → Waiting (stamina depleted, fish escaped)");
                    return;
                }
            }

            if (_stateTimer <= 0f)
            {
                _state = FishState.Collecting;
                Log("Reeling → Collecting");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  State: Collecting — determine fish, add to inventory
        // ══════════════════════════════════════════════════════════════════════

        private void UpdateCollecting()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null)
            {
                ResetToIdle();
                return;
            }

            string fishPrefab = DetermineCatch(_currentBaitName);
            if (!string.IsNullOrEmpty(fishPrefab))
            {
                var prefab = ZNetScene.instance?.GetPrefab(fishPrefab);
                if (prefab != null)
                {
                    var itemDrop = prefab.GetComponent<ItemDrop>();
                    if (itemDrop != null)
                    {
                        if (inv.CanAddItem(prefab, 1))
                        {
                            inv.AddItem(prefab, 1);
                            string fishName = itemDrop.m_itemData.m_shared.m_name;
                            string locName = Localization.instance != null
                                ? Localization.instance.Localize(fishName)
                                : fishName;
                            _talk?.Say(ModLocalization.LocFmt("hc_speech_fish_caught", locName), force: true);
                            PlayCatchEffect();
                            Log($"Collecting — caught {fishPrefab} ({locName})");
                        }
                        else
                        {
                            _talk?.Say(ModLocalization.Loc("hc_speech_fish_full"), force: true);
                            Log("Collecting → Idle (inventory full)");
                            ResetToIdle();
                            return;
                        }
                    }
                }
                else
                {
                    Log($"Collecting — fish prefab '{fishPrefab}' not found in ZNetScene");
                }
            }
            else
            {
                Log("Collecting — no fish determined (empty catch)");
            }

            // Check if we can keep fishing (bait remaining)
            var bait = FindBait();
            if (bait == null)
            {
                SayThrottled(ModLocalization.Loc("hc_speech_fish_need_bait"));
                Log("Collecting → Idle (no bait left)");
                ResetToIdle();
                return;
            }

            // Keep fishing at the same spot
            _stateTimer = 0f;
            _state = FishState.Casting;
            Log("Collecting → Casting (continuing to fish)");
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Water Spot Detection
        // ══════════════════════════════════════════════════════════════════════

        private bool FindWaterSpot(out Vector3 shorePos, out Vector3 waterPos)
        {
            shorePos = Vector3.zero;
            waterPos = Vector3.zero;

            if (ZoneSystem.instance == null) return false;

            Vector3 origin = transform.position;
            float bestDist = float.MaxValue;
            bool found = false;

            // Scan 12 directions at multiple distances
            for (int angle = 0; angle < 360; angle += 30)
            {
                Vector3 dir = Quaternion.Euler(0, angle, 0) * Vector3.forward;

                for (float dist = 8f; dist <= ScanRadius; dist += 5f)
                {
                    Vector3 testPoint = origin + dir * dist;
                    float waterLevel = GetWaterLevel(testPoint);

                    // Floating.GetWaterLevel returns -10000 when no water body covers the point
                    if (waterLevel < -999f)
                        continue;

                    if (!ZoneSystem.instance.GetSolidHeight(testPoint, out float groundHeight))
                        continue;

                    float depth = waterLevel - groundHeight;
                    if (depth < MinFishingDepth)
                        continue;

                    // Found deep enough water — find a shore point by stepping back
                    Vector3 shore = Vector3.zero;
                    bool foundShore = false;
                    for (float backStep = 3f; backStep <= dist; backStep += 1f)
                    {
                        Vector3 check = origin + dir * (dist - backStep);
                        if (!ZoneSystem.instance.GetSolidHeight(check, out float shoreGround))
                            continue;

                        float shoreDepth = waterLevel - shoreGround;
                        if (shoreDepth <= 0f) // ground at or above water level
                        {
                            shore = check;
                            foundShore = true;
                            break;
                        }
                    }

                    if (!foundShore)
                        break; // no dry land in this direction, try next angle

                    // Push shore position 2m back from water edge so the cast
                    // animation's forward momentum doesn't push companion into water
                    Vector3 awayFromWater = (shore - testPoint).normalized;
                    shore += awayFromWater * 2f;

                    float totalDist = Vector3.Distance(origin, shore);
                    if (totalDist < bestDist)
                    {
                        bestDist = totalDist;
                        shorePos = shore;
                        waterPos = testPoint;
                        found = true;
                    }
                    break; // found water in this direction, try next angle
                }
            }

            return found;
        }

        private float GetWaterLevel(Vector3 point)
        {
            return Floating.GetWaterLevel(point, ref _cachedWaterVolume);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Inventory Helpers
        // ══════════════════════════════════════════════════════════════════════

        private ItemDrop.ItemData FindFishingRod()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            foreach (var item in inv.GetAllItems())
            {
                if (item.m_shared.m_animationState == ItemDrop.ItemData.AnimationState.FishingRod)
                    return item;
            }
            return null;
        }

        private ItemDrop.ItemData FindBait()
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            BuildBaitFishMapIfNeeded();
            if (s_baitNames == null || s_baitNames.Count == 0) return null;

            foreach (var item in inv.GetAllItems())
            {
                if (item.m_dropPrefab != null && s_baitNames.Contains(item.m_dropPrefab.name))
                    return item;
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Bait→Fish Lookup
        // ══════════════════════════════════════════════════════════════════════

        private static void BuildBaitFishMapIfNeeded()
        {
            if (s_mapBuilt) return;
            if (ZNetScene.instance == null) return;

            s_baitFishMap = new Dictionary<string, List<FishEntry>>();
            s_baitNames = new HashSet<string>();

            int fishCount = 0;
            foreach (var prefab in ZNetScene.instance.m_prefabs)
            {
                var fish = prefab.GetComponent<Fish>();
                if (fish == null) continue;

                // m_pickupItem is set at Fish.Awake → on prefabs it may be null.
                // Fall back to the fish's own gameObject (same as vanilla Awake).
                string pickupName = fish.m_pickupItem != null
                    ? fish.m_pickupItem.name
                    : prefab.name;

                fishCount++;

                if (fish.m_baits == null || fish.m_baits.Count == 0)
                    continue;

                foreach (var bait in fish.m_baits)
                {
                    if (bait.m_bait == null) continue;
                    string baitName = bait.m_bait.name;

                    s_baitNames.Add(baitName);

                    if (!s_baitFishMap.ContainsKey(baitName))
                        s_baitFishMap[baitName] = new List<FishEntry>();

                    s_baitFishMap[baitName].Add(new FishEntry
                    {
                        PickupPrefabName = pickupName,
                        Chance = bait.m_chance
                    });
                }
            }

            s_mapBuilt = true;
            CompanionsPlugin.Log.LogInfo(
                $"[FishController] Built bait→fish map: {fishCount} Fish prefabs, " +
                $"{s_baitNames.Count} bait types, {s_baitFishMap.Count} mappings");
        }

        private static string DetermineCatch(string baitName)
        {
            if (string.IsNullOrEmpty(baitName) || s_baitFishMap == null)
                return null;

            if (!s_baitFishMap.TryGetValue(baitName, out var entries) || entries.Count == 0)
                return null;

            // Weighted random selection based on bait chances
            float totalChance = 0f;
            foreach (var e in entries)
                totalChance += e.Chance;

            if (totalChance <= 0f)
                return entries[0].PickupPrefabName; // fallback

            float roll = Random.value * totalChance;
            float cumulative = 0f;
            foreach (var e in entries)
            {
                cumulative += e.Chance;
                if (roll <= cumulative)
                    return e.PickupPrefabName;
            }

            return entries[entries.Count - 1].PickupPrefabName;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private void ResetToIdle()
        {
            _state = FishState.Idle;
            _stateTimer = 0f;
            _scanTimer = 0f;
            _missCount = 0;
            _stuckTimer = 0f;
            _stuckCount = 0;
            _castInitiated = false;
            _cachedWaterVolume = null;

            // Restore auto-equip so combat gear can be re-equipped
            if (_setup != null && _setup.SuppressAutoEquip)
            {
                _setup.SuppressAutoEquip = false;
                _setup.SyncEquipmentToInventory();
            }
        }

        private int GetMode()
        {
            var zdo = _nview?.GetZDO();
            return zdo?.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow)
                ?? CompanionSetup.ModeFollow;
        }

        private bool IsCompanionUIOpen()
        {
            return CompanionInteractPanel.IsOpenFor(_setup)
                || CompanionRadialMenu.IsOpenFor(_setup)
                || HomeZonePanel.IsOpenFor(_setup);
        }

        private void SayThrottled(string text)
        {
            float now = Time.time;
            if (now - _lastSpeechTime < SpeechCooldown) return;
            _lastSpeechTime = now;
            _talk?.Say(text);
        }

        private void PlayCatchEffect()
        {
            var player = Player.m_localPlayer;
            if (player == null || _character == null) return;
            player.m_skillLevelupEffects.Create(
                _character.GetHeadPoint(),
                _character.transform.rotation,
                _character.transform);
        }

        private void Log(string msg)
        {
            CompanionsPlugin.Log.LogDebug($"[FishController] {msg}");
        }
    }
}
