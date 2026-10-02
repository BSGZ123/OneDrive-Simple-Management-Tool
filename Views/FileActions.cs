using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.Views.Preview;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Views
{
    public enum FileAction
    {
        Open,
        Download,
        Delete,
        Convert,
        Share,
        Rename,
        Property
    }

    // Each item owns its menu instance; both layouts use the same actions and dialogs.
    public static class FileActions
    {
        private sealed class DialogState
        {
            public bool IsOpen;
        }
        private static readonly ConditionalWeakTable<XamlRoot, DialogState> Dialogs = new();

        public static bool IsDialogOpen(XamlRoot root) => root != null && Dialogs.GetOrCreateValue(root).IsOpen;

        public static void Attach(UserControl owner)
        {
            owner.DataContextChanged += (_, _) => UpdateMenu(owner);
            owner.Loaded += (_, _) => UpdateMenu(owner);
            owner.DoubleTapped += async (_, args) =>
            {
                args.Handled = true;
                await ExecuteAsync(FileAction.Open, owner, owner.DataContext as FileViewModel);
            };
            owner.RightTapped += (_, _) =>
            {
                if (owner.DataContext is FileViewModel file) file.Drive.SelectedItem = file;
            };
        }

        private static void UpdateMenu(UserControl owner)
        {
            if (owner.DataContext is not FileViewModel file) return;
            MenuFlyout menu = new();
            Add(FileAction.Open, file.CanOpen);
            Add(FileAction.Download, file.IsFile);
            Add(FileAction.Delete);
            if (file.CanConvert) Add(FileAction.Convert);
            Add(FileAction.Share);
            Add(FileAction.Rename);
            Add(FileAction.Property);
            owner.ContextFlyout = menu;

            void Add(FileAction action, bool enabled = true)
            {
                MenuFlyoutItem item = new()
                {
                    Text = ($"FileView_Flyout_{action}/Text").GetLocalized(),
                    IsEnabled = enabled
                };
                item.Click += async (_, _) => await ExecuteAsync(action, owner, file);
                menu.Items.Add(item);
            }
        }

        public static async Task ExecuteAsync(FileAction action, FrameworkElement owner, FileViewModel file)
        {
            if (file == null || IsDialogOpen(owner.XamlRoot) || file.Drive.IsLoading == Visibility.Visible) return;
            file.Drive.SelectedItem = file;
            try
            {
                switch (action)
                {
                    case FileAction.Open when file.IsFolder:
                        await file.Drive.OpenFolder(file);
                        break;
                    case FileAction.Open when file.CanPreview:
                        ContentDialog preview = Services.Utils.GetFileType(Path.GetExtension(file.Name).ToLowerInvariant()) switch
                        {
                            FileType.Markdown => new MarkdownPreviewView(),
                            FileType.Image => new ImagePreviewView(),
                            FileType.Media => new MediaPreviewView(),
                            FileType.Pdf => new PdfPreviewView(),
                            _ => null
                        };
                        if (preview != null)
                        {
                            preview.DataContext = new PreviewViewModel(file);
                            await ShowDialogAsync(owner, preview);
                        }
                        break;
                    case FileAction.Download when file.IsFile:
                        if (file.DownloadFileCommand.CanExecute(file.Id)) await file.DownloadFileCommand.ExecuteAsync(file.Id);
                        break;
                    case FileAction.Delete:
                        await ShowDialogAsync(owner, new DeleteFileView { DataContext = new DeleteFileViewModel(file) });
                        break;
                    case FileAction.Convert when file.CanConvert:
                        await ShowDialogAsync(owner, new ConvertFileFormatView { DataContext = new ConvertFileFormatViewModel(file) });
                        break;
                    case FileAction.Share:
                        await ShowDialogAsync(owner, new ShareFileView(file));
                        break;
                    case FileAction.Rename:
                        await ShowDialogAsync(owner, new RenameFileView { DataContext = new RenameFileViewModel(file.Drive, file) });
                        break;
                    case FileAction.Property:
                        await ShowDialogAsync(owner, new PropertyView { DataContext = new PropertyViewModel(file) });
                        break;
                }
            }
            catch (Exception exception)
            {
                file.Drive.ErrorMessage = FileOperationErrors.GetMessage(exception);
            }
        }

        public static async Task ShowDialogAsync(FrameworkElement owner, ContentDialog dialog)
        {
            if (owner.XamlRoot == null) return;
            var state = Dialogs.GetOrCreateValue(owner.XamlRoot);
            if (state.IsOpen) return;
            state.IsOpen = true;
            try
            {
                dialog.XamlRoot = owner.XamlRoot;
                await dialog.ShowAsync();
            }
            finally
            {
                state.IsOpen = false;
            }
        }

        public static bool CanUseShortcuts(FrameworkElement owner)
        {
            if (owner.XamlRoot == null || IsDialogOpen(owner.XamlRoot)) return false;
            var focused = FocusManager.GetFocusedElement(owner.XamlRoot);
            return focused is not TextBox && focused is not PasswordBox && focused is not RichEditBox && focused is not ComboBox;
        }
    }
}
