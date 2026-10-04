using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IBookmarkStore
    {
        Task<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken token = default);
        Task AddAsync(Bookmark bookmark, CancellationToken token = default);
        Task RemoveAsync(Bookmark bookmark, CancellationToken token = default);
        Task UpdateMetadataAsync(Bookmark bookmark, CancellationToken token = default);
        Task RestoreBackupAsync(CancellationToken token = default);
        Task RebuildAsync(CancellationToken token = default);
    }

    public sealed class BookmarkStore : IBookmarkStore
    {
        private enum Change { Add, Remove, UpdateMetadata }
        public const int MaximumBookmarks = 10000;
        private readonly ProtectedConfigurationFile<List<Bookmark>> _file;

        public BookmarkStore(ApplicationDataPaths paths, IConfigurationProtector protector = null)
        {
            _file = new(paths.Bookmarks, "bookmarks", BookmarkJsonContext.Default.ListBookmark, Validate, 8 * 1024 * 1024, protector);
        }

        public async Task<IReadOnlyList<Bookmark>> LoadAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            return (await _file.ReadAsync(token))?.Data ?? new List<Bookmark>();
        }

        public Task AddAsync(Bookmark bookmark, CancellationToken token = default) => ChangeAsync(bookmark, Change.Add, token);
        public Task RemoveAsync(Bookmark bookmark, CancellationToken token = default) => ChangeAsync(bookmark, Change.Remove, token);
        public Task UpdateMetadataAsync(Bookmark bookmark, CancellationToken token = default) => ChangeAsync(bookmark, Change.UpdateMetadata, token);

        private async Task ChangeAsync(Bookmark bookmark, Change operation, CancellationToken token)
        {
            Validate(new List<Bookmark> { bookmark });
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            var snapshot = await _file.ReadAsync(token);
            var records = snapshot?.Data ?? new List<Bookmark>();
            int index = records.FindIndex(item => item.Identity == bookmark.Identity);
            if (operation == Change.Add)
            {
                if (index >= 0) return; // Repeated add never turns into removal.
                if (records.Count >= MaximumBookmarks) throw new BookmarkException("Bookmarks_Limit");
                records.Add(bookmark);
            }
            else
            {
                if (index < 0) return; // A late metadata refresh must not recreate a removed bookmark.
                if (operation == Change.Remove) records.RemoveAt(index);
                else
                {
                    var updated = bookmark with { AddedAt = records[index].AddedAt };
                    if (updated == records[index]) return;
                    records[index] = updated;
                }
            }
            await _file.WriteAsync(records, checked((snapshot?.Revision ?? 0) + 1), snapshot != null, token);
        }

        public async Task RestoreBackupAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            await RequireBrokenAsync(token);
            var backup = await _file.ReadAsync(token, true);
            await _file.ArchiveAsync(_file.Path, token);
            await _file.WriteAsync(backup.Data, checked(backup.Revision + 1), false, token);
        }

        public async Task RebuildAsync(CancellationToken token = default)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_file.Path, token);
            await RequireBrokenAsync(token);
            await _file.ArchiveAsync(_file.Path, token);
            await _file.ArchiveAsync(_file.BackupPath, token);
            await _file.WriteAsync(new List<Bookmark>(), 1, false, token);
        }

        private async Task RequireBrokenAsync(CancellationToken token)
        {
            try { await _file.ReadAsync(token); }
            catch (ConfigurationException) { return; }
            throw new ConfigurationException(ConfigurationFailure.Conflict);
        }

        private static void Validate(List<Bookmark> records)
        {
            if (records == null || records.Count > MaximumBookmarks || records.Any(item => item == null ||
                Invalid(item.AccountId, 2048) || Invalid(item.DriveId, 2048) || Invalid(item.ItemId, 2048) ||
                Invalid(item.Name, 4096) || Invalid(item.DriveName, 4096) || item.AddedAt == default) ||
                records.Select(item => item.Identity).Distinct().Count() != records.Count)
                throw new ConfigurationException(ConfigurationFailure.Invalid);
        }

        private static bool Invalid(string value, int limit) => string.IsNullOrWhiteSpace(value) || value.Length > limit;
    }
}
