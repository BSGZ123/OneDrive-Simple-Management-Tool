using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    // One instance per task, shared by all files in a folder and by every retry.
    public sealed class TransferPolicy
    {
        public const int MaximumKiBPerSecond = 1024 * 1024;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _consumer = new(1, 1);
        private int _priority = 1;
        private long _rate;
        private long _transferred;
        private double _credit;
        private long _lastTick = Stopwatch.GetTimestamp();

        public int Priority
        {
            get => Volatile.Read(ref _priority);
            set
            {
                if (value is < 0 or > 2) throw new ArgumentOutOfRangeException(nameof(value));
                Volatile.Write(ref _priority, value);
            }
        }

        public long BytesPerSecond
        {
            get => Interlocked.Read(ref _rate);
            set
            {
                if (value < 0 || value > (long)MaximumKiBPerSecond * 1024) throw new ArgumentOutOfRangeException(nameof(value));
                lock (_gate)
                {
                    Interlocked.Exchange(ref _rate, value);
                    _credit = 0;
                    _lastTick = Stopwatch.GetTimestamp();
                }
            }
        }

        public long TransferredBytes => Interlocked.Read(ref _transferred);
        public void ReportBytes(int count) => Interlocked.Add(ref _transferred, count);
        public int BlockSize(int maximum)
        {
            long rate = BytesPerSecond;
            return (int)Math.Min(maximum, rate == 0 ? 16384 : Math.Clamp(rate / 10, 1, 16384));
        }

        public async Task WaitAsync(int bytes, CancellationToken token)
        {
            if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            token.ThrowIfCancellationRequested();
            if (bytes == 0 || BytesPerSecond == 0) return;
            await _consumer.WaitAsync(token).ConfigureAwait(false);
            try
            {
                while (bytes > 0)
                {
                    double delay;
                    lock (_gate)
                    {
                        long rate = _rate;
                        if (rate == 0) return;
                        long now = Stopwatch.GetTimestamp();
                        _credit = Math.Min(Math.Max(1, Math.Min(16384, rate / 10.0)),
                            _credit + Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds * rate);
                        _lastTick = now;
                        int taken = (int)Math.Min(bytes, Math.Floor(_credit));
                        bytes -= taken;
                        _credit -= taken;
                        delay = Math.Clamp(bytes * 1000.0 / rate, 1, 100);
                    }
                    // Short cancellable waits also make live limit changes take effect promptly.
                    if (bytes > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), token).ConfigureAwait(false);
                }
            }
            finally { _consumer.Release(); }
        }
    }
}
