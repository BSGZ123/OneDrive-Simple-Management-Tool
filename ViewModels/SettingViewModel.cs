using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class SettingViewModel : ObservableObject
    {
        private readonly IAppearanceSettingsStore _store;
        private readonly SemaphoreSlim _saves = new(1, 1);
        private AppearancePreferences _current = new();
        private Func<AppearancePreferences, bool> _apply;
        private Task _pendingSave = Task.CompletedTask;
        private long _change;
        private bool _loading;

        public SettingViewModel(IAppearanceSettingsStore store) => _store = store;

        [ObservableProperty] private int _themeIndex;
        [ObservableProperty] private int _materialIndex;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = "";
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))]
        private bool _isSaving;
        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(RetrySaveCommand))]
        private bool _hasUnsavedChanges;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSaveStatus))]
        private string _saveStatus = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasMaterialNotice))]
        private string _materialNotice = "";

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool HasSaveStatus => !string.IsNullOrEmpty(SaveStatus);
        public bool HasMaterialNotice => !string.IsNullOrEmpty(MaterialNotice);
        public string Version => string.Format("Settings_Version".GetLocalized(),
            typeof(SettingViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");

        public async Task InitializeAsync()
        {
            try { _current = await _store.LoadAsync(); }
            catch (Exception) { ErrorMessage = "Settings_LoadFailed".GetLocalized(); }
            // Loading must not write defaults over an unreadable preference file.
            _loading = true;
            try
            {
                ThemeIndex = (int)_current.Theme;
                MaterialIndex = (int)_current.Material;
            }
            finally { _loading = false; }
        }

        public void AttachAppearance(Func<AppearancePreferences, bool> apply)
        {
            _apply = apply;
            ApplyAppearance();
        }

        private void ApplyAppearance()
        {
            if (_apply == null) return;
            MaterialNotice = _apply(_current) ? "" : "Settings_MaterialFallback".GetLocalized();
        }

        partial void OnThemeIndexChanged(int value)
        {
            if (!Enum.IsDefined(typeof(AppearanceTheme), value))
            {
                ThemeIndex = (int)_current.Theme;
                return;
            }
            ChangeAppearance();
        }

        partial void OnMaterialIndexChanged(int value)
        {
            if (!Enum.IsDefined(typeof(AppearanceMaterial), value))
            {
                MaterialIndex = (int)_current.Material;
                return;
            }
            ChangeAppearance();
        }

        private void ChangeAppearance()
        {
            if (_loading) return;
            var next = new AppearancePreferences { Theme = (AppearanceTheme)ThemeIndex, Material = (AppearanceMaterial)MaterialIndex };
            if (next == _current) return;
            _current = next;
            ApplyAppearance();
            _pendingSave = PersistAsync(next, ++_change);
        }

        private async Task PersistAsync(AppearancePreferences preferences, long change)
        {
            HasUnsavedChanges = true;
            IsSaving = true;
            SaveStatus = "Settings_Saving".GetLocalized();
            await _saves.WaitAsync();
            try
            {
                await _store.SaveAsync(preferences);
                if (change != _change) return;
                HasUnsavedChanges = false;
                ErrorMessage = "";
                SaveStatus = "Settings_Saved".GetLocalized();
            }
            catch (Exception)
            {
                if (change != _change) return;
                ErrorMessage = "Settings_SaveFailed".GetLocalized();
                SaveStatus = "";
            }
            finally
            {
                _saves.Release();
                if (change == _change) IsSaving = false;
            }
        }

        private bool CanRetrySave() => HasUnsavedChanges && !IsSaving;

        [RelayCommand(CanExecute = nameof(CanRetrySave))]
        private async Task RetrySaveAsync()
        {
            _pendingSave = PersistAsync(_current, ++_change);
            await _pendingSave;
        }

        public async Task FlushAsync()
        {
            Task pending;
            do
            {
                pending = _pendingSave;
                await pending;
            } while (pending != _pendingSave);
        }
    }
}
