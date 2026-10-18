using System;
using System.Collections.Generic;

namespace OneDrive_Simple_Management_Tool.Services
{
    // Samples actual transferred bytes, excluding restored offsets and local hashing/scanning.
    public sealed class TransferProgressEstimator
    {
        private readonly Queue<(double Time, long Bytes)> _samples = new();
        private long _lastBytes;
        public void Reset() => _samples.Clear();

        public (long Speed, double? Seconds) Sample(double now, long bytes, long remaining, bool totalKnown)
        {
            if (_samples.Count > 0 && (bytes < _lastBytes || now < _samples.Peek().Time)) Reset();
            _lastBytes = bytes;
            _samples.Enqueue((now, bytes));
            while (_samples.Count > 1 && _samples.Peek().Time < now - 5) _samples.Dequeue();
            var first = _samples.Peek();
            double elapsed = now - first.Time;
            long speed = elapsed < 0.5 ? 0 : (long)Math.Max(0, (bytes - first.Bytes) / elapsed);
            return (speed, totalKnown && remaining > 0 && speed > 0 ? remaining / (double)speed : null);
        }
    }
}
