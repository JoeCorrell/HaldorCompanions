using UnityEngine;

namespace Companions
{
    /// <summary>
    /// Sliding-window position tracker. Records position samples at fixed intervals
    /// and provides movement analysis (distance, speed, oscillation detection).
    /// Used by follow stuck detection, harvest stuck detection, and combat systems.
    /// Ring buffer of 20 samples at 0.25s intervals = 5s window.
    /// </summary>
    internal class PositionTracker
    {
        private struct Sample
        {
            public Vector3 Position;
            public float Time;
        }

        private readonly Sample[] _buffer;
        private int _head;
        private int _count;
        private float _sampleTimer;
        private const float SampleRate = 0.25f;

        internal PositionTracker(int capacity = 20)
        {
            _buffer = new Sample[capacity];
        }

        /// <summary>Call from Update() to record samples at SampleRate intervals.</summary>
        internal void Update(Vector3 position, float time, float dt)
        {
            _sampleTimer -= dt;
            if (_sampleTimer > 0f) return;
            _sampleTimer = SampleRate;
            Record(position, time);
        }

        internal void Record(Vector3 position, float time)
        {
            _buffer[_head] = new Sample { Position = position, Time = time };
            _head = (_head + 1) % _buffer.Length;
            if (_count < _buffer.Length) _count++;
        }

        /// <summary>Total distance traveled in the last N seconds.</summary>
        internal float DistanceOverWindow(float seconds)
        {
            if (_count < 2) return 0f;
            float now = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length].Time;
            float cutoff = now - seconds;

            float total = 0f;
            int prevIdx = -1;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                if (_buffer[idx].Time < cutoff) continue;
                if (prevIdx >= 0)
                    total += Vector3.Distance(_buffer[prevIdx].Position, _buffer[idx].Position);
                prevIdx = idx;
            }
            return total;
        }

        /// <summary>
        /// Mean velocity over the last N seconds.
        /// Uses actual elapsed time from buffer samples to avoid near-zero division.
        /// </summary>
        internal float AverageSpeed(float seconds)
        {
            if (_count < 2) return 0f;

            float now = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length].Time;
            float cutoff = now - seconds;

            // Find the actual oldest sample within the window
            float oldestTime = now;
            float total = 0f;
            int prevIdx = -1;
            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                if (_buffer[idx].Time < cutoff) continue;

                if (prevIdx < 0)
                    oldestTime = _buffer[idx].Time;
                else
                    total += Vector3.Distance(_buffer[prevIdx].Position, _buffer[idx].Position);
                prevIdx = idx;
            }

            float elapsed = now - oldestTime;
            if (elapsed < 0.01f) return 0f;
            return total / elapsed;
        }

        /// <summary>
        /// True if the companion has revisited the same area (within radius)
        /// 3+ times within the window, indicating oscillation.
        /// </summary>
        internal bool IsOscillating(float radius, float window)
        {
            if (_count < 6) return false;
            float now = _buffer[(_head - 1 + _buffer.Length) % _buffer.Length].Time;
            float cutoff = now - window;

            float radiusSq = radius * radius;
            int windowStart = -1;
            int windowCount = 0;

            for (int i = 0; i < _count; i++)
            {
                int idx = (_head - _count + i + _buffer.Length) % _buffer.Length;
                if (_buffer[idx].Time >= cutoff)
                {
                    if (windowStart < 0) windowStart = i;
                    windowCount++;
                }
            }

            if (windowCount < 6) return false;

            for (int i = windowStart; i < windowStart + windowCount; i++)
            {
                int idxI = (_head - _count + i + _buffer.Length) % _buffer.Length;
                int revisits = 0;
                for (int j = windowStart; j < windowStart + windowCount; j++)
                {
                    if (i == j) continue;
                    int idxJ = (_head - _count + j + _buffer.Length) % _buffer.Length;
                    float dx = _buffer[idxI].Position.x - _buffer[idxJ].Position.x;
                    float dz = _buffer[idxI].Position.z - _buffer[idxJ].Position.z;
                    if (dx * dx + dz * dz < radiusSq)
                        revisits++;
                }
                if (revisits >= 3) return true;
            }
            return false;
        }

        /// <summary>Clear all samples. Call on teleport or mode change.</summary>
        internal void Reset()
        {
            _count = 0;
            _head = 0;
            _sampleTimer = 0f;
        }
    }
}
