using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Helpers;
using System;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public abstract partial class FileOperationViewModel : ObservableObject
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSubmit))]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        private bool _isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanSubmit))]
        [NotifyPropertyChangedFor(nameof(CanEdit))]
        private bool _hasSucceeded;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasError))]
        private string _errorMessage = string.Empty;

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);
        public bool CanEdit => !IsBusy && !HasSucceeded;
        public virtual bool CanSubmit => !IsBusy && !HasSucceeded;

        protected async Task RunAsync(DriveViewModel drive, Func<Task> operation)
        {
            if (!CanSubmit) return;
            IsBusy = true;
            ErrorMessage = string.Empty;
            try
            {
                await operation();
                // Never repeat a completed mutation just because refreshing failed.
                HasSucceeded = true;
                if (!await drive.TryRefresh())
                    drive.ErrorMessage = "FileOperation_RefreshFailed".GetLocalized();
            }
            catch (Exception exception)
            {
                ErrorMessage = FileOperationErrors.GetMessage(exception);
            }
            finally
            {
                IsBusy = false;
            }
        }
    }
}
