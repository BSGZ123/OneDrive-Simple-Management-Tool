using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IReaderStore
    {
        Task<ReaderProgress> LoadProgressAsync(ReaderIdentity identity, CancellationToken token);
        Task<ReaderSettings> LoadSettingsAsync(CancellationToken token);
        Task SaveProgressAsync(ReaderProgress progress, CancellationToken token);
        Task SaveSettingsAsync(ReaderPreferences preferences, CancellationToken token);
        Task RecoverAsync(bool backup, CancellationToken token);
    }
    public sealed class ReaderStore : IReaderStore
    {
        private readonly ProtectedConfigurationFile<List<ReaderProgress>> _progress;
        private readonly ProtectedConfigurationFile<ReaderPreferences> _settings;
        public ReaderStore(ApplicationDataPaths paths, IConfigurationProtector protector = null)
        {
            _progress = new(paths.ReaderProgress, "reader-progress", ReaderJsonContext.Default.ListReaderProgress, ValidateProgress, 16 * 1024 * 1024, protector);
            _settings = new(paths.ReaderSettings, "reader-settings", ReaderJsonContext.Default.ReaderPreferences, ValidateSettings, 16384, protector);
        }
        public async Task<ReaderProgress> LoadProgressAsync(ReaderIdentity identity, CancellationToken token)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_progress.Path, token).ConfigureAwait(false);
            return (await _progress.ReadAsync(token).ConfigureAwait(false))?.Data.Find(x => x.Identity == identity);
        }
        public async Task<ReaderSettings> LoadSettingsAsync(CancellationToken token)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(_settings.Path, token).ConfigureAwait(false);
            return (await _settings.ReadAsync(token).ConfigureAwait(false))?.Data.Settings ?? new();
        }
        public async Task SaveProgressAsync(ReaderProgress progress, CancellationToken token)
        {
            ValidateProgress([progress]);
            using var lease = await ConfigurationFileLease.AcquireAsync(_progress.Path, token).ConfigureAwait(false);
            var snapshot = await _progress.ReadAsync(token).ConfigureAwait(false);
            var records = snapshot?.Data ?? new();
            int index = records.FindIndex(x => x.Identity == progress.Identity);
            if (index >= 0 && records[index].UpdatedAtUtc >= progress.UpdatedAtUtc) return;
            if (index >= 0) records[index] = progress;
            else records.Add(progress);
            await _progress.WriteAsync(records, checked((snapshot?.Revision ?? 0) + 1), snapshot != null, token).ConfigureAwait(false);
        }
        public async Task SaveSettingsAsync(ReaderPreferences preferences, CancellationToken token)
        {
            ValidateSettings(preferences);
            using var lease = await ConfigurationFileLease.AcquireAsync(_settings.Path, token).ConfigureAwait(false);
            var snapshot = await _settings.ReadAsync(token).ConfigureAwait(false);
            if (snapshot?.Data.UpdatedAtUtc >= preferences.UpdatedAtUtc) return;
            await _settings.WriteAsync(preferences, checked((snapshot?.Revision ?? 0) + 1), snapshot != null, token).ConfigureAwait(false);
        }
        public async Task RecoverAsync(bool backup, CancellationToken token)
        {
            await RecoverFileAsync(_progress, new List<ReaderProgress>(), backup, token).ConfigureAwait(false);
            await RecoverFileAsync(_settings, new ReaderPreferences(new(), DateTimeOffset.UtcNow), backup, token).ConfigureAwait(false);
        }
        private static async Task RecoverFileAsync<T>(ProtectedConfigurationFile<T> file, T empty, bool backup, CancellationToken token)
        {
            using var lease = await ConfigurationFileLease.AcquireAsync(file.Path, token).ConfigureAwait(false);
            try { await file.ReadAsync(token).ConfigureAwait(false); return; }
            catch (ConfigurationException) { }
            var restored = backup ? await file.ReadAsync(token, true).ConfigureAwait(false) : null;
            await file.ArchiveAsync(file.Path, token).ConfigureAwait(false);
            if (!backup) await file.ArchiveAsync(file.BackupPath, token).ConfigureAwait(false);
            await file.WriteAsync(restored != null ? restored.Data : empty, checked((restored?.Revision ?? 0) + 1), false, token).ConfigureAwait(false);
        }
        private static void ValidateProgress(List<ReaderProgress> records)
        {
            if (records == null || records.Count > 10000 || records.Any(x => x == null || !ReaderProtocol.ValidIdentity(x.Identity)
                || !ReaderProtocol.ValidText(x.ContentVersion, 2048) || !ReaderProtocol.ValidLocation(x.Location) || x.Location.IsEmpty || x.UpdatedAtUtc == default)
                || records.Select(x => x.Identity).Distinct().Count() != records.Count) throw new ConfigurationException(ConfigurationFailure.Invalid);
        }
        private static void ValidateSettings(ReaderPreferences preferences)
        {
            if (preferences == null || !ReaderProtocol.ValidSettings(preferences.Settings) || preferences.UpdatedAtUtc == default)
                throw new ConfigurationException(ConfigurationFailure.Invalid);
        }
    }
}
