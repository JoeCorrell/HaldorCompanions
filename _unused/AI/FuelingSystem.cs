using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Fuels nearby smelters, kilns, and fireplaces when in Stay mode.
    /// Loads both fuel and ore into smelters.
    /// Per-instance scan buffer for multi-companion safety.
    /// </summary>
    internal class FuelingSystem
    {
        private readonly Humanoid _humanoid;
        private readonly ZNetView _nview;
        private readonly Transform _transform;

        private float _fuelTimer;
        private readonly Collider[] _scanBuffer = new Collider[96];
        private readonly HashSet<int> _seenIds = new HashSet<int>();
        private readonly int _scanMask;

        private const float CheckInterval = 2f;
        private const float FuelRange     = 3f;

        internal FuelingSystem(Humanoid humanoid, ZNetView nview, Transform transform)
        {
            _humanoid  = humanoid;
            _nview     = nview;
            _transform = transform;

            // Smelters/fireplaces are on "piece" layer
            int piece = LayerMask.NameToLayer("piece");
            int def   = LayerMask.NameToLayer("Default");
            int mask = 0;
            if (piece >= 0) mask |= (1 << piece);
            if (def   >= 0) mask |= (1 << def);
            _scanMask = mask != 0 ? mask : ~0;
        }

        internal void Update(float dt)
        {
            if (_nview == null || _humanoid == null) return;

            var zdo = _nview.GetZDO();
            if (zdo == null) return;
            int mode = zdo.GetInt(CompanionSetup.ActionModeHash, CompanionSetup.ModeFollow);
            if (mode != CompanionSetup.ModeStay) return;

            _fuelTimer -= dt;
            if (_fuelTimer > 0f) return;
            _fuelTimer = CheckInterval;

            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            _seenIds.Clear();
            float scanRange = FuelRange + 1.5f;
            int hitCount = Physics.OverlapSphereNonAlloc(
                _transform.position, scanRange, _scanBuffer, _scanMask, QueryTriggerInteraction.Ignore);
            if (hitCount > _scanBuffer.Length) hitCount = _scanBuffer.Length;

            for (int i = 0; i < hitCount; i++)
            {
                if (TryFuelFromCollider(_scanBuffer[i], inv)) return;
            }
        }

        private bool TryFuelFromCollider(Collider col, Inventory inv)
        {
            if (col == null || inv == null) return false;

            var smelter = col.GetComponentInParent<Smelter>();
            if (smelter != null)
            {
                int id = smelter.GetInstanceID();
                if (!_seenIds.Add(id)) return false;
                return TryFuelSmelter(smelter, inv);
            }

            var fireplace = col.GetComponentInParent<Fireplace>();
            if (fireplace != null)
            {
                int id = fireplace.GetInstanceID();
                if (!_seenIds.Add(id)) return false;
                return TryFuelFireplace(fireplace, inv);
            }

            return false;
        }

        private bool TryFuelSmelter(Smelter smelter, Inventory inv)
        {
            if (smelter == null || inv == null) return false;
            if (Vector3.Distance(_transform.position, smelter.transform.position) > FuelRange)
                return false;

            var smelterNview = smelter.GetComponent<ZNetView>();
            if (smelterNview == null || !smelterNview.IsValid()) return false;
            var smelterZdo = smelterNview.GetZDO();
            if (smelterZdo == null) return false;

            // Try adding fuel
            if (smelter.m_fuelItem != null &&
                smelter.m_fuelItem.m_itemData != null &&
                smelter.m_fuelItem.m_itemData.m_shared != null)
            {
                float fuel = smelterZdo.GetFloat(ZDOVars.s_fuel);
                if (fuel < smelter.m_maxFuel - 1f)
                {
                    string fuelName = smelter.m_fuelItem.m_itemData.m_shared.m_name;
                    var fuelItem = inv.GetItem(fuelName);
                    if (fuelItem != null)
                    {
                        inv.RemoveOneItem(fuelItem);
                        smelterNview.InvokeRPC("RPC_AddFuel");
                        return true;
                    }
                }
            }

            // Try adding ore
            int queued = smelterZdo.GetInt(ZDOVars.s_queued);
            if (queued >= smelter.m_maxOre || smelter.m_conversion == null) return false;

            foreach (var conversion in smelter.m_conversion)
            {
                if (conversion.m_from == null ||
                    conversion.m_from.m_itemData == null ||
                    conversion.m_from.m_itemData.m_shared == null)
                    continue;

                string oreName = conversion.m_from.m_itemData.m_shared.m_name;
                var oreItem = inv.GetItem(oreName);
                if (oreItem != null)
                {
                    inv.RemoveOneItem(oreItem);
                    smelterNview.InvokeRPC("RPC_AddOre", conversion.m_from.gameObject.name);
                    return true;
                }
            }

            return false;
        }

        private bool TryFuelFireplace(Fireplace fireplace, Inventory inv)
        {
            if (fireplace == null || inv == null || fireplace.m_fuelItem == null) return false;
            if (Vector3.Distance(_transform.position, fireplace.transform.position) > FuelRange)
                return false;

            var fpNview = fireplace.GetComponent<ZNetView>();
            if (fpNview == null || !fpNview.IsValid()) return false;
            if (fireplace.m_fuelItem.m_itemData == null ||
                fireplace.m_fuelItem.m_itemData.m_shared == null)
                return false;

            string fpFuelName = fireplace.m_fuelItem.m_itemData.m_shared.m_name;
            var fpFuelItem = inv.GetItem(fpFuelName);
            if (fpFuelItem == null) return false;

            return fireplace.Interact(_humanoid, false, false);
        }
    }
}
