using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class CreateDriveViewModel : ObservableObject
    {
        private readonly CloudViewModel _cloud;
        private CancellationTokenSource _attempt;
        private OneDrive _authenticated;
        private readonly Func<CancellationToken, Task<OneDrive>> _signIn;
        public CreateDriveViewModel(CloudViewModel cloud, Func<CancellationToken, Task<OneDrive>> signIn = null)
        {
            _cloud = cloud;
            _signIn = signIn ?? (async token => { var provider = new OneDrive(); await provider.SignInAsync(token); return provider; });
        }
        [ObservableProperty] private string _displayName;
        [ObservableProperty] private string _errorMessage = "";
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanCreate))]
        private bool _isBusy;
        public bool CanCreate => !IsBusy;
        public void Cancel() => _attempt?.Cancel();

        public async Task<bool> CreateAsync()
        {
            if (IsBusy) return false;
            using var attempt = new CancellationTokenSource();
            _attempt = attempt;
            IsBusy = true;
            ErrorMessage = "";
            try
            {
                // A failed save can be retried without forcing the user through login again.
                if (_authenticated == null)
                {
                    var provider = await _signIn(attempt.Token);
                    attempt.Token.ThrowIfCancellationRequested();
                    _authenticated = provider;
                }
                await _cloud.AddDriveAsync(new DriveViewModel(_authenticated, DisplayName), attempt.Token);
                return true;
            }
            catch (Exception exception)
            {
                ErrorMessage = AccountConfigurationErrors.Message(exception);
                if (exception is ConfigurationException { Failure: ConfigurationFailure.Conflict }) await _cloud.LoadDrivesFromDisk();
                return false;
            }
            finally { _attempt = null; IsBusy = false; }
        }
    }
}
