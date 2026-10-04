using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class BookmarkItemViewModel(Bookmark bookmark) : ObservableObject
    {
        public Bookmark Bookmark { get; private set; } = bookmark;
        public string Name => Bookmark.Name;
        public string DriveName => Bookmark.DriveName;
        public string Kind => (Bookmark.IsFolder ? "FileItem_Folder" : "FileItem_File").GetLocalized();
        public string AddedText => string.Format("Bookmarks_AddedAt".GetLocalized(), Bookmark.AddedAt.ToLocalTime().ToString("g"));
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = "";
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public void Update(Bookmark value)
        {
            Bookmark = value;
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(DriveName));
            OnPropertyChanged(nameof(Kind));
        }
    }

    public partial class BookmarkViewModel(IBookmarkStore store, IBookmarkResolver resolver) : ObservableObject
    {
        private List<BookmarkItemViewModel> _all = new();
        private CancellationTokenSource _request;
        private bool _active;
        public event Action<BookmarkLocation> NavigationRequested;
        [ObservableProperty] private IReadOnlyList<BookmarkItemViewModel> _items = Array.Empty<BookmarkItemViewModel>();
        [ObservableProperty] private string _query = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUseItems), nameof(IsEmpty), nameof(CanRecoverBookmarks))]
        [NotifyCanExecuteChangedFor(nameof(ReloadCommand), nameof(RestoreBackupCommand))]
        private bool _isBusy;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
        private string _errorMessage = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanUseItems), nameof(CanRecoverBookmarks))]
        [NotifyCanExecuteChangedFor(nameof(RestoreBackupCommand))]
        private bool _needsRecovery;
        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool IsEmpty => _all.Count == 0 && !HasError && !IsBusy;
        public bool HasNoMatches => _all.Count > 0 && Items.Count == 0;
        public bool HasItems => Items.Count > 0;
        public bool CanRecoverBookmarks => !IsBusy && NeedsRecovery;
        public bool CanUseItems => _active && !IsBusy && !NeedsRecovery;
        public string CountText => string.Format("Bookmarks_Count".GetLocalized(), Items.Count, _all.Count);

        partial void OnQueryChanged(string value) => Filter();

        public async Task ActivateAsync()
        {
            if (_active) return;
            _active = true;
            await ReloadAsync();
        }

        public void Deactivate()
        {
            _active = false;
            _request?.Cancel();
            _request = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanUseItems));
        }

        private bool CanReload() => !IsBusy;
        private bool CanRecover() => !IsBusy && NeedsRecovery;

        [RelayCommand(CanExecute = nameof(CanReload))]
        private Task ReloadAsync() => RunAsync(async token => await LoadAsync(token), "Bookmarks_LoadFailed");

        [RelayCommand(CanExecute = nameof(CanRecover))]
        private Task RestoreBackupAsync() => RunAsync(async token =>
        {
            await store.RestoreBackupAsync(token);
            await LoadAsync(token);
        }, "Bookmarks_RecoveryFailed");

        // Called only after the page's explicit recovery confirmation.
        public Task RebuildAsync() => !NeedsRecovery ? Task.CompletedTask : RunAsync(async token =>
        {
            await store.RebuildAsync(token);
            await LoadAsync(token);
        }, "Bookmarks_RecoveryFailed");

        private async Task LoadAsync(CancellationToken token)
        {
            var bookmarks = await store.LoadAsync(token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _all = bookmarks.OrderByDescending(item => item.AddedAt).ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(item => new BookmarkItemViewModel(item)).ToList();
            NeedsRecovery = false;
            Filter();
        }

        public Task RemoveAsync(BookmarkItemViewModel item)
        {
            if (!CanUseItems || !_all.Contains(item)) return Task.CompletedTask;
            return RunAsync(async token =>
            {
                await store.RemoveAsync(item.Bookmark, token);
                token.ThrowIfCancellationRequested();
                _all.Remove(item);
                Filter();
            }, "Bookmarks_SaveFailed");
        }

        public Task OpenAsync(BookmarkItemViewModel item)
        {
            if (!CanUseItems || !_all.Contains(item)) return Task.CompletedTask;
            item.ErrorMessage = "";
            return RunAsync(async token =>
            {
                var location = await resolver.ResolveAsync(item.Bookmark, token).WaitAsync(token);
                token.ThrowIfCancellationRequested();
                if (location.Bookmark.Identity != item.Bookmark.Identity) throw new BookmarkException("Bookmarks_InvalidTarget");
                if (location.Bookmark != item.Bookmark)
                {
                    try { await store.UpdateMetadataAsync(location.Bookmark, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { location = location with { Notice = "Bookmarks_MetadataNotSaved".GetLocalized() }; }
                    token.ThrowIfCancellationRequested();
                    item.Update(location.Bookmark);
                }
                NavigationRequested?.Invoke(location);
            }, "Bookmarks_OpenFailed", item);
        }

        private async Task RunAsync(Func<CancellationToken, Task> action, string failureKey, BookmarkItemViewModel item = null)
        {
            if (!_active || IsBusy) return;
            using var request = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            _request = request;
            IsBusy = true;
            ErrorMessage = "";
            try { await action(request.Token); }
            catch (OperationCanceledException)
            {
                if (_request == request) SetError("Bookmarks_Timeout".GetLocalized(), item);
            }
            catch (Exception exception)
            {
                if (_request != request) return;
                NeedsRecovery |= exception is ConfigurationException { Failure: ConfigurationFailure.Invalid or ConfigurationFailure.Version or ConfigurationFailure.Protection or ConfigurationFailure.Missing };
                SetError(BookmarkErrors.Message(exception, failureKey), item);
            }
            finally
            {
                if (_request == request) { _request = null; IsBusy = false; }
            }
        }

        private void SetError(string message, BookmarkItemViewModel item)
        {
            if (item == null) ErrorMessage = message;
            else item.ErrorMessage = message;
        }

        private void Filter()
        {
            string query = Query?.Trim() ?? "";
            Items = _all.Where(item => query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                item.DriveName.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNoMatches));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(CountText));
        }
    }
}
