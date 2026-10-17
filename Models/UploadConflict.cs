using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum UploadConflictChoice
    {
        Cancel, Replace, Skip, KeepBoth
    }

    public sealed record UploadConflict(string Name, bool IsFolder);
    public sealed record UploadConflictDecision(UploadConflictChoice Choice, bool ApplyToBatch = false);
    public sealed record UploadOutcome(int Uploaded, int Skipped);
    public sealed class UploadAbortedException : OperationCanceledException
    {
    }

    // One instance per drop. Serialize decisions and remember only explicit batch choices.
    public sealed class UploadConflictResolver
    {
        private readonly Func<UploadConflict, CancellationToken, Task<UploadConflictDecision>> _prompt;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private UploadConflictChoice? _batchChoice;

        public UploadConflictResolver(Func<UploadConflict, CancellationToken, Task<UploadConflictDecision>> prompt)
        {
            _prompt = prompt;
        }

        public async Task<UploadConflictChoice> ResolveAsync(UploadConflict conflict, CancellationToken token)
        {
            await _gate.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                if (_batchChoice.HasValue) return _batchChoice.Value;
                var decision = await _prompt(conflict, token);
                token.ThrowIfCancellationRequested();
                if (decision.ApplyToBatch && decision.Choice != UploadConflictChoice.Cancel)
                    _batchChoice = decision.Choice;
                return decision.Choice;
            }
            finally { _gate.Release(); }
        }
    }
}
