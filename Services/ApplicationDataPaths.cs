using System;
using System.IO;

namespace OneDrive_Simple_Management_Tool.Services
{
    public sealed class ApplicationDataPaths
    {
        public ApplicationDataPaths(string root = null, string applicationDirectory = null)
        {
            Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OneDriveSimpleManagementTool"));
            ApplicationDirectory = Path.GetFullPath(applicationDirectory ?? AppContext.BaseDirectory);
        }

        public string Root { get; }
        public string ApplicationDirectory { get; }
        public string Drives => Path.Combine(Root, "Configuration", "drives.dat");
        public string LegacyDrives => Path.Combine(ApplicationDirectory, "cache", "drives.json");
        public string Authentication => Path.Combine(Root, "Authentication");
        public string LegacyTokenCache => Path.Combine(ApplicationDirectory, "cache", "CloudFlowTokenCache.bin");
        public string FolderSync => Path.Combine(Root, "FolderSync");
        public string Diagnostics => Path.Combine(Root, "Diagnostics");
        public string Appearance => Path.Combine(Root, "Configuration", "appearance.json");
        public string Bookmarks => Path.Combine(Root, "Configuration", "bookmarks.dat");
        public string ReaderProgress => Path.Combine(Root, "Configuration", "reader-progress.dat");
        public string ReaderSettings => Path.Combine(Root, "Configuration", "reader-settings.dat");
        public string ReaderRuntime => Path.Combine(Root, "Reader", "WebView2");
    }
}
