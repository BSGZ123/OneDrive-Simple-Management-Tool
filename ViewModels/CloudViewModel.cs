using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models.DTO;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CloudViewModel : ObservableObject
    {
        private readonly DriveConfigurationStore _store;
        private readonly Func<DriveDTO, DriveViewModel> _createDrive;
        private long _revision;
        private readonly SemaphoreSlim _changes = new(1, 1);
        public CloudViewModel() : this(Ioc.Default.GetService<DriveConfigurationStore>()) { }
        public CloudViewModel(DriveConfigurationStore store, Func<DriveDTO, DriveViewModel> createDrive = null)
        {
            _store = store;
            _createDrive = createDrive ?? (item => new DriveViewModel(new OneDrive(item.Provider.DriveId, item.Provider.HomeAccountId), item.DisplayName));
        }
        public ObservableCollection<DriveViewModel> Drives { get; } = new();
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEmpty))]
        private bool _canAdd;
        public bool IsEmpty => CanAdd && Drives.Count == 0;
        [ObservableProperty] private bool _needsRecovery;
        [ObservableProperty] private bool _hasDuplicates;
        [ObservableProperty] private DriveViewModel _selectedDrive;

        public async Task LoadDrivesFromDisk()
        {
            await _changes.WaitAsync();
            try
            {
                Apply(await _store.LoadAsync());
                NeedsRecovery = HasDuplicates = false;
                CanAdd = true;
                ErrorMessage = _store.LegacyCleanupPending ? "Configuration_LegacyCleanup".GetLocalized() : "";
            }
            catch (Exception exception)
            {
                CanAdd = false;
                NeedsRecovery = true;
                HasDuplicates = exception is ConfigurationException { Failure: ConfigurationFailure.Duplicate };
                ErrorMessage = AccountConfigurationErrors.Message(exception);
            }
            finally { _changes.Release(); }
        }

        private void Apply(ConfigurationSnapshot<List<DriveDTO>> snapshot)
        {
            var existing = Drives.ToDictionary(d => (d.Provider.HomeAccountId, d.Provider.DriveId));
            Drives.Clear();
            foreach (var item in snapshot.Data)
            {
                string displayName = string.IsNullOrWhiteSpace(item.DisplayName) ? "Account_DefaultName".GetLocalized() : item.DisplayName;
                if (!existing.TryGetValue(DriveConfigurationStore.Identity(item), out var drive) || drive.DisplayName != displayName)
                    drive = _createDrive(item);
                Drives.Add(drive);
            }
            _revision = snapshot.Revision;
            OnPropertyChanged(nameof(IsEmpty));
        }

        public async Task AddDriveAsync(DriveViewModel drive, CancellationToken token)
        {
            await _changes.WaitAsync(token);
            try
            {
                var duplicate = Drives.FirstOrDefault(d => d.Provider.HomeAccountId == drive.Provider.HomeAccountId && d.Provider.DriveId == drive.Provider.DriveId);
                if (duplicate != null)
                {
                    SelectedDrive = duplicate;
                    throw new ConfigurationException(ConfigurationFailure.Duplicate);
                }
                var snapshot = await _store.AddAsync(new DriveDTO
                {
                    DisplayName = drive.DisplayName,
                    Provider = new ProviderDTO { HomeAccountId = drive.Provider.HomeAccountId, DriveId = drive.Provider.DriveId }
                }, _revision, token);
                Apply(snapshot);
                SelectedDrive = Drives.First(d => d.Provider.HomeAccountId == drive.Provider.HomeAccountId && d.Provider.DriveId == drive.Provider.DriveId);
                ErrorMessage = _store.LegacyCleanupPending ? "Configuration_LegacyCleanup".GetLocalized() : "";
            }
            finally { _changes.Release(); }
        }
    }
}
