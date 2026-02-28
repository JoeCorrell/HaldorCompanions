using System.Globalization;
using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Food system for companions — mirrors the player's food mechanics.
    /// Tracks up to 3 active food effects (matching the 3 food inventory slots).
    /// Base health = 25, base stamina = 25. Food provides bonuses on top.
    /// Auto-consumes food from inventory when a slot is empty.
    /// Persists to ZDO for save/load.
    /// </summary>
    public class CompanionFood : MonoBehaviour
    {
        public const int   MaxFoodSlots = 3;
        public const float BaseHealth   = 25f;
        public const float BaseStamina  = 25f;

        public struct FoodEffect
        {
            public string ItemName;
            public string ItemPrefabName;
            public float  HealthBonus;
            public float  StaminaBonus;
            public float  EitrBonus;
            public float  FoodRegen;
            public float  RemainingTime;
            public float  TotalTime;

            public bool IsActive => RemainingTime > 0f && !string.IsNullOrEmpty(ItemName);
        }

        private FoodEffect[] _foods = new FoodEffect[MaxFoodSlots];

        private ZNetView        _nview;
        private Humanoid        _humanoid;
        private Character       _character;
        private SEMan           _seman;
        private ZSyncAnimation  _zanim;
        private CompanionStamina _stamina;
        private CompanionSetup  _setup;
        private bool            _initialized;

        private float _saveTimer;
        private float _consumeTimer;
        private float _remoteSyncTimer;
        private float _foodRegenTimer;
        private float _meadCheckTimer;
        private float _meadCooldownTimer;

        private const float SaveInterval         = 5f;
        private const float ConsumeCheckInterval = 1f;
        private const float RemoteSyncInterval   = 0.5f;
        private const float FoodRegenInterval    = 10f;
        private const float MeadCheckInterval    = 2f;
        private const float MeadCooldown         = 10f;
        private const float HealthMeadThreshold  = 0.5f;   // use when HP < 50%
        private const float StaminaMeadThreshold = 0.25f;  // use when stamina < 25%

        // ZDO keys — one per food slot
        private static readonly int[] FoodHashes = new int[MaxFoodSlots];

        static CompanionFood()
        {
            for (int i = 0; i < MaxFoodSlots; i++)
                FoodHashes[i] = StringExtensionMethods.GetStableHashCode($"HC_Food_{i}");
        }

        // ── Public accessors ────────────────────────────────────────────────

        public float TotalHealthBonus
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < MaxFoodSlots; i++)
                    if (_foods[i].IsActive) total += GetScaledBonus(_foods[i].HealthBonus, _foods[i]);
                return total;
            }
        }

        public float TotalStaminaBonus
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < MaxFoodSlots; i++)
                    if (_foods[i].IsActive) total += GetScaledBonus(_foods[i].StaminaBonus, _foods[i]);
                return total;
            }
        }

        public float TotalEitrBonus
        {
            get
            {
                float total = 0f;
                for (int i = 0; i < MaxFoodSlots; i++)
                    if (_foods[i].IsActive) total += GetScaledBonus(_foods[i].EitrBonus, _foods[i]);
                return total;
            }
        }

        private static float GetScaledBonus(float baseBonus, FoodEffect food)
        {
            if (!food.IsActive) return 0f;
            float total = Mathf.Max(1f, food.TotalTime);
            float t = Mathf.Clamp01(food.RemainingTime / total);
            // Mirror vanilla food scaling (front-loaded with pow 0.3)
            return baseBonus * Mathf.Pow(t, 0.3f);
        }

        public FoodEffect GetFood(int slot)
        {
            if (slot < 0 || slot >= MaxFoodSlots) return default;
            return _foods[slot];
        }

        // ── Lifecycle ───────────────────────────────────────────────────────

        private void Awake()
        {
            _nview     = GetComponent<ZNetView>();
            _humanoid  = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _seman     = _character != null ? _character.GetSEMan() : null;
            _zanim     = GetComponent<ZSyncAnimation>();
            _stamina   = GetComponent<CompanionStamina>();
            _setup     = GetComponent<CompanionSetup>();
        }

        private void Start()
        {
            if (!_initialized) TryInit();
        }

        private void TryInit()
        {
            if (_initialized) return;
            if (_nview == null || _nview.GetZDO() == null) return;
            if (_seman == null && _character != null) _seman = _character.GetSEMan();

            LoadFromZDO();
            ClampHealth();
            _initialized = true;

            int activeCount = 0;
            for (int i = 0; i < MaxFoodSlots; i++)
                if (_foods[i].IsActive) activeCount++;
            CompanionsPlugin.Log.LogDebug(
                $"[Food] Initialized — {activeCount}/{MaxFoodSlots} active food slots " +
                $"hpBonus={TotalHealthBonus:F0} stamBonus={TotalStaminaBonus:F0} " +
                $"companion=\"{_character?.m_name ?? "?"}\"");
        }

        private void Update()
        {
            if (!_initialized) { TryInit(); return; }
            if (_nview == null || _nview.GetZDO() == null) return;
            if (!_nview.IsOwner())
            {
                _remoteSyncTimer -= Time.deltaTime;
                if (_remoteSyncTimer <= 0f)
                {
                    _remoteSyncTimer = RemoteSyncInterval;
                    LoadFromZDO();
                }
                return;
            }

            float dt = Time.deltaTime;

            // Tick food timers
            bool anyExpired = false;
            bool anyTicked = false;
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                var f = _foods[i];
                if (!f.IsActive) continue;
                anyTicked = true;
                f.RemainingTime -= dt;
                if (f.RemainingTime <= 0f)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] Slot {i} expired — \"{f.ItemName}\" " +
                        $"(hp={f.HealthBonus:F0} stam={f.StaminaBonus:F0} " +
                        $"eitr={f.EitrBonus:F0} regen={f.FoodRegen:F1}) " +
                        $"companion=\"{_character?.m_name ?? "?"}\"");
                    _foods[i] = default;
                    anyExpired = true;
                }
                else
                {
                    _foods[i] = f;
                }
            }

            if (anyExpired || anyTicked)
                ClampHealth();

            // Passive food regen — vanilla heals every 10s based on sum of m_foodRegen
            _foodRegenTimer += dt;
            if (_foodRegenTimer >= FoodRegenInterval)
            {
                _foodRegenTimer = 0f;
                float regen = 0f;
                for (int i = 0; i < MaxFoodSlots; i++)
                    if (_foods[i].IsActive) regen += _foods[i].FoodRegen;
                if (regen > 0f && _character != null)
                {
                    float hpBefore = _character.GetHealth();
                    _character.Heal(regen);
                    float hpAfter = _character.GetHealth();
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] Regen tick — healed {regen:F1} HP " +
                        $"({hpBefore:F1} → {hpAfter:F1} / {_character.GetMaxHealth():F1}) " +
                        $"companion=\"{_character.m_name}\"");
                }
            }

            // Auto-consume from food slots
            _consumeTimer -= dt;
            if (_consumeTimer <= 0f)
            {
                _consumeTimer = ConsumeCheckInterval;
                TryAutoConsume();
            }

            // Auto-consume meads when health or stamina is low
            if (_meadCooldownTimer > 0f) _meadCooldownTimer -= dt;
            _meadCheckTimer -= dt;
            if (_meadCheckTimer <= 0f)
            {
                _meadCheckTimer = MeadCheckInterval;
                TryConsumeMead();
            }

            // Periodic ZDO save
            _saveTimer += dt;
            if (_saveTimer >= SaveInterval)
            {
                _saveTimer = 0f;
                SaveToZDO();
            }
        }

        private void OnDestroy()
        {
            if (_initialized) SaveToZDO();
        }

        // ── Health / stamina clamping ───────────────────────────────────────

        private void ClampHealth()
        {
            if (_character == null) return;
            float maxHp = BaseHealth + TotalHealthBonus;
            float curHp = _character.GetHealth();
            if (curHp > maxHp)
            {
                CompanionsPlugin.Log.LogDebug(
                    $"[Food] ClampHealth — {curHp:F1} → {maxHp:F1} (food expired?) " +
                    $"companion=\"{_character.m_name}\"");
                _character.SetHealth(maxHp);
            }
        }

        // ── Auto-consume from food inventory slots ──────────────────────────

        private void TryAutoConsume()
        {
            if (_humanoid == null || _character == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;
            if (_seman == null) _seman = _character.GetSEMan();

            bool consumedAny = false;

            // Fill empty food slots first.
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                if (_foods[i].IsActive) continue;

                var item = FindFoodForSlot(inv, i);
                if (item == null)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] AutoConsume — slot {i} empty, no food found in inventory " +
                        $"(items={inv.GetAllItems().Count}) companion=\"{_character?.m_name ?? "?"}\"");
                    continue;
                }
                if (!CanConsumeFoodItem(item))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] AutoConsume — slot {i} empty, found \"{item.m_shared?.m_name}\" " +
                        $"but CanConsume=false companion=\"{_character?.m_name ?? "?"}\"");
                    continue;
                }
                if (ConsumeIntoSlot(inv, item, i))
                    consumedAny = true;
            }

            // If all slots are occupied, refresh a depleted slot when vanilla would allow re-eating.
            if (!consumedAny)
            {
                int refreshSlot = FindMostDepletedRefreshableSlot();
                if (refreshSlot >= 0)
                {
                    var item = FindFoodForSlot(inv, refreshSlot);
                    if (item != null)
                    {
                        CompanionsPlugin.Log.LogDebug(
                            $"[Food] AutoConsume — refresh slot {refreshSlot} " +
                            $"(\"{_foods[refreshSlot].ItemName}\" {_foods[refreshSlot].RemainingTime:F0}s left) " +
                            $"with \"{item.m_shared?.m_name}\"");
                    }
                    if (CanConsumeFoodItem(item, isRefresh: true) && ConsumeIntoSlot(inv, item, refreshSlot))
                        consumedAny = true;
                }
            }

            if (consumedAny)
            {
                ClampHealth();
                SaveToZDO();
            }
        }

        private bool ConsumeIntoSlot(Inventory inv, ItemDrop.ItemData item, int slot)
        {
            if (inv == null || item == null || slot < 0 || slot >= MaxFoodSlots) return false;

            float burnTime = Mathf.Max(1f, item.m_shared.m_foodBurnTime);
            _foods[slot] = new FoodEffect
            {
                ItemName       = item.m_shared.m_name,
                ItemPrefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : item.m_shared?.m_name ?? "",
                HealthBonus    = item.m_shared.m_food,
                StaminaBonus   = item.m_shared.m_foodStamina,
                EitrBonus      = item.m_shared.m_foodEitr,
                FoodRegen      = item.m_shared.m_foodRegen,
                TotalTime      = burnTime,
                RemainingTime  = burnTime
            };

            // Remove one from stack (triggers Inventory.Changed for UI + sync).
            inv.RemoveOneItem(item);

            // Apply status-effect buff for foods/meads that include one.
            if (_seman != null && item.m_shared.m_consumeStatusEffect != null)
                _seman.AddStatusEffect(item.m_shared.m_consumeStatusEffect, true);

            string animTrigger = _setup != null && _setup.CanWearArmor() ? "eat" : "consume";
            if (_zanim != null) _zanim.SetTrigger(animTrigger);
            CompanionsPlugin.Log.LogDebug(
                $"[Food] Consume anim trigger=\"{animTrigger}\" " +
                $"canWearArmor={_setup?.CanWearArmor()} companion=\"{_character?.m_name}\"");

            // Play eating sound effects (copied from Player prefab's m_consumeItemEffects).
            if (_humanoid != null && _humanoid.m_consumeItemEffects != null)
                _humanoid.m_consumeItemEffects.Create(transform.position, Quaternion.identity);

            // Heal up to new max health.
            float newMax = BaseHealth + TotalHealthBonus;
            float curHp  = _character.GetHealth();
            if (curHp < newMax)
                _character.SetHealth(Mathf.Min(curHp + _foods[slot].HealthBonus, newMax));

            float hpAfter = _character.GetHealth();
            bool hasSE = item.m_shared.m_consumeStatusEffect != null;
            CompanionsPlugin.Log.LogDebug(
                $"[Food] Consumed \"{item.m_shared.m_name}\" → slot {slot} " +
                $"hp={item.m_shared.m_food:F0} stam={item.m_shared.m_foodStamina:F0} " +
                $"eitr={item.m_shared.m_foodEitr:F0} regen={item.m_shared.m_foodRegen:F1} " +
                $"burnTime={burnTime:F0}s statusEffect={hasSE} " +
                $"health {curHp:F1} → {hpAfter:F1} / {newMax:F1} " +
                $"totalStamBonus={TotalStaminaBonus:F1} " +
                $"companion=\"{_character?.m_name ?? "?"}\"");

            return true;
        }

        public bool TryConsumeItem(ItemDrop.ItemData item)
        {
            if (!_initialized) TryInit();
            if (_nview == null || !_nview.IsOwner()) return false;
            if (_humanoid == null || _character == null || item == null) return false;

            var inv = _humanoid.GetInventory();
            if (inv == null || !inv.ContainsItem(item)) return false;

            string itemName = item.m_shared?.m_name ?? "?";
            int slot = FindEmptyFoodSlot();
            if (slot >= 0)
            {
                // Empty slot — normal duplicate check applies
                if (!CanConsumeFoodItem(item))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] TryConsumeItem REJECTED \"{itemName}\" — " +
                        $"empty slot {slot} available but CanConsume=false");
                    return false;
                }
            }
            else
            {
                // No empty slot — try refresh (same food in refreshable slot)
                slot = FindRefreshableSlotForFood(item.m_shared?.m_name);
                if (slot < 0)
                    slot = FindMostDepletedRefreshableSlot();
                if (slot < 0)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] TryConsumeItem REJECTED \"{itemName}\" — " +
                        $"all 3 slots occupied, none refreshable");
                    return false;
                }
                // Refresh path — skip duplicate check since we're replacing same slot
                if (!CanConsumeFoodItem(item, isRefresh: true))
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] TryConsumeItem REJECTED \"{itemName}\" — " +
                        $"refresh slot {slot} found but CanConsume(refresh)=false");
                    return false;
                }
            }

            CompanionsPlugin.Log.LogDebug(
                $"[Food] TryConsumeItem \"{itemName}\" → slot {slot} " +
                $"companion=\"{_character?.m_name ?? "?"}\"");

            if (!ConsumeIntoSlot(inv, item, slot)) return false;

            ClampHealth();
            SaveToZDO();
            return true;
        }

        private int FindEmptyFoodSlot()
        {
            for (int i = 0; i < MaxFoodSlots; i++)
                if (!_foods[i].IsActive) return i;
            return -1;
        }

        private int FindRefreshableSlotForFood(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return -1;
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                if (!_foods[i].IsActive) continue;
                if (_foods[i].ItemName != itemName) continue;
                if (CanEatAgain(i)) return i;
            }
            return -1;
        }

        private int FindMostDepletedRefreshableSlot()
        {
            int bestSlot = -1;
            float leastTime = float.MaxValue;
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                if (!CanEatAgain(i)) continue;
                float t = _foods[i].RemainingTime;
                if (t < leastTime)
                {
                    leastTime = t;
                    bestSlot = i;
                }
            }
            return bestSlot;
        }

        private static bool IsFoodItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
            return item.m_shared.m_food > 0f ||
                   item.m_shared.m_foodStamina > 0f ||
                   item.m_shared.m_foodEitr > 0f;
        }

        private bool CanConsumeFoodItem(ItemDrop.ItemData item, bool isRefresh = false)
        {
            if (!IsFoodItem(item)) return false;
            string name = item.m_shared?.m_name ?? "?";
            if (!isRefresh && IsFoodAlreadyActive(item.m_shared.m_name))
            {
                CompanionsPlugin.Log.LogDebug($"[Food] CanConsume REJECTED \"{name}\" — already active in another slot");
                return false;
            }
            if (!CanApplyConsumeStatus(item))
            {
                CompanionsPlugin.Log.LogDebug($"[Food] CanConsume REJECTED \"{name}\" — status effect conflict");
                return false;
            }
            return true;
        }

        private bool IsFoodAlreadyActive(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return false;
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                if (!_foods[i].IsActive) continue;
                if (_foods[i].ItemName == itemName) return true;
            }
            return false;
        }

        private bool CanEatAgain(int slot)
        {
            if (slot < 0 || slot >= MaxFoodSlots) return false;
            var food = _foods[slot];
            if (!food.IsActive) return false;
            float total = Mathf.Max(1f, food.TotalTime);
            return food.RemainingTime < total * 0.5f;
        }

        private bool CanApplyConsumeStatus(ItemDrop.ItemData item)
        {
            if (_seman == null || item == null || item.m_shared == null) return true;
            var consumeSE = item.m_shared.m_consumeStatusEffect;
            if (consumeSE == null) return true;

            // Mirror Player.CanConsumeItem behavior for consume status effects:
            // block if same effect or same category is already active.
            return !_seman.HaveStatusEffect(consumeSE.NameHash()) &&
                   !_seman.HaveStatusEffectCategory(consumeSE.m_category);
        }

        private ItemDrop.ItemData FindFoodForSlot(Inventory inv, int slot)
        {
            // Scan all inventory slots for valid food (food slots are display-only).
            List<ItemDrop.ItemData> all = inv.GetAllItemsInGridOrder();
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (CanConsumeFoodItem(item)) return item;
            }

            return null;
        }

        // ── Mead / potion auto-consume ────────────────────────────────────

        private enum MeadKind { Unknown, Health, Stamina }

        private void TryConsumeMead()
        {
            if (_humanoid == null || _character == null) return;
            if (_meadCooldownTimer > 0f) return;

            // Don't consume during attack/dodge/stagger — animation conflict
            if (_humanoid.InAttack() || _humanoid.InDodge() || _character.IsStaggering()) return;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;
            if (_seman == null) _seman = _character.GetSEMan();

            float hpPct = _character.GetMaxHealth() > 0f
                ? _character.GetHealth() / _character.GetMaxHealth()
                : 1f;

            float stamPct = _stamina != null ? _stamina.GetStaminaPercentage() : 1f;

            // Health takes priority
            if (hpPct < HealthMeadThreshold)
            {
                var mead = FindMead(inv, MeadKind.Health);
                if (mead != null)
                {
                    if (ConsumeMead(inv, mead, MeadKind.Health))
                        return;
                }
                else
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] Mead check — hp={hpPct * 100f:F0}% below threshold, " +
                        $"no health mead in inventory companion=\"{_character?.m_name ?? "?"}\"");
                }
            }

            // Then stamina
            if (stamPct < StaminaMeadThreshold)
            {
                var mead = FindMead(inv, MeadKind.Stamina);
                if (mead != null)
                {
                    if (ConsumeMead(inv, mead, MeadKind.Stamina))
                        return;
                }
                else
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] Mead check — stam={stamPct * 100f:F0}% below threshold, " +
                        $"no stamina mead in inventory companion=\"{_character?.m_name ?? "?"}\"");
                }
            }
        }

        private ItemDrop.ItemData FindMead(Inventory inv, MeadKind wanted)
        {
            List<ItemDrop.ItemData> all = inv.GetAllItemsInGridOrder();
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (!IsMeadItem(item)) continue;
                if (ClassifyMead(item) != wanted) continue;

                // Check status effect not already active
                var se = item.m_shared.m_consumeStatusEffect;
                if (se != null && _seman != null)
                {
                    if (_seman.HaveStatusEffect(se.NameHash())) continue;
                    if (_seman.HaveStatusEffectCategory(se.m_category)) continue;
                }

                return item;
            }
            return null;
        }

        private bool ConsumeMead(Inventory inv, ItemDrop.ItemData item, MeadKind kind)
        {
            if (inv == null || item == null) return false;

            string itemName = item.m_shared?.m_name ?? "?";
            string prefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : "?";

            // Apply status effect
            var se = item.m_shared.m_consumeStatusEffect;
            if (se != null && _seman != null)
                _seman.AddStatusEffect(se, true);

            // For stamina meads, manually restore CompanionStamina since
            // SE_Stats.m_staminaUpFront calls Player.AddStamina (no-op on NPCs).
            if (kind == MeadKind.Stamina && _stamina != null && se is SE_Stats stats)
            {
                if (stats.m_staminaUpFront > 0f)
                    _stamina.Restore(stats.m_staminaUpFront);
            }

            // Play eat/drink animation + sound effects
            string meadAnimTrigger = _setup != null && _setup.CanWearArmor() ? "eat" : "consume";
            if (_zanim != null) _zanim.SetTrigger(meadAnimTrigger);
            CompanionsPlugin.Log.LogDebug(
                $"[Food] Mead anim trigger=\"{meadAnimTrigger}\" " +
                $"canWearArmor={_setup?.CanWearArmor()} companion=\"{_character?.m_name}\"");
            if (_humanoid != null && _humanoid.m_consumeItemEffects != null)
                _humanoid.m_consumeItemEffects.Create(transform.position, Quaternion.identity);

            // Remove one from inventory
            inv.RemoveOneItem(item);

            float hpPct = _character.GetMaxHealth() > 0f
                ? _character.GetHealth() / _character.GetMaxHealth() * 100f
                : 0f;
            float stamPct = _stamina != null ? _stamina.GetStaminaPercentage() * 100f : 0f;

            CompanionsPlugin.Log.LogDebug(
                $"[Food] MEAD consumed \"{itemName}\" (prefab={prefabName}) kind={kind} " +
                $"hp={hpPct:F0}% stam={stamPct:F0}% " +
                $"hasSE={se != null} companion=\"{_character?.m_name ?? "?"}\"");

            _meadCooldownTimer = MeadCooldown;
            return true;
        }

        private static bool IsMeadItem(ItemDrop.ItemData item)
        {
            if (item == null || item.m_shared == null) return false;
            if (item.m_shared.m_itemType != ItemDrop.ItemData.ItemType.Consumable) return false;
            if (item.m_shared.m_consumeStatusEffect == null) return false;
            // Meads have no food stats — they work entirely through status effects.
            // Items with food stats are regular food handled by TryAutoConsume.
            return item.m_shared.m_food <= 0f
                && item.m_shared.m_foodStamina <= 0f
                && item.m_shared.m_foodEitr <= 0f;
        }

        private static MeadKind ClassifyMead(ItemDrop.ItemData item)
        {
            var se = item.m_shared?.m_consumeStatusEffect;
            if (se == null) return MeadKind.Unknown;

            // Classify via SE_Stats fields (most reliable)
            if (se is SE_Stats stats)
            {
                if (stats.m_healthOverTime > 0f || stats.m_healthOverTimeDuration > 0f)
                    return MeadKind.Health;
                if (stats.m_staminaOverTime > 0f || stats.m_staminaOverTimeDuration > 0f
                    || stats.m_staminaUpFront > 0f)
                    return MeadKind.Stamina;
            }

            // Fallback: check prefab name
            string prefab = item.m_dropPrefab != null ? item.m_dropPrefab.name : "";
            if (prefab.IndexOf("Health", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return MeadKind.Health;
            if (prefab.IndexOf("Stamina", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return MeadKind.Stamina;

            return MeadKind.Unknown;
        }

        // ── ZDO persistence ─────────────────────────────────────────────────

        // Format (v3): "itemName|hp|stam|eitr|remaining|total|prefabName|regen"
        // Legacy (v2): "itemName|hp|stam|eitr|remaining|total|prefabName"
        // Legacy (v1): "itemName|hp|stam|eitr|remaining|total"
        private string SerializeFood(int slot)
        {
            var f = _foods[slot];
            if (!f.IsActive) return "";
            return string.Join("|",
                f.ItemName,
                f.HealthBonus.ToString("F1", CultureInfo.InvariantCulture),
                f.StaminaBonus.ToString("F1", CultureInfo.InvariantCulture),
                f.EitrBonus.ToString("F1", CultureInfo.InvariantCulture),
                f.RemainingTime.ToString("F1", CultureInfo.InvariantCulture),
                f.TotalTime.ToString("F1", CultureInfo.InvariantCulture),
                f.ItemPrefabName ?? "",
                f.FoodRegen.ToString("F1", CultureInfo.InvariantCulture));
        }

        private static FoodEffect DeserializeFood(string data)
        {
            if (string.IsNullOrEmpty(data)) return default;
            var parts = data.Split('|');
            if (parts.Length < 6) return default;

            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float hp))
                return default;
            if (!float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float stam))
                return default;
            if (!float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float eitr))
                return default;
            if (!float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float remaining))
                return default;
            if (!float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float total))
                return default;

            float regen = 0f;
            if (parts.Length >= 8)
                float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out regen);

            return new FoodEffect
            {
                ItemName       = parts[0],
                ItemPrefabName = parts.Length >= 7 ? parts[6] : "",
                HealthBonus    = hp,
                StaminaBonus   = stam,
                EitrBonus      = eitr,
                FoodRegen      = regen,
                RemainingTime  = remaining,
                TotalTime      = total
            };
        }

        private void SaveToZDO()
        {
            if (_nview == null || _nview.GetZDO() == null || !_nview.IsOwner()) return;
            var zdo = _nview.GetZDO();
            for (int i = 0; i < MaxFoodSlots; i++)
                zdo.Set(FoodHashes[i], SerializeFood(i));
        }

        private void LoadFromZDO()
        {
            if (_nview == null || _nview.GetZDO() == null) return;
            var zdo = _nview.GetZDO();
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                string data = zdo.GetString(FoodHashes[i], "");
                _foods[i] = DeserializeFood(data);
                if (_foods[i].IsActive)
                {
                    CompanionsPlugin.Log.LogDebug(
                        $"[Food] ZDO load slot {i} — \"{_foods[i].ItemName}\" " +
                        $"hp={_foods[i].HealthBonus:F0} stam={_foods[i].StaminaBonus:F0} " +
                        $"remaining={_foods[i].RemainingTime:F0}/{_foods[i].TotalTime:F0}s");
                }
            }
        }
    }
}
