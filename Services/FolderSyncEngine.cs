using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public interface IFolderSyncBrowser : IDisposable
    {
        Task<FolderSyncRemoteFolder> GetRootAsync(CancellationToken token);
        Task<IReadOnlyList<FolderSyncRemoteFolder>> BrowseAsync(string folderId, CancellationToken token);
    }

    public interface IFolderSyncTarget : IDisposable
    {
        Task ValidateRootAsync(CancellationToken token);
        Task EnsureFolderAsync(string relativePath, CancellationToken token);
        Task UploadAsync(string relativePath, Stream content, IProgress<long> progress, CancellationToken token);
    }

    public static class FolderSyncRules
    {
        public static void ValidateName(string name)
        {
            string stem = name?.Split('.')[0];
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith('.') || name is "." or ".." ||
                name.Any(c => char.IsControl(c) || "\"*:<>?/\\|".Contains(c)) ||
                new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new FolderSyncException("Sync_InvalidName");
        }

        public static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        public static bool Overlaps(string first, string second)
        {
            first = NormalizeRoot(first);
            second = NormalizeRoot(second);
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase) ||
                first.StartsWith(second + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                second.StartsWith(first + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public static void CheckLocalRoot(FolderSyncBinding binding)
        {
            if (!Directory.Exists(binding.LocalPath)) throw new FolderSyncException("Sync_RootMissing");
            if (new DirectoryInfo(binding.LocalPath).Name != binding.FolderName)
                throw new FolderSyncException("Sync_NameMismatch");
            CheckLinks(binding.LocalPath);
        }

        public static void CheckLinks(string path)
        {
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new FolderSyncException("Sync_LinkUnsupported");
        }

        public static string Resolve(FolderSyncBinding binding, string relativePath)
        {
            CheckLocalRoot(binding);
            foreach (string part in relativePath.Split('/')) ValidateName(part);
            string root = NormalizeRoot(binding.LocalPath);
            string full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new FolderSyncException("Sync_InvalidName");
            CheckLinks(full);
            return full;
        }
    }

    public sealed class FolderSyncEngine
    {
        public async Task<FolderSyncResult> RunAsync(FolderSyncBinding binding, IFolderSyncTarget target,
            Func<CancellationToken, Task> checkpoint, IProgress<FolderSyncProgress> progress, CancellationToken token)
        {
            FolderSyncRules.CheckLocalRoot(binding);
            await target.ValidateRootAsync(token);
            var result = new FolderSyncResult();
            var items = new List<FolderSyncItem>();
            progress?.Report(new("Sync_Scanning", 0, 0, ""));
            Scan(binding, "", items, result.Issues, token);

            // Only a complete enumeration proves absence. No remote deletion is ever issued.
            if (result.Issues.Count == 0)
            {
                var present = items.Select(i => i.Path).ToHashSet(StringComparer.Ordinal);
                foreach (string oldPath in binding.Files.Keys.Where(p => !present.Contains(p)).ToList())
                    binding.Files.Remove(oldPath);
            }
            int completed = 0;
            foreach (var item in items)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new("Sync_Uploading", completed, items.Count, item.Path));
                try
                {
                    string full = FolderSyncRules.Resolve(binding, item.Path);
                    if (item.IsFolder)
                    {
                        if (!binding.Files.TryGetValue(item.Path, out var old) || !old.IsFolder)
                        {
                            await target.EnsureFolderAsync(item.Path, token);
                            binding.Files[item.Path] = new(true, null, 0);
                            await checkpoint(token);
                            result.Uploaded++;
                        }
                    }
                    else
                    {
                        // Deny writers/deletion while reading and sending so each committed stamp describes
                        // exactly the bytes uploaded. A locked/growing file is retried on the next pass.
                        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                        var stamp = new FolderSyncStamp(false, hash, stream.Length);
                        if (!binding.Files.TryGetValue(item.Path, out var old) || old != stamp)
                        {
                            stream.Position = 0;
                            int done = completed;
                            long length = stream.Length;
                            var bytes = new InlineProgress<long>(value => progress?.Report(new("Sync_Uploading", done,
                                items.Count, item.Path, length == 0 ? 0 : (int)Math.Clamp(value * 100d / length, 0, 99))));
                            await target.UploadAsync(item.Path, stream, bytes, token);
                            binding.Files[item.Path] = stamp;
                            await checkpoint(token);
                            result.Uploaded++;
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (FileNotFoundException) { binding.Files.Remove(item.Path); }
                catch (DirectoryNotFoundException) { binding.Files.Remove(item.Path); }
                catch (Exception exception)
                {
                    if (exception is FolderSyncException { Message: "Sync_LoginNeeded" or "Sync_Quota" }) throw;
                    result.Issues.Add(new(item.Path, ErrorKey(exception)));
                }
                completed++;
            }
            if (result.Issues.Count == 0) binding.LastSuccess = DateTimeOffset.Now;
            await checkpoint(token);
            progress?.Report(new(result.Issues.Count == 0 ? "Sync_Monitoring" : "Sync_Attention", completed, items.Count, "", 100));
            return result;
        }

        private static void Scan(FolderSyncBinding binding, string relative, List<FolderSyncItem> items,
            List<FolderSyncIssue> issues, CancellationToken token)
        {
            string full = relative.Length == 0 ? binding.LocalPath : FolderSyncRules.Resolve(binding, relative);
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(full))
                {
                    token.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(path);
                    string child = relative.Length == 0 ? name : relative + "/" + name;
                    try
                    {
                        FolderSyncRules.ValidateName(name);
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new FolderSyncException("Sync_LinkUnsupported");
                        bool folder = (attributes & FileAttributes.Directory) != 0;
                        items.Add(new(child, folder));
                        if (folder) Scan(binding, child, items, issues, token);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception) { issues.Add(new(child, ErrorKey(exception))); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { issues.Add(new(relative, ErrorKey(exception))); }
        }

        public static string ErrorKey(Exception exception) => exception switch
        {
            FolderSyncException => exception.Message,
            UnauthorizedAccessException => "Sync_AccessDenied",
            IOException => "Sync_FileBusy",
            _ => "Sync_NetworkError"
        };
    }

    public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
