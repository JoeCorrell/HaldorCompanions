using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Monitors inventory weight and halves walk/run speed when over capacity.
    /// Restores original speeds when weight drops below threshold.
    /// </summary>
    internal class EncumbranceSystem
    {
        internal bool IsEncumbered => _isEncumbered;

        private readonly Character _character;
        private readonly Humanoid _humanoid;
        private readonly ZSyncAnimation _zanim;

        private float _origWalkSpeed;
        private float _origRunSpeed;
        private bool  _speedsStored;
        private bool  _isEncumbered;

        internal EncumbranceSystem(Character character, Humanoid humanoid, ZSyncAnimation zanim)
        {
            _character = character;
            _humanoid  = humanoid;
            _zanim     = zanim;
        }

        internal void StoreOriginalSpeeds()
        {
            if (_speedsStored || _character == null) return;
            _origWalkSpeed = _character.m_walkSpeed;
            _origRunSpeed  = _character.m_runSpeed;
            _speedsStored  = true;
        }

        internal void Update()
        {
            if (_humanoid == null || _character == null) return;
            var inv = _humanoid.GetInventory();
            if (inv == null) return;

            bool overweight = inv.GetTotalWeight() > CompanionTierData.MaxCarryWeight;

            if (overweight && !_isEncumbered)
            {
                _isEncumbered = true;
                _character.m_walkSpeed = _origWalkSpeed * 0.5f;
                _character.m_runSpeed  = _origRunSpeed * 0.5f;
                if (_zanim != null) _zanim.SetBool("encumbered", true);
            }
            else if (!overweight && _isEncumbered)
            {
                _isEncumbered = false;
                _character.m_walkSpeed = _origWalkSpeed;
                _character.m_runSpeed  = _origRunSpeed;
                if (_zanim != null) _zanim.SetBool("encumbered", false);
            }
        }

        internal void RestoreOnDestroy()
        {
            if (_character != null && _speedsStored && _isEncumbered)
            {
                _character.m_walkSpeed = _origWalkSpeed;
                _character.m_runSpeed  = _origRunSpeed;
            }
        }
    }
}
