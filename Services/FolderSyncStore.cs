using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class FolderSyncStore(string directory)
    {
        public string DirectoryPath { get; } = Path.GetFullPath(directory);

        private string FilePath(string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException();
            return Path.Combine(DirectoryPath, id + ".json");
        }

        public async Task<(List<FolderSyncBinding> Bindings, int Errors)> LoadAsync(CancellationToken token)
        {
            var bindings = new List<FolderSyncBinding>();
            int errors = 0;
            if (!Directory.Exists(DirectoryPath)) return (bindings, errors);
            foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                try
                {
                    using var stream = File.OpenRead(file);
                    var binding = await JsonSerializer.DeserializeAsync(stream, FolderSyncJsonContext.Default.FolderSyncBinding, token);
                    if (binding == null || FilePath(binding.Id) != file || string.IsNullOrWhiteSpace(binding.AccountId) ||
                        string.IsNullOrWhiteSpace(binding.DriveId) || string.IsNullOrWhiteSpace(binding.RemoteFolderId) ||
                        string.IsNullOrWhiteSpace(binding.RemotePath) || !Path.IsPathFullyQualified(binding.LocalPath) ||
                        binding.Files == null || binding.RemoteAncestorIds == null)
                        throw new InvalidDataException();
                    FolderSyncRules.ValidateName(binding.FolderName);
                    bindings.Add(binding);
                }
                catch (OperationCanceledException) { throw; }
                catch { errors++; } // Preserve unreadable configurations; never replace them with empty state.
            }
            return (bindings, errors);
        }

        public async Task SaveAsync(FolderSyncBinding binding, CancellationToken token)
        {
            Directory.CreateDirectory(DirectoryPath);
            string destination = FilePath(binding.Id);
            string temporary = destination + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await JsonSerializer.SerializeAsync(stream, binding, FolderSyncJsonContext.Default.FolderSyncBinding, token);
                    await stream.FlushAsync(token);
                    stream.Flush(true);
                }
                File.Move(temporary, destination, true);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new FolderSyncException("Sync_ConfigError");
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        public void Remove(string id) => File.Delete(FilePath(id));
    }
}
