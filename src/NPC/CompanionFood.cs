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

        private ZNetView       _nview;
        private Humanoid       _humanoid;
        private Character      _character;
        private SEMan          _seman;
        private ZSyncAnimation _zanim;
        private bool           _initialized;

        private float _saveTimer;
        private float _consumeTimer;
        private float _remoteSyncTimer;
        private float _foodRegenTimer;

        private const float SaveInterval         = 5f;
        private const float ConsumeCheckInterval = 1f;
        private const float RemoteSyncInterval   = 0.5f;
        private const float FoodRegenInterval    = 10f;

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
                    CompanionsPlugin.Log.LogInfo(
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
                    CompanionsPlugin.Log.LogInfo(
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
                _character.SetHealth(maxHp);
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
                if (!CanConsumeFoodItem(item)) continue;
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
                    if (CanConsumeFoodItem(item) && ConsumeIntoSlot(inv, item, refreshSlot))
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
                ItemPrefabName = item.m_dropPrefab != null ? item.m_dropPrefab.name : "",
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

            if (_zanim != null) _zanim.SetTrigger("eat");

            // Heal up to new max health.
            float newMax = BaseHealth + TotalHealthBonus;
            float curHp  = _character.GetHealth();
            if (curHp < newMax)
                _character.SetHealth(Mathf.Min(curHp + _foods[slot].HealthBonus, newMax));

            float hpAfter = _character.GetHealth();
            bool hasSE = item.m_shared.m_consumeStatusEffect != null;
            CompanionsPlugin.Log.LogInfo(
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
            if (!CanConsumeFoodItem(item)) return false;

            int slot = FindEmptyFoodSlot();
            if (slot < 0)
            {
                slot = FindRefreshableSlotForFood(item.m_shared?.m_name);
                if (slot < 0)
                    slot = FindMostDepletedRefreshableSlot();
            }
            if (slot < 0) return false;
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

        private bool CanConsumeFoodItem(ItemDrop.ItemData item)
        {
            if (!IsFoodItem(item)) return false;
            if (_humanoid != null && !_humanoid.CanConsumeItem(item, checkWorldLevel: false))
                return false;
            if (IsFoodAlreadyActive(item.m_shared.m_name)) return false;
            if (!CanApplyConsumeStatus(item)) return false;
            return true;
        }

        private bool IsFoodAlreadyActive(string itemName)
        {
            if (string.IsNullOrEmpty(itemName)) return false;
            for (int i = 0; i < MaxFoodSlots; i++)
            {
                if (!_foods[i].IsActive) continue;
                if (_foods[i].ItemName != itemName) continue;
                if (!CanEatAgain(i)) return true;
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
            // Preferred behavior: consume from the dedicated food row first.
            var slotted = inv.GetItemAt(slot, 0);
            if (CanConsumeFoodItem(slotted)) return slotted;

            // Fallback for reliability: consume valid food from any inventory slot.
            List<ItemDrop.ItemData> all = inv.GetAllItemsInGridOrder();
            for (int i = 0; i < all.Count; i++)
            {
                var item = all[i];
                if (CanConsumeFoodItem(item)) return item;
            }

            return null;
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
            }
        }
    }
}
