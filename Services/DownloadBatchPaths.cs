using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Services
{
    // A batch owns its allocated names until completion/removal. Existing destinations
    // are never merged or overwritten, including reservations made by other batches.
    internal sealed class DownloadBatchPaths : IDisposable
    {
        private static readonly object Gate = new();
        private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _owned = new();
        private readonly string _root;

        public DownloadBatchPaths(string root) => _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        public void Restore(string path)
        {
            Validate(path);
            lock (Gate)
            {
                if (!Reserved.Add(path)) throw new IOException("A restored destination is already reserved.");
                _owned.Add(path);
            }
        }

        public string Reserve(string parent, string name, bool folder)
        {
            string safe = SafeName(name);
            string extension = folder ? "" : Path.GetExtension(safe);
            string stem = folder ? safe : Path.GetFileNameWithoutExtension(safe);
            lock (Gate)
            {
                for (int number = 1; ; number++)
                {
                    string candidate = Path.Combine(parent, number == 1 ? safe : $"{stem} ({number}){extension}");
                    Validate(candidate);
                    if (Reserved.Contains(candidate) || File.Exists(candidate) || Directory.Exists(candidate)) continue;
                    Reserved.Add(candidate);
                    _owned.Add(candidate);
                    return candidate;
                }
            }
        }

        internal static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException();
            const string invalid = "<>:\"/\\|?*";
            string result = new(name.Select(c => char.IsControl(c) || invalid.Contains(c) ? '_' : c).ToArray());
            result = result.TrimEnd(' ', '.');
            if (result.Length == 0) result = "_";
            string device = result.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) &&
                    "123456789¹²³".Contains(device[3]))) result = "_" + result;
            // Leave room for collision suffixes and avoid splitting surrogate pairs.
            if (result.Length > 180)
            {
                string extension = Path.GetExtension(result);
                if (extension.Length > 30) extension = "";
                int length = 180 - extension.Length;
                if (char.IsHighSurrogate(result[length - 1])) length--;
                result = result[..length].TrimEnd(' ', '.') + extension;
            }
            return result;
        }

        public void EnsureDirectory(string path)
        {
            Validate(path);
            Directory.CreateDirectory(path);
            Validate(path);
        }

        public void Validate(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.Equals(_root, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(Path.EndsInDirectorySeparator(_root) ? _root : _root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The destination is outside the selected folder.");
            // Check the destination and ancestors again before publishing a file. A
            // replaced directory/junction must not redirect downloads outside the root.
            for (string current = full; current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("Reparse-point destinations are not supported.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        public void Dispose()
        {
            lock (Gate)
            {
                foreach (string path in _owned) Reserved.Remove(path);
                _owned.Clear();
            }
        }
    }
}
