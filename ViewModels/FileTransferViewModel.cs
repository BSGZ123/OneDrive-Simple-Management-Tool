using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class FileTransferViewModel : FileOperationViewModel
    {
        private readonly DriveViewModel _drive;
        private readonly IFileTransferService _service;
        private readonly TimeSpan _pollInterval;
        private readonly int _pollLimit;
        private List<TransferItem> _path = new();
        private CancellationTokenSource _cancellation;
        private bool _started;
        private FileTransferConflict _confirmedConflict;
        private string _destination;

        public FileTransferViewModel(IEnumerable<FileViewModel> files, FileTransferKind kind,
            IFileTransferService service = null, TimeSpan? pollInterval = null, int pollLimit = 60)
        {
            var targets = files.ToArray();
            if (targets.Length == 0 || targets.Any(file => file == null || file.Drive != targets[0].Drive))
                throw new ArgumentException("Transfers require items from one drive.", nameof(files));
            _drive = targets[0].Drive;
            _service = service ?? _drive.Provider.CreateFileTransferService();
            Kind = kind;
            Items = targets.DistinctBy(file => file.Id).Select(file => new TransferItemViewModel(file.Id, file.Name)).ToArray();
            _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
            _pollLimit = Math.Max(1, pollLimit);
            PropertyChanged += (_, args) => { if (args.PropertyName == nameof(IsBusy)) NotifyControls(); };
        }

        public FileTransferKind Kind { get; }
        public IReadOnlyList<TransferItemViewModel> Items { get; }
        public ObservableCollection<TransferItem> Folders { get; } = new();
        [ObservableProperty] private int _conflictIndex;
        [ObservableProperty] private string _destinationPath = "/";
        [ObservableProperty] private string _progressText = "";
        public string Title => (Kind == FileTransferKind.Move ? "Transfer_MoveTitle" : "Transfer_CopyTitle").GetLocalized();
        public string SubmitText => (_started ? "Transfer_Retry" : Kind == FileTransferKind.Move ? "Transfer_Move" : "Transfer_Copy").GetLocalized();
        public string Description => string.Format("Transfer_Description".GetLocalized(), Items.Count, _drive.DisplayName);
        public bool CanChoose => !IsBusy && !_started;
        public Visibility PickerVisibility => _started ? Visibility.Collapsed : Visibility.Visible;
        public bool CanGoUp => CanChoose && _path.Count > 1;
        public override bool CanSubmit => !IsBusy && _path.Count > 0 && Items.Any(item => item.CanRetry);
        public bool HasPendingCopy => Kind == FileTransferKind.Copy && Items.Any(item => item.MonitorUrl != null && item.State != FileTransferState.Succeeded);
        public bool CanStopTracking => !IsBusy && HasPendingCopy;
        public Visibility TrackingVisibility => HasPendingCopy ? Visibility.Visible : Visibility.Collapsed;
        public string Summary => string.Format("Transfer_Summary".GetLocalized(), Items.Count(i => i.State == FileTransferState.Succeeded),
            Items.Count(i => i.State == FileTransferState.Skipped), Items.Count(i => i.State == FileTransferState.Failed || i.State == FileTransferState.Uncertain), Items.Count);

        public Task InitializeAsync() => _path.Count == 0 ? NavigateAsync(new()) : Task.CompletedTask;
        public Task OpenFolderAsync(TransferItem folder) => CanChoose && Folders.Contains(folder) ? NavigateAsync(_path.Append(folder).ToList()) : Task.CompletedTask;
        [RelayCommand] public Task GoUpAsync() => CanGoUp ? NavigateAsync(_path.Take(_path.Count - 1).ToList()) : Task.CompletedTask;
        [RelayCommand] public Task ReloadAsync() => CanChoose ? NavigateAsync(_path.ToList()) : Task.CompletedTask;
        [RelayCommand] public void Stop() => _cancellation?.Cancel();

        [RelayCommand]
        public void StopTracking()
        {
            if (!CanStopTracking) return;
            // Explicitly release an expired/unreachable monitor without claiming the cloud job stopped.
            foreach (var row in Items.Where(item => item.MonitorUrl != null))
            {
                row.MonitorUrl = null;
                row.State = FileTransferState.Uncertain;
                row.ErrorMessage = "Transfer_Uncertain".GetLocalized();
            }
            NotifyControls();
        }

        private async Task NavigateAsync(List<TransferItem> path)
        {
            if (!CanChoose) return;
            IsBusy = true;
            ErrorMessage = "";
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            try
            {
                if (path.Count == 0) path.Add(await _service.GetRootAsync(cancellation.Token));
                var folders = await _service.GetChildrenAsync(path[^1].Id, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                _path = path;
                DestinationPath = "/" + string.Join("/", path.Skip(1).Select(item => item.Name));
                Folders.Clear();
                foreach (var folder in folders.Where(item => item.IsUsable && item.IsFolder && !Items.Any(row => row.Id == item.Id))
                    .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)) Folders.Add(folder);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception exception) { ErrorMessage = Error(exception); }
            finally { _cancellation = null; IsBusy = false; NotifyControls(); }
        }

        [RelayCommand]
        public async Task SubmitAsync()
        {
            if (!CanSubmit) return;
            IsBusy = true;
            ErrorMessage = "";
            using var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            var token = cancellation.Token;
            int completedBefore = Items.Count(item => item.State == FileTransferState.Succeeded);
            try
            {
                var destination = await _service.GetItemAsync(_destination ?? _path[^1].Id, token);
                if (!destination.IsFolder || !destination.IsUsable) throw new FileTransferException("Transfer_InvalidTarget");
                var cache = new Dictionary<string, TransferItem>(StringComparer.Ordinal) { [destination.Id] = destination };
                var targetPath = await AncestorsAsync(destination, cache, token);
                if (targetPath.Append(destination).Any(folder => Items.Any(row => row.Id == folder.Id)))
                    throw new FileTransferException("Transfer_InvalidTarget");
                _destination = destination.Id;
                if (!_started) _confirmedConflict = ConflictIndex == 1 ? FileTransferConflict.Skip : FileTransferConflict.KeepBoth;
                _started = true;
                NotifyControls();

                // Resolve the selected hierarchy before mutating any of its members.
                foreach (var row in Items.Where(row => row.CanRetry && !row.Submitted))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        row.Source = await _service.GetItemAsync(row.Id, token);
                        if (row.Source.IsRoot || !row.Source.IsUsable) throw new FileTransferException("Transfer_Unsupported");
                        var ancestors = await AncestorsAsync(row.Source, cache, token);
                        row.Depth = ancestors.Count;
                        row.CoveredBy = ancestors.Select(item => item.Id).FirstOrDefault(id => Items.Any(other => other.Id == id));
                        row.State = FileTransferState.Pending;
                        row.ErrorMessage = "";
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) { row.State = FileTransferState.Failed; row.ErrorMessage = Error(exception); }
                }

                foreach (var row in Items.OrderBy(row => row.Depth))
                {
                    token.ThrowIfCancellationRequested();
                    if (!row.CanRetry || (row.Source == null && !row.Submitted) || row.State == FileTransferState.Failed) continue;
                    if (row.CoveredBy != null)
                    {
                        var ancestor = Items.Single(other => other.Id == row.CoveredBy);
                        row.State = ancestor.State is FileTransferState.Succeeded or FileTransferState.Skipped ? FileTransferState.Skipped : FileTransferState.Failed;
                        row.ErrorMessage = (row.State == FileTransferState.Skipped ? "Transfer_Included" : "Transfer_AncestorFailed").GetLocalized();
                        NotifyControls();
                        continue;
                    }
                    row.State = FileTransferState.Working;
                    row.ErrorMessage = "";
                    ProgressText = row.Name;
                    try { await TransferAsync(row, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        row.State = row.Submitted ? FileTransferState.Waiting : FileTransferState.Pending;
                        break;
                    }
                    catch (FileTransferException error) when (error.Uncertain)
                    {
                        row.State = Kind == FileTransferKind.Copy && row.MonitorUrl == null ? FileTransferState.Uncertain : FileTransferState.Waiting;
                        row.ErrorMessage = Error(error);
                    }
                    catch (Exception exception)
                    {
                        row.State = row.Submitted ? Kind == FileTransferKind.Copy && row.MonitorUrl == null
                            ? FileTransferState.Uncertain : FileTransferState.Waiting : FileTransferState.Failed;
                        row.ErrorMessage = Error(exception);
                    }
                    NotifyControls();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception exception) { ErrorMessage = Error(exception); }
            finally
            {
                // Refresh cannot cause a completed mutation to be submitted again.
                if (Items.Count(item => item.State == FileTransferState.Succeeded) > completedBefore)
                    await _drive.RefreshAfterMutation();
                _cancellation = null;
                IsBusy = false;
                HasSucceeded = Items.All(item => item.State is FileTransferState.Succeeded or FileTransferState.Skipped);
                ProgressText = "";
                NotifyControls();
            }
        }

        private async Task<List<TransferItem>> AncestorsAsync(TransferItem item, Dictionary<string, TransferItem> cache, CancellationToken token)
        {
            var result = new List<TransferItem>();
            var visited = new HashSet<string>(StringComparer.Ordinal) { item.Id };
            while (!item.IsRoot)
            {
                if (string.IsNullOrWhiteSpace(item.ParentId) || !visited.Add(item.ParentId) || visited.Count > 512) throw new InvalidDataException();
                if (!cache.TryGetValue(item.ParentId, out var parent))
                    cache[item.ParentId] = parent = await _service.GetItemAsync(item.ParentId, token);
                if (!parent.IsFolder || !parent.IsUsable) throw new InvalidDataException();
                result.Add(parent);
                item = parent;
            }
            return result;
        }

        private async Task TransferAsync(TransferItemViewModel row, CancellationToken token)
        {
            for (int conflictAttempts = 0; conflictAttempts < 5; conflictAttempts++)
            {
                if (row.Submitted && Kind == FileTransferKind.Move)
                {
                    var current = await _service.GetItemAsync(row.Id, token);
                    if (current.ParentId == _destination && current.Name == row.TargetName)
                    { Succeed(row); return; }
                    if (current.ParentId != row.Source.ParentId || current.ETag != row.Source.ETag)
                    { row.State = FileTransferState.Uncertain; row.ErrorMessage = "Transfer_Changed".GetLocalized(); return; }
                    row.Submitted = false;
                }
                if (!row.Submitted)
                {
                    // Recheck ancestry immediately before each mutation, after earlier items or long copies.
                    var destination = await _service.GetItemAsync(_destination, token);
                    var destinationPath = await AncestorsAsync(destination, new(), token);
                    if (!destination.IsFolder || !destination.IsUsable || destinationPath.Append(destination).Any(folder => folder.Id == row.Id))
                        throw new FileTransferException("Transfer_InvalidTarget");
                    var source = await _service.GetItemAsync(row.Id, token);
                    if (!source.IsUsable || source.IsRoot || source.Name != row.Source.Name || source.ParentId != row.Source.ParentId || source.IsFolder != row.Source.IsFolder)
                        throw new FileTransferException("Transfer_Changed");
                    row.Source = source;
                    if (Kind == FileTransferKind.Move && source.ParentId == _destination)
                    { row.State = FileTransferState.Skipped; row.ErrorMessage = "Transfer_AlreadyHere".GetLocalized(); return; }
                    var children = await _service.GetChildrenAsync(_destination, token);
                    var names = children.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (names.Contains(source.Name) && _confirmedConflict == FileTransferConflict.Skip)
                    { row.State = FileTransferState.Skipped; row.ErrorMessage = "Transfer_ConflictSkipped".GetLocalized(); return; }
                    row.TargetName = AvailableName(source, names);
                    token.ThrowIfCancellationRequested();
                    // Once sent, stop only prevents later work. Give the mutation its own bounded
                    // token so cancelling the dialog cannot discard an accepted copy receipt.
                    using var mutationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    row.Submitted = true;
                    try
                    {
                        if (Kind == FileTransferKind.Move)
                        {
                            await _service.MoveAsync(source, _destination, row.TargetName, mutationTimeout.Token);
                            var moved = await _service.GetItemAsync(source.Id, token);
                            if (moved.ParentId != _destination || moved.Name != row.TargetName) throw new FileTransferException("Transfer_Uncertain", uncertain: true);
                            Succeed(row);
                            return;
                        }
                        row.MonitorUrl = await _service.CopyAsync(source, _destination, row.TargetName, mutationTimeout.Token);
                    }
                    catch (FileTransferException error) when (!error.Uncertain)
                    {
                        row.Submitted = false;
                        if (error.NameConflict)
                        {
                            if (_confirmedConflict == FileTransferConflict.Skip) { row.State = FileTransferState.Skipped; return; }
                            continue;
                        }
                        throw;
                    }
                }
                if (Kind == FileTransferKind.Copy)
                {
                    for (int poll = 0; poll < _pollLimit; poll++)
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        var status = await _service.GetCopyStatusAsync(row.MonitorUrl, timeout.Token);
                        row.Percentage = status.Percentage;
                        if (status.ErrorKey != null)
                        {
                            row.MonitorUrl = null;
                            row.Submitted = false;
                            if (status.NameConflict && _confirmedConflict == FileTransferConflict.KeepBoth) break;
                            if (status.NameConflict) { row.State = FileTransferState.Skipped; return; }
                            throw new FileTransferException(status.ErrorKey);
                        }
                        if (status.Completed)
                        {
                            var copied = await _service.GetItemAsync(status.ResourceId, token);
                            if (copied.Id == row.Id || copied.ParentId != _destination || copied.Name != row.TargetName || copied.IsFolder != row.Source.IsFolder)
                                throw new InvalidDataException();
                            Succeed(row);
                            return;
                        }
                        await Task.Delay(_pollInterval, token);
                    }
                    if (row.Submitted)
                    { row.State = FileTransferState.Waiting; row.ErrorMessage = "Transfer_StillRunning".GetLocalized(); return; }
                }
            }
            throw new FileTransferException("FileOperation_NameConflict");
        }

        private void Succeed(TransferItemViewModel row)
        {
            row.State = FileTransferState.Succeeded;
            row.Percentage = 100;
            row.MonitorUrl = null;
            if (Kind == FileTransferKind.Move) _drive.RemoveFile(row.Id);
        }

        private static string AvailableName(TransferItem source, HashSet<string> names)
        {
            if (!names.Contains(source.Name)) return source.Name;
            string extension = source.IsFolder ? "" : Path.GetExtension(source.Name);
            string stem = source.IsFolder ? source.Name : Path.GetFileNameWithoutExtension(source.Name);
            for (int number = 2; number < 10000; number++)
            {
                string suffix = $" ({number})";
                int length = Math.Min(stem.Length, 255 - suffix.Length - extension.Length);
                if (length < 1) throw new FileTransferException("FileOperation_NameConflict");
                if (char.IsHighSurrogate(stem[length - 1])) length--;
                string name = stem[..length] + suffix + extension;
                if (!names.Contains(name)) return name;
            }
            throw new FileTransferException("FileOperation_NameConflict");
        }

        private static string Error(Exception exception) => exception is FileTransferException error ? error.Key.GetLocalized() : FileOperationErrors.GetMessage(exception);
        private void NotifyControls()
        {
            foreach (string name in new[] { nameof(CanChoose), nameof(PickerVisibility), nameof(CanGoUp), nameof(CanSubmit), nameof(SubmitText), nameof(Summary), nameof(HasPendingCopy), nameof(CanStopTracking), nameof(TrackingVisibility) }) OnPropertyChanged(name);
        }
    }

    public partial class TransferItemViewModel(string id, string name) : ObservableObject
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        internal TransferItem Source;
        internal string CoveredBy;
        internal int Depth;
        internal bool Submitted;
        internal string MonitorUrl;
        [ObservableProperty] private string _targetName = "";
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private double _percentage;
        [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private FileTransferState _state;
        public bool CanRetry => State is FileTransferState.Pending or FileTransferState.Failed or FileTransferState.Waiting;
        public string StatusText => State == FileTransferState.Working && Percentage > 0
            ? string.Format("Transfer_Percentage".GetLocalized(), Percentage) : ("Transfer_State_" + State).GetLocalized();
    }
}
