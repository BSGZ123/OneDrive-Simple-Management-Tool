using System;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public enum ConfigurationFailure { Invalid, Version, Protection, Conflict, Unavailable, Missing, Duplicate, LegacyCleanup }

    public sealed class ConfigurationException(ConfigurationFailure failure) : Exception(failure.ToString())
    {
        public ConfigurationFailure Failure { get; } = failure;
    }

    public interface IConfigurationProtector
    {
        byte[] Protect(byte[] data, string purpose);
        byte[] Unprotect(byte[] data, string purpose);
    }

    public sealed class WindowsConfigurationProtector : IConfigurationProtector
    {
        public byte[] Protect(byte[] data, string purpose) =>
            ProtectedData.Protect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
        public byte[] Unprotect(byte[] data, string purpose) =>
            ProtectedData.Unprotect(data, Encoding.UTF8.GetBytes(purpose), DataProtectionScope.CurrentUser);
    }

    internal sealed class ProtectedEnvelope
    {
        public string Format { get; set; } = "OneDriveConfiguration";
        public int Version { get; set; } = 1;
        public string Protection { get; set; } = "DPAPI-CurrentUser";
        public byte[] Payload { get; set; }
    }

    internal sealed class ProtectedPayload
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public JsonElement Data { get; set; }
    }

    [JsonSerializable(typeof(ProtectedEnvelope))]
    [JsonSerializable(typeof(ProtectedPayload))]
    internal partial class ProtectedConfigurationJsonContext : JsonSerializerContext { }

    public sealed record ConfigurationSnapshot<T>(long Revision, T Data);

    // The same lock protocol is used by migration, ordinary writes and explicit recovery.
    public sealed class ConfigurationFileLease : IDisposable
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _gate;
        private readonly FileStream _stream;
        private ConfigurationFileLease(SemaphoreSlim gate, FileStream stream) { _gate = gate; _stream = stream; }

        public static async Task<ConfigurationFileLease> AcquireAsync(string path, CancellationToken token)
        {
            path = Path.GetFullPath(path);
            var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            bool entered = false;
            try
            {
                await gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                entered = true;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                while (true)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    try { return new ConfigurationFileLease(gate, new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                    catch (IOException) { await Task.Delay(100, timeout.Token).ConfigureAwait(false); }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                if (entered) gate.Release();
                throw new ConfigurationException(ConfigurationFailure.Unavailable);
            }
            catch
            {
                if (entered) gate.Release();
                throw;
            }
        }

        public void Dispose() { _stream.Dispose(); _gate.Release(); }
    }

    public sealed class ProtectedConfigurationFile<T>
    {
        private readonly IConfigurationProtector _protector;
        private readonly string _purpose;
        private readonly JsonTypeInfo<T> _type;
        private readonly Action<T> _validate;
        private readonly int _maximumBytes;
        public string Path { get; }
        public string BackupPath => Path + ".bak";
        public string MigrationMarker => Path + ".migrated";

        public ProtectedConfigurationFile(string path, string purpose, JsonTypeInfo<T> type, Action<T> validate,
            int maximumBytes, IConfigurationProtector protector = null)
        {
            Path = System.IO.Path.GetFullPath(path);
            _purpose = "OneDriveSimpleManagementTool/" + purpose + "/v1";
            _type = type;
            _validate = validate;
            _maximumBytes = maximumBytes;
            _protector = protector ?? new WindowsConfigurationProtector();
        }

        public async Task<ConfigurationSnapshot<T>> ReadAsync(CancellationToken token, bool backup = false)
        {
            string path = backup ? BackupPath : Path;
            if (!File.Exists(path))
            {
                if (backup || File.Exists(MigrationMarker) || File.Exists(BackupPath))
                    throw new ConfigurationException(ConfigurationFailure.Missing);
                return null;
            }
            byte[] plaintext = null;
            try
            {
                byte[] bytes = await ReadBoundedAsync(path, _maximumBytes, token).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                Require(document.RootElement, "Format", "Version", "Protection", "Payload");
                var envelope = JsonSerializer.Deserialize(bytes, ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
                if (envelope.Format != "OneDriveConfiguration" || envelope.Version != 1 || envelope.Protection != "DPAPI-CurrentUser")
                    throw new ConfigurationException(ConfigurationFailure.Version);
                plaintext = _protector.Unprotect(envelope.Payload, _purpose);
                using var inner = JsonDocument.Parse(plaintext);
                Require(inner.RootElement, "SchemaVersion", "Revision", "Data");
                var payload = JsonSerializer.Deserialize(plaintext, ProtectedConfigurationJsonContext.Default.ProtectedPayload);
                if (payload.SchemaVersion != 1) throw new ConfigurationException(ConfigurationFailure.Version);
                if (payload.Revision < 1) throw new ConfigurationException(ConfigurationFailure.Invalid);
                T data = payload.Data.Deserialize(_type);
                _validate(data);
                return new(payload.Revision, data);
            }
            catch (JsonException) { throw new ConfigurationException(ConfigurationFailure.Invalid); }
            catch (CryptographicException) { throw new ConfigurationException(ConfigurationFailure.Protection); }
            finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
        }

        private static void Require(JsonElement element, params string[] fields)
        {
            if (element.ValueKind != JsonValueKind.Object) throw new ConfigurationException(ConfigurationFailure.Invalid);
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name)) throw new ConfigurationException(ConfigurationFailure.Invalid);
            foreach (string field in fields)
                if (!names.Contains(field)) throw new ConfigurationException(ConfigurationFailure.Invalid);
        }

        public static async Task<byte[]> ReadBoundedAsync(string path, int maximumBytes, CancellationToken token)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            if (stream.Length > maximumBytes) throw new ConfigurationException(ConfigurationFailure.Invalid);
            byte[] bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            return bytes;
        }

        // Caller holds the lease and has checked the current revision. Never checks cancellation after replacement.
        public async Task<ConfigurationSnapshot<T>> WriteAsync(T data, long revision, bool preservePrevious, CancellationToken token)
        {
            _validate(data);
            if (revision < 1) throw new ConfigurationException(ConfigurationFailure.Invalid);
            byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new ProtectedPayload
            {
                Revision = revision, Data = JsonSerializer.SerializeToElement(data, _type)
            }, ProtectedConfigurationJsonContext.Default.ProtectedPayload);
            byte[] bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(new ProtectedEnvelope { Payload = _protector.Protect(plaintext, _purpose) },
                    ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
            }
            catch (CryptographicException) { throw new ConfigurationException(ConfigurationFailure.Protection); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            if (bytes.Length > _maximumBytes) throw new ConfigurationException(ConfigurationFailure.Invalid);
            await WriteBytesAtomicallyAsync(Path, bytes, preservePrevious ? BackupPath : null, token).ConfigureAwait(false);
            return new(revision, data);
        }

        public static async Task WriteBytesAtomicallyAsync(string destination, byte[] bytes, string backup, CancellationToken token)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination));
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                    await stream.FlushAsync(token).ConfigureAwait(false);
                    stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(temporary, destination, backup);
                else File.Move(temporary, destination);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public async Task ArchiveAsync(string source, CancellationToken token)
        {
            if (!File.Exists(source)) return;
            byte[] data = await ReadBoundedAsync(source, _maximumBytes, token).ConfigureAwait(false);
            try
            {
                byte[] archive = JsonSerializer.SerializeToUtf8Bytes(new ProtectedEnvelope { Payload = _protector.Protect(data, _purpose + "/recovery") },
                    ProtectedConfigurationJsonContext.Default.ProtectedEnvelope);
                await WriteBytesAtomicallyAsync(Path + ".recovery-" + Guid.NewGuid().ToString("N"), archive, null, token).ConfigureAwait(false);
            }
            finally { CryptographicOperations.ZeroMemory(data); }
        }

        // Contains no account information. A missing new file after migration is damage, not a first run.
        public Task MarkMigratedAsync(CancellationToken token) => WriteBytesAtomicallyAsync(MigrationMarker,
            Encoding.UTF8.GetBytes("{\"Version\":1,\"State\":\"Protected\"}"), null, token);
    }
}
