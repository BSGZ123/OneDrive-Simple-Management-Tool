using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class FolderSyncStore
    {
        private const int MaximumBytes = 128 * 1024 * 1024;
        private readonly IConfigurationProtector _protector;
        public string DirectoryPath { get; }
        public bool LegacyCleanupPending { get; private set; }

        public FolderSyncStore(string directory, IConfigurationProtector protector = null)
        {
            DirectoryPath = Path.GetFullPath(directory);
            _protector = protector;
        }

        private ProtectedConfigurationFile<FolderSyncBinding> FileFor(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException();
            return new(Path.Combine(DirectoryPath, id + ".dat"), "folder-sync", FolderSyncJsonContext.Default.FolderSyncBinding,
                binding => Validate(binding, id), MaximumBytes, _protector);
        }

        private static void Validate(FolderSyncBinding binding, string id)
        {
            if (binding == null || binding.Id != id || string.IsNullOrWhiteSpace(binding.AccountId) ||
                string.IsNullOrWhiteSpace(binding.DriveId) || string.IsNullOrWhiteSpace(binding.RemoteFolderId) ||
                string.IsNullOrWhiteSpace(binding.RemotePath) || string.IsNullOrWhiteSpace(binding.LocalPath) ||
                !Path.IsPathFullyQualified(binding.LocalPath) || binding.Files == null || binding.RemoteAncestorIds == null ||
                binding.Files.Count > 1000000 || binding.Files.Any(p => p.Value == null || p.Value.Length < 0))
                throw new ConfigurationException(ConfigurationFailure.Invalid);
            FolderSyncRules.ValidateName(binding.FolderName);
            // Checkpoint entries may refer to files deleted since the previous scan. Validate syntax without touching disk.
            foreach (string relativePath in binding.Files.Keys)
                foreach (string part in relativePath.Split('/')) FolderSyncRules.ValidateName(part);
        }

        public async Task<(List<FolderSyncBinding> Bindings, int Errors)> LoadAsync(CancellationToken token)
        {
            var bindings = new List<FolderSyncBinding>();
            int errors = 0;
            LegacyCleanupPending = false;
            if (!Directory.Exists(DirectoryPath)) return (bindings, errors);
            // Include missing main files with backups/markers so data loss is reported, not mistaken for a first run.
            var ids = Directory.EnumerateFiles(DirectoryPath).Where(p => p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".dat.bak", StringComparison.OrdinalIgnoreCase) ||
                p.EndsWith(".dat.migrated", StringComparison.OrdinalIgnoreCase))
                .Select(p => Path.GetFileName(p).Split('.')[0]).Distinct(StringComparer.Ordinal).ToList();
            foreach (string id in ids)
            {
                try
                {
                    var file = FileFor(id);
                    using var lease = await ConfigurationFileLease.AcquireAsync(file.Path, token);
                    if (File.Exists(file.Path + ".removed")) continue;
                    var snapshot = await file.ReadAsync(token);
                    string legacy = Path.Combine(DirectoryPath, id + ".json");
                    if (snapshot == null)
                    {
                        byte[] bytes = await ProtectedConfigurationFile<FolderSyncBinding>.ReadBoundedAsync(legacy, MaximumBytes, token);
                        FolderSyncBinding binding;
                        try { binding = JsonSerializer.Deserialize(bytes, FolderSyncJsonContext.Default.FolderSyncBinding); }
                        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                        Validate(binding, id);
                        snapshot = await file.WriteAsync(binding, 1, false, token);
                    }
                    // Do not start a binding while its old plaintext has not been cleaned up.
                    await file.ReadAsync(CancellationToken.None);
                    if (!File.Exists(file.MigrationMarker)) await file.MarkMigratedAsync(CancellationToken.None);
                    File.Delete(legacy);
                    snapshot.Data.StorageRevision = snapshot.Revision;
                    bindings.Add(snapshot.Data);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    errors++;
                    if (File.Exists(Path.Combine(DirectoryPath, id + ".json"))) LegacyCleanupPending = true;
                }
            }
            return (bindings, errors);
        }

        public async Task SaveAsync(FolderSyncBinding binding, CancellationToken token)
        {
            try
            {
                var file = FileFor(binding.Id);
                using var lease = await ConfigurationFileLease.AcquireAsync(file.Path, token);
                if (File.Exists(file.Path + ".removed")) throw new ConfigurationException(ConfigurationFailure.Conflict);
                var current = await file.ReadAsync(token);
                if (File.Exists(Path.Combine(DirectoryPath, binding.Id + ".json")) || (current?.Revision ?? 0) != binding.StorageRevision)
                    throw new ConfigurationException(ConfigurationFailure.Conflict);
                var snapshot = await file.WriteAsync(binding, checked(binding.StorageRevision + 1), current != null, token);
                binding.StorageRevision = snapshot.Revision;
                // Do not report a successful commit as failed if optional marker maintenance fails.
                try { if (!File.Exists(file.MigrationMarker)) await file.MarkMigratedAsync(CancellationToken.None); }
                catch { }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ConfigurationException or System.Security.Cryptography.CryptographicException)
            {
                throw new FolderSyncException("Sync_ConfigError");
            }
        }

        public async Task RemoveAsync(FolderSyncBinding binding, CancellationToken token = default)
        {
            var file = FileFor(binding.Id);
            using var lease = await ConfigurationFileLease.AcquireAsync(file.Path, token);
            var current = await file.ReadAsync(token);
            if (current?.Revision != binding.StorageRevision) throw new ConfigurationException(ConfigurationFailure.Conflict);
            // Keep a protected recovery copy before removing the binding; legacy files cannot resurrect it.
            await file.ArchiveAsync(file.Path, token);
            File.Delete(Path.Combine(DirectoryPath, binding.Id + ".json"));
            await ProtectedConfigurationFile<FolderSyncBinding>.WriteBytesAtomicallyAsync(file.Path + ".removed", new byte[] { 1 }, null, token);
            // The durable tombstone is the commit point. Failure to delete encrypted remnants cannot resurrect a job.
            try { File.Delete(file.BackupPath); File.Delete(file.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public async Task RestoreUnreadableBackupsAsync(IReadOnlyCollection<string> activeIds, CancellationToken token = default)
        {
            if (!Directory.Exists(DirectoryPath)) return;
            foreach (string backup in Directory.EnumerateFiles(DirectoryPath, "*.dat.bak"))
            {
                string id = Path.GetFileName(backup).Split('.')[0];
                if (activeIds.Contains(id)) continue;
                var file = FileFor(id);
                using var lease = await ConfigurationFileLease.AcquireAsync(file.Path, token);
                if (File.Exists(file.Path + ".removed")) continue;
                try { if (await file.ReadAsync(token) != null) continue; }
                catch (ConfigurationException) { }
                var snapshot = await file.ReadAsync(token, true);
                await file.ArchiveAsync(file.Path, token);
                await file.WriteAsync(snapshot.Data, checked(snapshot.Revision + 1), false, token);
            }
        }
    }
}
