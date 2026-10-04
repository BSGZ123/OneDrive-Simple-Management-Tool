using OneDrive_Simple_Management_Tool.Models.DTO;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class DriveConfigurationStore
    {
        // Generous bounds for a list of accounts; sync manifests use a separate, much larger limit.
        public const int MaximumBytes = 4 * 1024 * 1024;
        public const int MaximumDrives = 4096;
        private readonly ProtectedConfigurationFile<List<DriveDTO>> _file;
        private readonly string _legacy;
        public bool LegacyCleanupPending { get; private set; }

        public DriveConfigurationStore(ApplicationDataPaths paths, IConfigurationProtector protector = null)
        {
            _legacy = paths.LegacyDrives;
            _file = new(paths.Drives, "drives", DriveDTOSourceGenerationContext.Default.ListDriveDTO, ValidateStructure, MaximumBytes, protector);
        }

        public async Task<ConfigurationSnapshot<List<DriveDTO>>> LoadAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            return await LoadLockedAsync(token);
        }

        private async Task<ConfigurationSnapshot<List<DriveDTO>>> LoadLockedAsync(CancellationToken token)
        {
            var snapshot = await _file.ReadAsync(token);
            if (snapshot != null)
            {
                ValidateUnique(snapshot.Data);
                // Retry cleanup after a crash between the new commit and removal of the old file.
                await CompleteMigrationAsync(token);
                return snapshot;
            }
            if (!File.Exists(_legacy)) return new(0, new List<DriveDTO>());
            var drives = await ReadLegacyAsync(token);
            ValidateUnique(drives);
            snapshot = await _file.WriteAsync(drives, 1, false, token);
            // A committed new file is authoritative even if cancellation arrives during cleanup.
            await CompleteMigrationAsync(CancellationToken.None);
            return snapshot;
        }

        private async Task<List<DriveDTO>> ReadLegacyAsync(CancellationToken token)
        {
            byte[] bytes = await ProtectedConfigurationFile<List<DriveDTO>>.ReadBoundedAsync(_legacy, MaximumBytes, token);
            try
            {
                var drives = JsonSerializer.Deserialize(bytes, DriveDTOSourceGenerationContext.Default.ListDriveDTO);
                ValidateStructure(drives);
                return drives;
            }
            catch (JsonException) { throw new ConfigurationException(ConfigurationFailure.Invalid); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }

        private async Task CompleteMigrationAsync(CancellationToken token)
        {
            LegacyCleanupPending = false;
            try
            {
                // Verify persisted encryption before deleting any plaintext source.
                if (!File.Exists(_file.MigrationMarker) || File.Exists(_legacy))
                {
                    await _file.ReadAsync(token);
                    if (!File.Exists(_file.MigrationMarker)) await _file.MarkMigratedAsync(token);
                    File.Delete(_legacy);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { LegacyCleanupPending = true; }
        }

        public async Task<ConfigurationSnapshot<List<DriveDTO>>> AddAsync(DriveDTO drive, long expectedRevision, CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            var current = await LoadLockedAsync(token);
            if (current.Revision != expectedRevision) throw new ConfigurationException(ConfigurationFailure.Conflict);
            ValidateStructure(new List<DriveDTO> { drive });
            if (current.Data.Any(d => SameIdentity(d, drive))) throw new ConfigurationException(ConfigurationFailure.Duplicate);
            var next = current.Data.Append(drive).ToList();
            ValidateUnique(next);
            var committed = await _file.WriteAsync(next, checked(current.Revision + 1), current.Revision > 0, token);
            await CompleteMigrationAsync(CancellationToken.None);
            return committed;
        }

        public async Task RestoreBackupAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            await RequireBrokenAsync(token);
            var backup = await _file.ReadAsync(token, true);
            ValidateUnique(backup.Data);
            await _file.ArchiveAsync(_file.Path, token);
            await _file.WriteAsync(backup.Data, checked(backup.Revision + 1), false, token);
            await CompleteMigrationAsync(CancellationToken.None);
        }

        // Explicit user confirmation is required by the recovery UI. Preserve encrypted recovery copies.
        public async Task RebuildAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            await RequireBrokenAsync(token);
            await _file.ArchiveAsync(_file.Path, token);
            await _file.ArchiveAsync(_legacy, token);
            await _file.WriteAsync(new List<DriveDTO>(), 1, false, token);
            await CompleteMigrationAsync(CancellationToken.None);
        }

        private async Task RequireBrokenAsync(CancellationToken token)
        {
            try
            {
                var current = await _file.ReadAsync(token);
                if (current != null) ValidateUnique(current.Data);
                else if (File.Exists(_legacy)) ValidateUnique(await ReadLegacyAsync(token));
                else if (!File.Exists(_file.BackupPath)) throw new ConfigurationException(ConfigurationFailure.Conflict);
            }
            catch (ConfigurationException exception) when (exception.Failure != ConfigurationFailure.Conflict) { return; }
            // Another process has already repaired it. Do not overwrite its work from a stale error screen.
            throw new ConfigurationException(ConfigurationFailure.Conflict);
        }

        public async Task<ConfigurationSnapshot<List<DriveDTO>>> ReadDuplicateCandidatesAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            return await ReadCandidatesLockedAsync(token);
        }

        private async Task<ConfigurationSnapshot<List<DriveDTO>>> ReadCandidatesLockedAsync(CancellationToken token)
        {
            var source = await _file.ReadAsync(token) ?? new ConfigurationSnapshot<List<DriveDTO>>(0, await ReadLegacyAsync(token));
            if (!source.Data.GroupBy(Identity).Any(g => g.Count() > 1)) throw new ConfigurationException(ConfigurationFailure.Conflict);
            return source;
        }

        public async Task RepairDuplicatesAsync(ConfigurationSnapshot<List<DriveDTO>> original, IReadOnlyCollection<int> retained,
            CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            var current = await ReadCandidatesLockedAsync(token);
            if (current.Revision != original.Revision || !current.Data.Select(FullRecord).SequenceEqual(original.Data.Select(FullRecord)))
                throw new ConfigurationException(ConfigurationFailure.Conflict);
            if (retained.Distinct().Count() != retained.Count || retained.Any(i => i < 0 || i >= current.Data.Count))
                throw new ConfigurationException(ConfigurationFailure.Invalid);
            var repaired = retained.OrderBy(i => i).Select(i => current.Data[i]).ToList();
            ValidateUnique(repaired);
            if (!current.Data.Select(Identity).ToHashSet().SetEquals(repaired.Select(Identity)))
                throw new ConfigurationException(ConfigurationFailure.Invalid);
            await _file.ArchiveAsync(_file.Path, token);
            await _file.ArchiveAsync(_legacy, token);
            await _file.WriteAsync(repaired, checked(current.Revision + 1), false, token);
            await CompleteMigrationAsync(CancellationToken.None);
        }

        private static (string, string, string) FullRecord(DriveDTO drive) =>
            (drive.DisplayName, drive.Provider.HomeAccountId, drive.Provider.DriveId);
        public static (string AccountId, string DriveId) Identity(DriveDTO drive) => (drive.Provider.HomeAccountId, drive.Provider.DriveId);
        public static bool SameIdentity(DriveDTO left, DriveDTO right) => Identity(left) == Identity(right);

        private static void ValidateStructure(List<DriveDTO> drives)
        {
            if (drives == null || drives.Count > MaximumDrives || drives.Any(d => d == null ||
                string.IsNullOrWhiteSpace(d.Provider?.HomeAccountId) || string.IsNullOrWhiteSpace(d.Provider.DriveId)))
                throw new ConfigurationException(ConfigurationFailure.Invalid);
        }

        private static void ValidateUnique(List<DriveDTO> drives)
        {
            ValidateStructure(drives);
            if (drives.Select(Identity).Distinct().Count() != drives.Count)
                throw new ConfigurationException(ConfigurationFailure.Duplicate);
        }
    }
}
