using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Graph.Models;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DriveViewModel : ObservableObject
    {
        public DriveViewModel(OneDrive provider, string displayName = null)
        {
            Provider = provider;
            DisplayName = displayName ?? provider.DriveId;
            BreadcrumbItems.Add(new BreadcrumbItem { Name = "RootFileName".GetLocalized(), ItemId = "Root" });
        }

        [RelayCommand]
        public async Task GetFiles(string itemId = "Root")
        {
            await LoadFiles(itemId, null, null);
        }

        [RelayCommand]
        public async Task Refresh()
        {
            await TryRefresh();
        }

        public Task<bool> TryRefresh() => LoadFiles(_parentItemId, _globalQuery, _localFilter);

        private async Task<bool> LoadFiles(string itemId, string globalQuery, string localFilter)
        {
            if (IsLoading == Visibility.Visible) return false;
            IsLoading = Visibility.Visible;
            ErrorMessage = string.Empty;
            try
            {
                var items = globalQuery == null
                    ? (await Provider.GetFiles(itemId))?.Value
                    : (await Provider.SearchGlobalItems(globalQuery))?.Value;
                if (items == null) throw new InvalidDataException();
                if (localFilter != null)
                    items = items.Where(item => item.Name?.Contains(localFilter, StringComparison.OrdinalIgnoreCase) == true).ToList();

                // Commit navigation and selection only after a successful response.
                string selectedId = itemId == _parentItemId ? SelectedItem?.Id : null;
                ReplaceFiles(items, selectedId);
                _parentItemId = itemId;
                _globalQuery = globalQuery;
                _localFilter = localFilter;
                OnPropertyChanged(nameof(ParentItemId));
                return true;
            }
            catch (Exception exception)
            {
                ErrorMessage = FileOperationErrors.GetMessage(exception);
                return false;
            }
            finally
            {
                IsLoading = Visibility.Collapsed;
            }
        }

        private void ReplaceFiles(IEnumerable<DriveItem> items, string selectedId)
        {
            var files = items.Select(item => new FileViewModel(this, item)).ToList();
            Files.Clear();
            Images.Clear();
            foreach (var file in files)
            {
                Files.Add(file);
                if (file.IsImage) Images.Add(file);
            }
            SelectedItem = Files.FirstOrDefault(file => file.Id == selectedId);
        }

        public void FilterByName(string name)
        {
            if (IsLoading == Visibility.Visible) return;
            _localFilter = name;
            foreach (var file in Files.Where(file => !file.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Files.Remove(file);
                Images.Remove(file);
            }
            if (!Files.Contains(SelectedItem)) SelectedItem = null;
        }

        [RelayCommand]
        public async Task SearchFile(string fileName)
        {
            await LoadFiles(_parentItemId, fileName, null);
        }

        [RelayCommand]
        public async Task OpenFolder(FileViewModel file)
        {
            if (file?.IsFolder != true || file.Drive != this) return;
            if (await LoadFiles(file.Id, null, null))
                BreadcrumbItems.Add(new BreadcrumbItem { Name = file.Name, ItemId = file.Id });
        }

        public async Task NavigateToBreadcrumb(int index)
        {
            if (index < 0 || index >= BreadcrumbItems.Count) return;
            if (await LoadFiles(BreadcrumbItems[index].ItemId, null, null))
            {
                while (BreadcrumbItems.Count > index + 1) BreadcrumbItems.RemoveAt(BreadcrumbItems.Count - 1);
            }
        }

        [RelayCommand]
        public Task GoUp() => NavigateToBreadcrumb(BreadcrumbItems.Count - 2);

        [RelayCommand]
        private async Task GetCapacity()
        {
            Quota quota = await Provider.GetStorageInfo();
            StorageInfo = Utils.ReadableFileSize(quota.Used) + " / " + Utils.ReadableFileSize(quota.Total);
        }

        private string _parentItemId = "Root";
        private string _globalQuery;
        private string _localFilter;
        [ObservableProperty] private Visibility _isLoading = Visibility.Collapsed;
        [ObservableProperty] private string _storageInfo;
        [ObservableProperty] private FileViewModel _selectedItem;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ListVisibility))]
        [NotifyPropertyChangedFor(nameof(GridVisibility))]
        private FileLayout _layout;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = string.Empty;

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public Visibility ListVisibility => Layout == FileLayout.List ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GridVisibility => Layout == FileLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        public string ParentItemId => _parentItemId;
        public ObservableCollection<FileViewModel> Files { get; } = new();
        public ObservableCollection<FileViewModel> Images { get; } = new();
        public ObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = new();
        public OneDrive Provider { get; }
        public string DisplayName { get; }
    }
}
