using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CreateFolderSyncViewModel : ObservableObject, IDisposable
    {
        private readonly FolderSyncService _service;
        private List<FolderSyncRemoteFolder> _path = new();
        private CancellationTokenSource _navigation;
        private long _version;
        private DriveViewModel _preferredDrive;
        public ObservableCollection<DriveDTO> Drives { get; } = new();
        public ObservableCollection<FolderSyncRemoteFolder> Folders { get; } = new();
        [ObservableProperty] private DriveDTO _selectedDrive;
        [ObservableProperty] private string _localPath = "";
        [ObservableProperty] private string _cloudPath = "/";
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty] private bool _isBusy;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool CanEdit => !IsBusy;
        public bool CanGoUp => !IsBusy && _path.Count > 1;
        public bool CanBind => !IsBusy && SelectedDrive != null && _path.Count > 1 &&
            !string.IsNullOrWhiteSpace(LocalPath) &&
            new DirectoryInfo(FolderSyncRules.NormalizeRoot(LocalPath)).Name == _path[^1].Name;
        public string NameHint => string.IsNullOrWhiteSpace(LocalPath) ? "Sync_ChooseLocalHint".GetLocalized() :
            string.Format("Sync_SameNameHint".GetLocalized(), new DirectoryInfo(FolderSyncRules.NormalizeRoot(LocalPath)).Name);
        partial void OnLocalPathChanged(string value) { OnPropertyChanged(nameof(CanBind)); OnPropertyChanged(nameof(NameHint)); }
        partial void OnIsBusyChanged(bool value) { OnPropertyChanged(nameof(CanBind)); OnPropertyChanged(nameof(CanGoUp)); OnPropertyChanged(nameof(CanEdit)); }
        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        public CreateFolderSyncViewModel(FolderSyncService service, DriveViewModel preferredDrive = null)
        {
            _service = service;
            _preferredDrive = preferredDrive;
        }

        public async Task LoadDrivesAsync()
        {
            try
            {
                string file = Path.Combine(AppContext.BaseDirectory, "cache", "drives.json");
                if (File.Exists(file))
                {
                    var drives = JsonSerializer.Deserialize(await File.ReadAllTextAsync(file), DriveDTOSourceGenerationContext.Default.ListDriveDTO);
                    foreach (var drive in drives ?? new())
                        if (!string.IsNullOrWhiteSpace(drive.Provider?.DriveId) && !string.IsNullOrWhiteSpace(drive.Provider.HomeAccountId)) Drives.Add(drive);
                }
                if (Drives.Count == 0) ErrorMessage = "Sync_NoDrives".GetLocalized();
                else if (_preferredDrive != null)
                    SelectedDrive = Drives.FirstOrDefault(d => d.Provider.DriveId == _preferredDrive.Provider.DriveId &&
                        d.Provider.HomeAccountId == _preferredDrive.Provider.HomeAccountId);
            }
            catch { ErrorMessage = "Sync_ConfigError".GetLocalized(); }
        }

        public Task SelectDriveAsync() => NavigateAsync(new List<FolderSyncRemoteFolder>());

        public Task OpenFolderAsync(FolderSyncRemoteFolder folder) => NavigateAsync(_path.Append(folder).ToList());

        [RelayCommand]
        private Task GoUpAsync() => _path.Count > 1 ? NavigateAsync(_path.Take(_path.Count - 1).ToList()) : Task.CompletedTask;

        private async Task NavigateAsync(List<FolderSyncRemoteFolder> next)
        {
            _navigation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _navigation = cancellation;
            long version = ++_version;
            IsBusy = true;
            ErrorMessage = "";
            var drive = SelectedDrive;
            if (next.Count == 0) { _path.Clear(); Folders.Clear(); CloudPath = "/"; }
            try
            {
                if (drive == null) return;
                using var target = _service.CreateBrowser(new FolderSyncBinding
                { AccountId = drive.Provider.HomeAccountId, DriveId = drive.Provider.DriveId });
                if (next.Count == 0)
                {
                    next.Add(await target.GetRootAsync(cancellation.Token));
                    var preferred = _preferredDrive;
                    _preferredDrive = null;
                    if (preferred != null && preferred.Provider.DriveId == drive.Provider.DriveId &&
                        !string.Equals(preferred.ParentItemId, "root", StringComparison.OrdinalIgnoreCase))
                    {
                        var path = await preferred.Provider.GetFolderPathAsync(preferred.ParentItemId, cancellation.Token);
                        next.AddRange(path.Select(p => new FolderSyncRemoteFolder(p.Id, p.Name)));
                    }
                }
                var folders = await target.BrowseAsync(next[^1].Id, cancellation.Token);
                if (version != _version) return;
                _path = next;
                Folders.Clear();
                foreach (var folder in folders) Folders.Add(folder);
                CloudPath = "/" + string.Join("/", next.Skip(1).Select(p => p.Name));
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) { if (version == _version) ErrorMessage = FolderSyncJob.GetErrorKey(exception).GetLocalized(); }
            finally
            {
                if (version == _version) { _navigation = null; IsBusy = false; }
            }
        }

        public async Task<bool> BindAsync()
        {
            if (!CanBind) return false;
            IsBusy = true;
            ErrorMessage = "";
            try
            {
                await _service.AddAsync(new FolderSyncBinding
                {
                    LocalPath = LocalPath, FolderName = _path[^1].Name,
                    DriveId = SelectedDrive.Provider.DriveId, AccountId = SelectedDrive.Provider.HomeAccountId,
                    DriveName = SelectedDrive.DisplayName, RemoteFolderId = _path[^1].Id, RemotePath = CloudPath,
                    RemoteAncestorIds = _path.Take(_path.Count - 1).Select(p => p.Id).ToList()
                });
                return true;
            }
            catch (Exception exception) { ErrorMessage = FolderSyncJob.GetErrorKey(exception).GetLocalized(); return false; }
            finally { IsBusy = false; }
        }

        public void Dispose() { _version++; _navigation?.Cancel(); }
    }
}
