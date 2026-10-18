using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    // Only leaf transfers take a slot. Priority changes apply when the next slot is freed.
    public sealed class TransferScheduler
    {
        public static TransferScheduler Shared { get; } = new(3);
        private readonly object _gate = new();
        private readonly List<Waiter> _waiting = new();
        private readonly int _concurrency;
        private readonly Func<long> _now;
        private int _running;
        public TransferScheduler(int concurrency) : this(concurrency, () => Environment.TickCount64) { }

        internal TransferScheduler(int concurrency, Func<long> now)
        {
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
            _concurrency = concurrency;
            _now = now;
        }

        public async Task<IDisposable> EnterAsync(CancellationToken token, TransferPolicy policy = null)
        {
            token.ThrowIfCancellationRequested();
            var waiter = new Waiter(policy, _now());
            lock (_gate) { _waiting.Add(waiter); Pump(); }
            using var registration = token.Register(() =>
            {
                lock (_gate)
                    if (_waiting.Remove(waiter)) waiter.Ready.TrySetCanceled(token);
            });
            var lease = await waiter.Ready.Task.ConfigureAwait(false);
            if (token.IsCancellationRequested) { lease.Dispose(); token.ThrowIfCancellationRequested(); }
            return lease;
        }

        private void Pump()
        {
            while (_running < _concurrency && _waiting.Count > 0)
            {
                long now = _now();
                // Aging prevents a continuous stream of high-priority files starving older tasks.
                var next = _waiting.OrderByDescending(x => Math.Min(2,
                    (x.Policy?.Priority ?? 1) + (now - x.QueuedAt) / 30000)).First();
                _waiting.Remove(next);
                _running++;
                next.Ready.SetResult(new Lease(this));
            }
        }

        private void Release() { lock (_gate) { _running--; Pump(); } }

        private sealed class Waiter(TransferPolicy policy, long queuedAt)
        {
            public TransferPolicy Policy { get; } = policy;
            public long QueuedAt { get; } = queuedAt;
            public TaskCompletionSource<IDisposable> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class Lease(TransferScheduler owner) : IDisposable
        {
            private TransferScheduler _owner = owner;
            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
        }
    }
}
