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
