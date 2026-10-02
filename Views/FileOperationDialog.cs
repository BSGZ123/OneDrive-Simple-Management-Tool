using Microsoft.UI.Xaml.Controls;
using OneDrive_Simple_Management_Tool.ViewModels;
using System;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Views
{
    internal static class FileOperationDialog
    {
        public static void Configure(ContentDialog dialog, Func<Task> submit)
        {
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                args.Cancel = true;
                if (dialog.DataContext is not FileOperationViewModel model || !model.CanSubmit) return;
                var deferral = args.GetDeferral();
                try
                {
                    await submit();
                    args.Cancel = !model.HasSucceeded;
                }
                finally
                {
                    deferral.Complete();
                }
            };
            dialog.Closing += (_, args) =>
            {
                if (dialog.DataContext is FileOperationViewModel { IsBusy: true }) args.Cancel = true;
            };
        }
    }
}
