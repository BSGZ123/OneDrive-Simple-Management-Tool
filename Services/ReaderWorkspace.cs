using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    // UI-thread owner also tracks a page which is finishing a navigation-away save.
    public sealed class ReaderWorkspace
    {
        private readonly HashSet<ReaderSession> _sessions = new();
        private readonly IReaderStore _store;
        public ReaderWorkspace(ApplicationDataPaths paths) { Paths = paths; _store = new ReaderStore(paths); }
        public ApplicationDataPaths Paths { get; }
        public bool HasOpenSessions => _sessions.Any(x => x.State != Models.ReaderState.Closed);
        public ReaderSession CreateSession(Func<IReaderHost> factory)
        {
            var session = new ReaderSession(_store, factory);
            Track(session);
            return session;
        }
        private void Track(ReaderSession session)
        {
            if (!_sessions.Add(session)) return;
            var previousState = session.State;
            session.Changed += RemoveClosed;
            void RemoveClosed()
            {
                var previous = previousState;
                previousState = session.State;
                if (previous == Models.ReaderState.Closed || session.State != Models.ReaderState.Closed) return;
                _sessions.Remove(session); session.Changed -= RemoveClosed;
            }
        }
        public async Task OpenAsync(ReaderSession session, Models.ReaderOpenRequest request)
        {
            Track(session);
            await CloseOthersAsync(session);
            await session.OpenAsync(request);
        }
        public Task CloseOthersAsync(ReaderSession active) => Task.WhenAll(_sessions.Where(x => x != active).ToArray().Select(x => x.CloseAsync()));
        public Task CloseAllAsync() => Task.WhenAll(_sessions.ToArray().Select(x => x.CloseAsync()));
    }
}
