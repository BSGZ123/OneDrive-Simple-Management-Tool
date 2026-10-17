using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Views
{
    internal static class TransferWindowCloseGuard
    {
        public static void Attach(MainWindow window, TaskManagerViewModel manager, Func<Task> finish)
        {
            bool handlingClose = false;
            bool allowClose = false;
            window.AppWindow.Closing += async (_, args) =>
            {
                if (allowClose) return;
                args.Cancel = true;
                // Let an in-flight move/copy retain its acknowledgement before the app can exit.
                if (FileActions.IsFileTransferBusy(((Microsoft.UI.Xaml.FrameworkElement)window.Content).XamlRoot)) return;
                if (handlingClose) return;
                handlingClose = true;
                try
                {
                    allowClose = await manager.RequestCloseAsync(
                        (uploads, downloads) => TransferDialogs.ConfirmExitAsync(window, uploads, downloads), finish);
                    if (allowClose) window.Close();
                }
                catch (Exception)
                {
                    window.ShowStartupNotice("Transfer_ExitFailed".GetLocalized());
                }
                finally { handlingClose = false; }
            };
        }
    }
}
