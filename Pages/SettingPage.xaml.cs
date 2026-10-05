using CommunityToolkit.Mvvm.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class SettingPage : Page
    {
        public SettingPage()
        {
            InitializeComponent();
            DataContext = Ioc.Default.GetRequiredService<SettingViewModel>();
        }

        private void StartDiagnostics(object sender, RoutedEventArgs e)
        {
            Services.SafeDiagnostics.Current.BeginSession();
            DiagnosticsStatus.Text = Helpers.ResourceHelper.GetLocalized("Diagnostics_Started");
            DiagnosticsStatus.Visibility = Visibility.Visible;
        }

        private async void ClearReaderCache(object sender, RoutedEventArgs e)
        {
            ClearReaderCacheButton.IsEnabled = false;
            try
            {
                int retained = await Ioc.Default.GetRequiredService<Services.ReaderWorkspace>().ClearCacheAsync();
                ReaderCacheStatus.Text = Helpers.ResourceHelper.GetLocalized(retained == 0 ? "Reader_CacheCleared" : "Reader_CacheRetained");
            }
            catch { ReaderCacheStatus.Text = Helpers.ResourceHelper.GetLocalized("Reader_CacheClearFailed"); }
            finally { ReaderCacheStatus.Visibility = Visibility.Visible; ClearReaderCacheButton.IsEnabled = true; }
        }

        private void StopDiagnostics(object sender, RoutedEventArgs e)
        {
            Services.SafeDiagnostics.Current.EndSession();
            DiagnosticsStatus.Text = Helpers.ResourceHelper.GetLocalized("Diagnostics_Stopped");
            DiagnosticsStatus.Visibility = Visibility.Visible;
        }

        private void CopyDiagnostics(object sender, RoutedEventArgs e)
        {
            try
            {
                var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
                data.SetText(Services.SafeDiagnostics.Current.GetSummary());
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            }
            catch
            {
                DiagnosticsStatus.Text = Helpers.ResourceHelper.GetLocalized("Diagnostics_CopyFailed");
                DiagnosticsStatus.Visibility = Visibility.Visible;
            }
        }
    }
}
