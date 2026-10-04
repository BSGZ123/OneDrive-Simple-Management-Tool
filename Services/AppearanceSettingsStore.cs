using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IAppearanceSettingsStore
    {
        Task<AppearancePreferences> LoadAsync();
        Task SaveAsync(AppearancePreferences preferences);
    }

    // Appearance contains no account information. Commit a complete JSON file atomically.
    public sealed class AppearanceSettingsStore : IAppearanceSettingsStore
    {
        private readonly string _path;
        private readonly SemaphoreSlim _writes = new(1, 1);

        public AppearanceSettingsStore(ApplicationDataPaths paths) => _path = paths.Appearance;

        public async Task<AppearancePreferences> LoadAsync()
        {
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length > 16384) throw new InvalidDataException();
                var preferences = await JsonSerializer.DeserializeAsync(stream, AppearanceJsonContext.Default.AppearancePreferences);
                Validate(preferences);
                return preferences;
            }
            catch (FileNotFoundException) { return new(); }
            catch (DirectoryNotFoundException) { return new(); }
        }

        public async Task SaveAsync(AppearancePreferences preferences)
        {
            Validate(preferences);
            await _writes.WaitAsync();
            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await JsonSerializer.SerializeAsync(stream, preferences, AppearanceJsonContext.Default.AppearancePreferences);
                    stream.Flush(true);
                }
                File.Move(temporary, _path, true);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                _writes.Release();
            }
        }

        private static void Validate(AppearancePreferences preferences)
        {
            if (preferences == null || preferences.Version != 1 ||
                !Enum.IsDefined(preferences.Theme) || !Enum.IsDefined(preferences.Material))
                throw new InvalidDataException();
        }
    }

    [JsonSerializable(typeof(AppearancePreferences))]
    internal partial class AppearanceJsonContext : JsonSerializerContext { }
}
