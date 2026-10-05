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
        private ReaderLocalBook(string path, FileStream lease, string hash)
        {
            _path = path;
            _lease = lease;
            Identity = new("local", null, null, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))));
            ContentVersion = hash;
            Length = lease.Length;
        }
        public ReaderIdentity Identity { get; }
        public string ContentVersion { get; }
        public long Length { get; }
        public static async Task<ReaderLocalBook> OpenAsync(string path, CancellationToken token)
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
                return new(path, lease, hash);
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
