using System.Collections.Generic;
using UnityEngine;

namespace Companions
{
    internal enum BlacklistReason { Unreachable, Oscillation, PathFailed, ToolTier }

    /// <summary>
    /// Timed blacklist for harvest targets that couldn't be reached.
    /// Entries escalate in duration on repeated failures.
    /// </summary>
    internal class HarvestBlacklist
    {
        private struct Entry
        {
            public float ExpireTime;
            public int FailCount;
            public BlacklistReason Reason;
        }

        private readonly Dictionary<int, Entry> _entries = new Dictionary<int, Entry>();
        private readonly List<int> _removeBuffer = new List<int>();

        private const float DurationUnreachable = 15f;
        private const float DurationOscillation = 20f;
        private const float DurationPathFailed  = 10f;

        internal int Count => _entries.Count;

        internal void Add(int id, BlacklistReason reason)
        {
            float baseDuration;
            switch (reason)
            {
                case BlacklistReason.Oscillation: baseDuration = DurationOscillation; break;
                case BlacklistReason.PathFailed:  baseDuration = DurationPathFailed;  break;
                case BlacklistReason.ToolTier:    baseDuration = float.MaxValue;       break;
                default:                          baseDuration = DurationUnreachable;  break;
            }

            int failCount = 1;
            if (_entries.TryGetValue(id, out var existing))
                failCount = existing.FailCount + 1;

            float duration = failCount >= 3 ? baseDuration * 2f : baseDuration;

            _entries[id] = new Entry
            {
                ExpireTime = Time.time + duration,
                FailCount  = failCount,
                Reason     = reason
            };
        }

        internal void AddTarget(GameObject target, BlacklistReason reason)
        {
            if (target == null) return;
            Add(target.GetInstanceID(), reason);
        }

        internal bool IsBlacklisted(int id)
        {
            if (!_entries.TryGetValue(id, out var entry)) return false;
            if (Time.time > entry.ExpireTime)
            {
                _entries.Remove(id);
                return false;
            }
            return true;
        }

        internal void Clean()
        {
            _removeBuffer.Clear();
            float now = Time.time;
            foreach (var kv in _entries)
            {
                if (now > kv.Value.ExpireTime)
                    _removeBuffer.Add(kv.Key);
            }
            for (int i = 0; i < _removeBuffer.Count; i++)
                _entries.Remove(_removeBuffer[i]);
        }

        internal void ClearByReason(BlacklistReason reason)
        {
            _removeBuffer.Clear();
            foreach (var kv in _entries)
            {
                if (kv.Value.Reason == reason)
                    _removeBuffer.Add(kv.Key);
            }
            for (int i = 0; i < _removeBuffer.Count; i++)
                _entries.Remove(_removeBuffer[i]);
        }

        internal void Clear()
        {
            _entries.Clear();
        }
    }
}
