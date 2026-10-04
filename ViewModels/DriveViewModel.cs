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
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DriveViewModel : ObservableObject
    {
        private sealed class Listing
        {
            public string ParentId;
            public string Query;
            public string Filter;
            public string SelectionId;
            public string RequiredSelectionId;
            public List<BreadcrumbItem> Path;
            public bool ResolvePath;
            public bool Committed;
            public bool Complete;
            public string NextLink;
            public int ExcludedCount;
            public readonly HashSet<string> LoadedLinks = new(StringComparer.Ordinal);
            public readonly Dictionary<string, FileViewModel> Items = new(StringComparer.Ordinal);
        }

        private Listing _view;
        private Listing _attempt;
        private CancellationTokenSource _requestCancellation;
        private long _requestVersion;
        private bool _updatingSelection;

        public DriveViewModel(OneDrive provider, string displayName = null)
        {
            Provider = provider;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? "Account_DefaultName".GetLocalized() : displayName;
            BreadcrumbItems.Add(RootBreadcrumb());
        }

        private static BreadcrumbItem RootBreadcrumb() => new() { Name = "RootFileName".GetLocalized(), ItemId = "Root" };

        [RelayCommand(AllowConcurrentExecutions = true)]
        public Task GetFiles(string itemId = "Root") => LoadFiles(itemId, null, null,
            itemId == "Root" ? new List<BreadcrumbItem> { RootBreadcrumb() } : null, itemId != "Root");

        public Task OpenLocationAsync(string folderId, string selectedItemId) => RunLoad(new Listing
        {
            ParentId = folderId, SelectionId = selectedItemId, RequiredSelectionId = selectedItemId, ResolvePath = true
        });

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task Refresh() => await TryRefresh();

        public Task<bool> TryRefresh()
        {
            // A mutation may finish while a newly requested search/navigation is awaiting its first page.
            // Refresh that latest intent instead of replacing it with the previously displayed context.
            Listing target = IsLoading == Visibility.Visible ? _attempt : _view ?? _attempt;
            return RunLoad(new Listing
            {
                ParentId = target?.ParentId ?? "Root", Query = target?.Query, Filter = target?.Filter,
                Path = target?.Path ?? BreadcrumbItems.ToList(), ResolvePath = target?.ResolvePath == true && !target.Committed,
                SelectionId = target == _view ? SelectedItem?.Id ?? target?.SelectionId : target?.SelectionId,
                RequiredSelectionId = target?.RequiredSelectionId
            });
        }

        public async Task<bool> RefreshAfterMutation()
        {
            Task<bool> refresh = TryRefresh();
            long version = _requestVersion;
            bool succeeded = await refresh;
            // A later navigation or explicit stop owns the screen; do not publish an old warning.
            if (!succeeded && version == _requestVersion)
                ErrorMessage = "FileOperation_RefreshFailed".GetLocalized();
            return succeeded || version != _requestVersion;
        }

        private Task<bool> LoadFiles(string parentId, string query, string filter,
            List<BreadcrumbItem> path, bool resolvePath = false)
        {
            return RunLoad(new Listing
            {
                ParentId = parentId, Query = query, Filter = filter, Path = path, ResolvePath = resolvePath,
                SelectionId = parentId == ParentItemId && query == _view?.Query ? SelectedItem?.Id ?? _view?.SelectionId : null
            });
        }

        private async Task<bool> RunLoad(Listing listing)
        {
            _requestCancellation?.Cancel();
            using var cancellation = new CancellationTokenSource();
            _requestCancellation = cancellation;
            long version = ++_requestVersion;
            _attempt = listing;
            IsLoading = Visibility.Visible;
            ErrorMessage = string.Empty;
            BookmarkMessage = string.Empty;
            NotifyListing();
            try
            {
                if (listing.ResolvePath && !listing.Committed)
                {
                    var path = await Provider.GetFolderPathAsync(listing.ParentId, cancellation.Token);
                    if (version != _requestVersion) return false;
                    listing.Path = new List<BreadcrumbItem> { RootBreadcrumb() };
                    listing.Path.AddRange(path.Select(item => new BreadcrumbItem { Name = item.Name, ItemId = item.Id }));
                }

                do
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    string link = listing.NextLink;
                    if (link != null && listing.LoadedLinks.Contains(link)) throw new InvalidDataException();
                    var page = await Provider.GetFilePageAsync(listing.ParentId, listing.Query, link, cancellation.Token);
                    if (version != _requestVersion) return false;
                    cancellation.Token.ThrowIfCancellationRequested();

                    if (!listing.Committed)
                    {
                        _view = listing;
                        listing.Committed = true;
                        SetVisibleFiles(Array.Empty<FileViewModel>(), null);
                        BreadcrumbItems.Clear();
                        foreach (var crumb in listing.Path ?? new List<BreadcrumbItem> { RootBreadcrumb() }) BreadcrumbItems.Add(crumb);
                        OnPropertyChanged(nameof(ParentItemId));
                    }
                    // A page commits as one unit. Cancellation cannot skip its remaining rows.
                    foreach (var item in page.Items)
                    {
                        if (listing.Items.ContainsKey(item.Id)) continue;
                        var file = new FileViewModel(this, item);
                        listing.Items.Add(file.Id, file);
                        if (Matches(file, listing.Filter))
                        {
                            Files.Add(file);
                            if (file.IsImage) Images.Add(file);
                            if (file.Id == listing.SelectionId) SelectedItem = file;
                        }
                    }
                    if (link != null) listing.LoadedLinks.Add(link);
                    listing.ExcludedCount += page.ExcludedCount;
                    listing.NextLink = page.NextLink;
                    listing.Complete = page.NextLink == null;
                    if (listing.Complete && SelectedItem == null) listing.SelectionId = null;
                    NotifyListing();
                    // Render each page and process navigation/cancellation between pages.
                    await Task.Yield();
                } while (!listing.Complete);
                if (version == _requestVersion && listing.RequiredSelectionId != null)
                {
                    if (!listing.Items.ContainsKey(listing.RequiredSelectionId)) ErrorMessage = "Bookmarks_LocationChanged".GetLocalized();
                    listing.RequiredSelectionId = null;
                }
                return version == _requestVersion;
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception exception)
            {
                if (version == _requestVersion) ErrorMessage = FileOperationErrors.GetMessage(exception);
                return false;
            }
            finally
            {
                if (version == _requestVersion)
                {
                    _requestCancellation = null;
                    IsLoading = Visibility.Collapsed;
                    NotifyListing();
                }
            }
        }

        [RelayCommand]
        public void CancelLoading()
        {
            ++_requestVersion;
            _requestCancellation?.Cancel();
            _requestCancellation = null;
            _attempt = _view;
            IsLoading = Visibility.Collapsed;
            ErrorMessage = string.Empty;
            NotifyListing();
        }

        [RelayCommand]
        public async Task RetryLoading()
        {
            if (CanRetry) await RunLoad(_attempt);
        }

        private static bool Matches(FileViewModel file, string filter) => filter == null ||
            file.Name.Contains(filter, StringComparison.OrdinalIgnoreCase);

        private void SetVisibleFiles(IEnumerable<FileViewModel> files, string selectionId)
        {
            _updatingSelection = true;
            try
            {
                // One reset per filter change avoids thousands of Remove notifications.
                Files = new ObservableCollection<FileViewModel>(files);
                Images = new ObservableCollection<FileViewModel>(Files.Where(file => file.IsImage));
                OnPropertyChanged(nameof(Files));
                OnPropertyChanged(nameof(Images));
                SelectedItem = Files.FirstOrDefault(file => file.Id == selectionId);
            }
            finally { _updatingSelection = false; }
        }

        partial void OnSelectedItemChanged(FileViewModel value)
        {
            if (!_updatingSelection && _view != null && (value != null || IsLoading != Visibility.Visible))
                _view.SelectionId = value?.Id;
        }

        public void FilterByName(string name)
        {
            if (_view == null || _view.Query != null) return;
            if (!string.IsNullOrEmpty(name) && !SearchQueryRules.TryNormalize(name, out name)) return;
            if (_attempt != _view) CancelLoading();
            _view.Filter = string.IsNullOrEmpty(name) ? null : name;
            string selectedId = SelectedItem?.Id ?? _view.SelectionId;
            SetVisibleFiles(_view.Items.Values.Where(file => Matches(file, _view.Filter)), selectedId);
            _view.SelectionId = SelectedItem?.Id ?? (IsLoading == Visibility.Visible ? selectedId : null);
            NotifyListing();
        }

        public Task ApplyLocalFilter(string keyword)
        {
            if (!SearchQueryRules.TryNormalize(keyword, out keyword)) return Task.CompletedTask;
            if (_view == null || _view.Query != null)
                return LoadFiles(ParentItemId, null, keyword, BreadcrumbItems.ToList());
            FilterByName(keyword);
            return Task.CompletedTask;
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        public Task SearchFile(string keyword) => SearchQueryRules.TryNormalize(keyword, out keyword)
            ? LoadFiles(ParentItemId, keyword, null, BreadcrumbItems.ToList()) : Task.CompletedTask;

        [RelayCommand(AllowConcurrentExecutions = true)]
        public Task ClearSearch()
        {
            if (_view?.Query != null) return LoadFiles(ParentItemId, null, null, BreadcrumbItems.ToList());
            FilterByName(null);
            return Task.CompletedTask;
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        public Task OpenFolder(FileViewModel file)
        {
            if (file?.IsFolder != true || file.Drive != this || !Files.Contains(file)) return Task.CompletedTask;
            var path = BreadcrumbItems.ToList();
            path.Add(new BreadcrumbItem { Name = file.Name, ItemId = file.Id });
            return LoadFiles(file.Id, null, null, path, _view?.Query != null);
        }

        public Task NavigateToBreadcrumb(int index)
        {
            if (index < 0 || index >= BreadcrumbItems.Count) return Task.CompletedTask;
            return LoadFiles(BreadcrumbItems[index].ItemId, null, null, BreadcrumbItems.Take(index + 1).ToList());
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        public Task GoUp() => NavigateToBreadcrumb(BreadcrumbItems.Count - 2);

        public void RemoveFile(string id)
        {
            _view?.Items.Remove(id);
            var file = Files.FirstOrDefault(item => item.Id == id);
            if (file != null) { Files.Remove(file); Images.Remove(file); }
            if (SelectedItem?.Id == id) SelectedItem = null;
            if (_view?.SelectionId == id) _view.SelectionId = null;
            NotifyListing();
        }

        public void UpdateFileName(string id, string name)
        {
            if (_view == null || !_view.Items.TryGetValue(id, out var file)) return;
            file.UpdateName(name);
            string selectionId = SelectedItem?.Id;
            SetVisibleFiles(_view.Items.Values.Where(item => Matches(item, _view.Filter)), selectionId);
            _view.SelectionId = SelectedItem?.Id;
            NotifyListing();
        }

        [RelayCommand]
        private async Task GetCapacity()
        {
            try
            {
                Quota quota = await Provider.GetStorageInfo();
                StorageInfo = Utils.ReadableFileSize(quota.Used) + " / " + Utils.ReadableFileSize(quota.Total);
            }
            catch (Exception exception) { StorageInfo = FileOperationErrors.GetMessage(exception); }
        }

        [RelayCommand]
        private async Task SignIn()
        {
            try { await Provider.SignInAsync(); await TryRefresh(); }
            catch (Exception exception) { ErrorMessage = AccountConfigurationErrors.Message(exception); }
        }

        private void NotifyListing()
        {
            foreach (string property in new[] { nameof(IsSearchActive), nameof(SearchVisibility), nameof(SearchDescription),
                nameof(Keyword), nameof(IsDriveSearch), nameof(IsComplete), nameof(LoadedCount), nameof(StatusText),
                nameof(CanRetry), nameof(RetryVisibility), nameof(CanCreateHere) }) OnPropertyChanged(property);
        }

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
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasBookmarkMessage))]
        private string _bookmarkMessage = string.Empty;
        public bool HasBookmarkMessage => !string.IsNullOrEmpty(BookmarkMessage);

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public Visibility ListVisibility => Layout == FileLayout.List ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GridVisibility => Layout == FileLayout.Grid ? Visibility.Visible : Visibility.Collapsed;
        public string ParentItemId => _view?.ParentId ?? "Root";
        public bool IsDriveSearch => _view?.Query != null;
        public string Keyword => _view?.Query ?? _view?.Filter ?? string.Empty;
        public bool IsSearchActive => !string.IsNullOrEmpty(Keyword);
        public Visibility SearchVisibility => IsSearchActive ? Visibility.Visible : Visibility.Collapsed;
        public bool IsComplete => _view?.Complete == true;
        public int LoadedCount => _view?.Items.Count ?? 0;
        public bool CanRetry => IsLoading != Visibility.Visible && _attempt != null && !_attempt.Complete;
        public Visibility RetryVisibility => CanRetry ? Visibility.Visible : Visibility.Collapsed;
        public bool CanCreateHere => !IsDriveSearch && IsLoading != Visibility.Visible;
        public string SearchDescription => IsSearchActive ? string.Format(
            (IsDriveSearch ? "Drive_SearchScope" : "Drive_FilterScope").GetLocalized(), Keyword) : string.Empty;
        public string StatusText
        {
            get
            {
                if (IsLoading == Visibility.Visible && _attempt != _view) return "Drive_LoadingNew".GetLocalized();
                string key = IsLoading == Visibility.Visible ? "Drive_LoadingCount" : !IsComplete ? "Drive_PartialCount" : "Drive_CompleteCount";
                int excluded = _view?.ExcludedCount ?? 0;
                if (IsComplete && Files.Count == 0 && excluded == 0)
                    return (IsSearchActive ? "Drive_NoMatches" : "Drive_Empty").GetLocalized();
                string status = string.Format(key.GetLocalized(), LoadedCount, Files.Count);
                return excluded == 0 ? status : status + " " + string.Format("Drive_ExcludedCount".GetLocalized(), excluded);
            }
        }
        public ObservableCollection<FileViewModel> Files { get; private set; } = new();
        public ObservableCollection<FileViewModel> Images { get; private set; } = new();
        public ObservableCollection<BreadcrumbItem> BreadcrumbItems { get; } = new();
        public OneDrive Provider { get; }
        public string DisplayName { get; }
    }
}
