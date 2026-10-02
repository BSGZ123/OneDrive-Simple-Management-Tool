using System;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    // Each file reports an absolute byte count; aggregate only its new bytes across the entire tree.
    internal sealed class UploadProgressTracker
    {
        private readonly object _sync = new();
        private readonly IProgress<long> _progress;
        private long _uploadedBytes;

        public UploadProgressTracker(IProgress<long> progress)
        {
            _progress = progress;
        }

        public IProgress<long> CreateFileProgress() => new FileProgress(this);

        private sealed class FileProgress(UploadProgressTracker owner) : IProgress<long>
        {
            private long _uploadedBytes;

            public void Report(long value)
            {
                lock (owner._sync)
                {
                    // Retries and duplicate reports must not count the same bytes twice.
                    if (value <= _uploadedBytes)
                    {
                        return;
                    }

                    owner._uploadedBytes = checked(owner._uploadedBytes + value - _uploadedBytes);
                    _uploadedBytes = value;
                    owner._progress?.Report(owner._uploadedBytes);
                }
            }
        }
    }
}
