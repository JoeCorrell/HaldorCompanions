namespace Companions
{
    /// <summary>
    /// Tool selection, equipping for harvest mode, and restoring combat loadout.
    /// Manages the SuppressAutoEquip flag on CompanionSetup.
    /// </summary>
    internal class HarvestToolManager
    {
        private readonly Humanoid _humanoid;
        private readonly CompanionSetup _setup;

        private bool _toolWarned;

        internal HarvestToolManager(Humanoid humanoid, CompanionSetup setup)
        {
            _humanoid = humanoid;
            _setup    = setup;
        }

        internal ItemDrop.ItemData FindBestTool(ResourceType type, int minToolTier = int.MinValue)
        {
            var inv = _humanoid?.GetInventory();
            if (inv == null) return null;

            ItemDrop.ItemData best = null;
            float bestScore = float.MinValue;

            foreach (var item in inv.GetAllItems())
            {
                if (item == null || item.m_shared == null) continue;
                if (item.m_shared.m_useDurability && item.m_durability <= 0f) continue;
                if (item.m_shared.m_toolTier < minToolTier) continue;

                float relevant = ResourceClassifier.GetRelevantToolDamage(item, type);
                if (relevant <= 0f) continue;

                float score = item.m_shared.m_toolTier * 1000f +
                              relevant * 10f +
                              item.m_quality;
                if (score > bestScore)
                {
                    best = item;
                    bestScore = score;
                }
            }
            return best;
        }

        internal ItemDrop.ItemData GetEquippedTool()
        {
            if (_setup == null) return null;
            return _setup.GetEquipSlot(CompanionSetup._rightItemField);
        }

        internal void EquipForHarvest(ItemDrop.ItemData tool)
        {
            if (_setup == null || _humanoid == null || tool == null) return;
            _setup.SuppressAutoEquip = true;

            var curRight = _setup.GetEquipSlot(CompanionSetup._rightItemField);
            if (curRight != null && curRight != tool)
                _humanoid.UnequipItem(curRight, false);

            var toolType = tool.m_shared.m_itemType;
            bool is2H = toolType == ItemDrop.ItemData.ItemType.TwoHandedWeapon ||
                        toolType == ItemDrop.ItemData.ItemType.TwoHandedWeaponLeft;
            if (is2H)
            {
                var curLeft = _setup.GetEquipSlot(CompanionSetup._leftItemField);
                if (curLeft != null)
                    _humanoid.UnequipItem(curLeft, false);
            }

            if (curRight != tool) _toolWarned = false;
            if (!_humanoid.IsItemEquiped(tool))
                _humanoid.EquipItem(tool, true);
        }

        internal void RestoreCombatLoadout()
        {
            if (_setup == null) return;
            _setup.SuppressAutoEquip = false;
            _setup.SyncEquipmentToInventory();
        }

        internal void ApplyToolPreferenceForMode(int mode)
        {
            if (_setup == null) return;

            var targetType = HarvestController.ModeToResourceType(mode);
            if (targetType == ResourceType.None)
            {
                _setup.SuppressAutoEquip = false;
                _setup.SyncEquipmentToInventory();
                return;
            }

            var tool = FindBestTool(targetType);
            if (tool == null)
            {
                _setup.SuppressAutoEquip = false;
                return;
            }

            EquipForHarvest(tool);
        }

        internal bool CheckToolWarning(ItemDrop.ItemData tool)
        {
            if (tool == null) return false;
            if (!tool.m_shared.m_useDurability) return false;

            float maxDura = tool.GetMaxDurability();
            if (maxDura > 0f && tool.m_durability / maxDura < 0.1f && !_toolWarned)
            {
                _toolWarned = true;
                return true;
            }
            return false;
        }

        internal void ResetWarnings()
        {
            _toolWarned = false;
        }
    }
}
