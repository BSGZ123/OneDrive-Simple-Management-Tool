using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace OneDrive_Simple_Management_Tool.Services
{
    public enum DiagnosticLevel { Debug, Info, Warning, Error }
    public enum DiagnosticEvent { Startup, SettingsFailure, CacheFailure, ConfigurationFailure, Authentication, Cancelled, Msal, Migration, Recovery, DiagnosticsFailure }

    // No API accepts exception messages, SDK text, URLs, account IDs, paths or arbitrary strings.
    public sealed class SafeDiagnostics
    {
#if DEBUG
        public const bool IsDebugBuild = true;
#else
        public const bool IsDebugBuild = false;
#endif
        // Before explicit application initialization, diagnostics are memory-only and touch no user files.
        public static SafeDiagnostics Current { get; private set; } = new(null);
        public static void Configure(SafeDiagnostics diagnostics) => Current = diagnostics;
        private readonly object _gate = new();
        private readonly Queue<string> _events = new();
        private readonly string _directory;
        private readonly bool _debug;
        private readonly TimeProvider _time;
        private DateTimeOffset? _expires;
        private string _file;
        private bool _fileEnabled;
        public SafeDiagnostics(string directory, bool debug = IsDebugBuild, TimeProvider time = null)
        {
            _directory = directory == null ? null : Path.Combine(directory, debug ? "Debug" : "Production");
            _debug = debug;
            _time = time ?? TimeProvider.System;
            try { if (Directory.Exists(_directory)) CleanFiles(0); }
            catch { } // Diagnosis must never become a prerequisite for application startup.
        }

        public bool IsSessionActive
        {
            get { lock (_gate) { Expire(); return _fileEnabled; } }
        }

        public void BeginSession()
        {
            lock (_gate)
            {
                _expires = _debug ? DateTimeOffset.MaxValue : _time.GetUtcNow().AddMinutes(30);
                _file = null;
                _fileEnabled = true;
            }
        }

        public void EndSession()
        {
            lock (_gate) { _fileEnabled = false; _expires = null; _file = null; }
        }

        private void Expire()
        {
            if (_expires.HasValue && _time.GetUtcNow() >= _expires.Value) EndSession();
        }

        public string GetSummary() { lock (_gate) return string.Join(Environment.NewLine, _events); }

        public void Record(DiagnosticEvent code, DiagnosticLevel level = DiagnosticLevel.Warning, int status = 0, long elapsedMilliseconds = 0, int count = 0)
        {
            lock (_gate)
            {
                Expire();
                if (level < (_debug ? DiagnosticLevel.Debug : _fileEnabled ? DiagnosticLevel.Info : DiagnosticLevel.Warning)) return;
                // Enum values are normalized, preventing even incorrectly cast values from becoming free-form text.
                if (!Enum.IsDefined(code) || !Enum.IsDefined(level)) return;
                string line = FormattableString.Invariant($"{_time.GetUtcNow():O} {level} {code} status={status} elapsedMs={Math.Max(0, elapsedMilliseconds)} count={Math.Max(0, count)}");
                _events.Enqueue(line);
                while (_events.Count > 200) _events.Dequeue();
                if (_debug) Debug.WriteLine(line);
                if (!_fileEnabled) return;
                try
                {
                    Directory.CreateDirectory(_directory);
                    byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                    CleanFiles(bytes.Length);
                    if (_file == null || !File.Exists(_file) || new FileInfo(_file).Length + bytes.Length > 1024 * 1024)
                        _file = Path.Combine(_directory, _time.GetUtcNow().ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N") + ".log");
                    using var stream = new FileStream(_file, FileMode.Append, FileAccess.Write, FileShare.Read);
                    stream.Write(bytes);
                }
                catch { EndSession(); } // No recursive logging, and no more files after a quota/I/O failure.
            }
        }

        private void CleanFiles(int incoming)
        {
            if (!Directory.Exists(_directory)) return;
            var age = _debug ? TimeSpan.FromDays(3) : TimeSpan.FromHours(24);
            long limit = (_debug ? 10L : 5L) * 1024 * 1024;
            var files = new DirectoryInfo(_directory).GetFiles("*.log").OrderBy(f => f.CreationTimeUtc).ToList();
            long total = files.Sum(f => f.Length);
            foreach (var file in files)
            {
                if (_time.GetUtcNow().UtcDateTime - file.CreationTimeUtc < age && total + incoming <= limit) continue;
                long length = file.Length;
                file.Delete();
                total -= length;
                if (file.FullName == _file) _file = null;
            }
            if (total + incoming > limit) throw new IOException();
        }
    }
}
