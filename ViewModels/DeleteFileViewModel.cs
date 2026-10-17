using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class DeleteFileViewModel : FileOperationViewModel
    {
        private readonly DriveViewModel _drive;
        private bool? _confirmedPermanent;

        public DeleteFileViewModel(FileViewModel file) : this(new[] { file }) { }

        public DeleteFileViewModel(IEnumerable<FileViewModel> files)
        {
            var targets = files.ToArray();
            if (targets.Length == 0 || targets.Any(file => file == null || file.Drive != targets[0]?.Drive))
                throw new ArgumentException("Deletion requires items from one drive.", nameof(files));
            _drive = targets[0].Drive;
            Items = targets.DistinctBy(file => file.Id).Select(file => new DeleteItemViewModel(file)).ToArray();
        }

        [RelayCommand]
        public async Task DeleteFile()
        {
            if (!CanSubmit) return;
            IsBusy = true;
            ErrorMessage = string.Empty;
            _confirmedPermanent ??= PermanentDelete;
            OnPropertyChanged(nameof(CanChooseDeleteMode));
            int succeededBefore = SuccessCount;
            try
            {
                // Successful items never run twice, including after a refresh failure.
                foreach (var item in Items.Where(item => !item.Succeeded))
                {
                    item.ErrorMessage = string.Empty;
                    item.Status = "DeleteBatch_Working".GetLocalized();
                    try
                    {
                        if (_confirmedPermanent.Value) await _drive.Provider.PermanentDeleteItem(item.File.Id);
                        else await _drive.Provider.DeleteItem(item.File.Id);
                        item.Succeeded = true;
                        item.Status = "DeleteBatch_Succeeded".GetLocalized();
                        _drive.RemoveFile(item.File.Id);
                    }
                    catch (Exception exception)
                    {
                        item.ErrorMessage = FileOperationErrors.GetMessage(exception);
                        item.Status = "DeleteBatch_Failed".GetLocalized();
                    }
                    NotifyResults();
                }
                HasAttempted = true;
                HasSucceeded = Items.All(item => item.Succeeded);
                if (!HasSucceeded) ErrorMessage = "DeleteBatch_RetryHint".GetLocalized();
                if (SuccessCount > succeededBefore) await _drive.RefreshAfterMutation();
            }
            finally
            {
                IsBusy = false;
                NotifyResults();
            }
        }

        private void NotifyResults()
        {
            foreach (string property in new[] { nameof(SuccessCount), nameof(FailureCount), nameof(Summary),
                nameof(SubmitText), nameof(Question), nameof(CanSubmit) }) OnPropertyChanged(property);
        }

        [ObservableProperty] private bool _permanentDelete;
        [ObservableProperty] private bool _hasAttempted;
        public IReadOnlyList<DeleteItemViewModel> Items { get; }
        public FileViewModel File => Items[0].File;
        public int SuccessCount => Items.Count(item => item.Succeeded);
        public int FailureCount => Items.Count(item => !string.IsNullOrEmpty(item.ErrorMessage));
        public bool CanChooseDeleteMode => !IsBusy && !_confirmedPermanent.HasValue;
        public string Question => HasAttempted ? "DeleteBatch_Results".GetLocalized()
            : string.Format("DeleteBatch_Question".GetLocalized(), Items.Count);
        public string Summary => string.Format("DeleteBatch_Summary".GetLocalized(), SuccessCount, FailureCount, Items.Count);
        public string SubmitText => (HasAttempted ? "DeleteBatch_Retry" : "DeleteFile_Dialog/PrimaryButtonText").GetLocalized();
    }

    public partial class DeleteItemViewModel : ObservableObject
    {
        public DeleteItemViewModel(FileViewModel file) => File = file;
        public FileViewModel File { get; }
        public string Name => File.Name;
        public bool Succeeded { get; set; }
        [ObservableProperty] private string _status = "DeleteBatch_Pending".GetLocalized();
        [ObservableProperty] private string _errorMessage = string.Empty;
    }
}
