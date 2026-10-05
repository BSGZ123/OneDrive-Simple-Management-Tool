using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class ReaderLocalBook : IDisposable
    {
        private readonly FileStream _lease;
        private readonly string _path;
        private bool _disposed;
        private ReaderLocalBook(string path, FileStream lease, string hash, ReaderIdentity identity = null, string version = null)
        {
            _path = path;
            _lease = lease;
            Identity = identity ?? new("local", null, null, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))));
            ContentVersion = version ?? hash;
            ContentHash = hash;
            Length = lease.Length;
        }
        public ReaderIdentity Identity { get; }
        public string ContentVersion { get; }
        public string ContentHash { get; }
        public long Length { get; }
        public static Task<ReaderLocalBook> OpenAsync(string path, CancellationToken token) => OpenCachedAsync(path, null, null, null, token);
        public static async Task<ReaderLocalBook> OpenCachedAsync(string path, ReaderIdentity identity, string version, string expectedHash, CancellationToken token)
        {
            if (!string.Equals(Path.GetExtension(path), ".epub", StringComparison.OrdinalIgnoreCase)) throw new ReaderException("InvalidBook");
            path = Path.GetFullPath(path);
            var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            try
            {
                if (lease.Length <= 0 || lease.Length > ReaderProtocol.MaximumBookBytes) throw new ReaderException("BookLimit");
                byte[] signature = new byte[4];
                await lease.ReadExactlyAsync(signature, token).ConfigureAwait(false);
                if (signature[0] != 'P' || signature[1] != 'K' || signature[2] != 3 || signature[3] != 4) throw new ReaderException("InvalidBook");
                lease.Position = 0;
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(lease, token).ConfigureAwait(false));
                if (expectedHash != null && !hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) throw new ReaderException("InvalidBook");
                return new(path, lease, hash, identity, version);
            }
            catch { lease.Dispose(); throw; }
        }
        public Stream OpenRead()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        }
        public void Dispose() { _disposed = true; _lease.Dispose(); }
    }
}
