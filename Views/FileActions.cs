using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using OneDrive_Simple_Management_Tool.Views.Preview;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Threading;
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
        AddBookmark,
        RemoveBookmark,
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
            AddBookmarkMenu(menu, owner, file);
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

        private static void AddBookmarkMenu(MenuFlyout menu, UserControl owner, FileViewModel file)
        {
            var store = Ioc.Default.GetService<IBookmarkStore>();
            var item = new MenuFlyoutItem { Text = "Bookmarks_Add".GetLocalized(), IsEnabled = store != null };
            FileAction action = FileAction.AddBookmark;
            CancellationTokenSource request = null;
            menu.Items.Add(item);
            menu.Opening += async (_, _) =>
            {
                if (store == null) return;
                request?.Cancel();
                using var current = new CancellationTokenSource();
                request = current;
                item.IsEnabled = false;
                try
                {
                    var bookmarks = await store.LoadAsync(current.Token);
                    if (request != current || owner.DataContext != file) return;
                    bool saved = bookmarks.Any(b => b.Identity == (file.Drive.Provider.HomeAccountId, file.Drive.Provider.DriveId, file.Id));
                    action = saved ? FileAction.RemoveBookmark : FileAction.AddBookmark;
                    item.Text = (saved ? "Bookmarks_Remove" : "Bookmarks_Add").GetLocalized();
                    item.IsEnabled = true;
                }
                catch (OperationCanceledException) when (current.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    if (request == current) file.Drive.ErrorMessage = BookmarkErrors.Message(exception, "Bookmarks_LoadFailed");
                }
                finally { if (request == current) request = null; }
            };
            menu.Closed += (_, _) => { request?.Cancel(); request = null; };
            item.Click += async (_, _) => await ExecuteAsync(action, owner, file);
        }

        public static async Task ExecuteAsync(FileAction action, FrameworkElement owner, FileViewModel file)
        {
            if (file == null || IsDialogOpen(owner.XamlRoot)) return;
            if (file.Drive.IsLoading == Visibility.Visible && !(action == FileAction.Open && file.IsFolder)) return;
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
                            FileType.Text => new TextPreviewView(),
                            FileType.Markdown => new MarkdownPreviewView(),
                            FileType.Image => new ImagePreviewView(),
                            FileType.Media => new MediaPreviewView(),
                            FileType.Pdf => new PdfPreviewView(),
                            _ => null
                        };
                        if (preview != null)
                        {
                            PreviewKind kind = preview switch
                            {
                                TextPreviewView => PreviewKind.Text,
                                MarkdownPreviewView => PreviewKind.Markdown,
                                ImagePreviewView => PreviewKind.Image,
                                MediaPreviewView => PreviewKind.Media,
                                _ => PreviewKind.Pdf
                            };
                            var viewModel = new PreviewViewModel(file.Name,
                                Services.GraphPreviewSource.Create(file.Drive.Provider, file.Id, kind));
                            preview.DataContext = viewModel;
                            try { await ShowDialogAsync(owner, preview); }
                            finally { await viewModel.CloseAsync(); }
                            if (viewModel.DownloadRequested && file.DownloadFileCommand.CanExecute(file.Id))
                                await file.DownloadFileCommand.ExecuteAsync(file.Id);
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
                    case FileAction.AddBookmark:
                    case FileAction.RemoveBookmark:
                        var store = Ioc.Default.GetRequiredService<IBookmarkStore>();
                        file.Drive.ErrorMessage = file.Drive.BookmarkMessage = "";
                        var bookmark = new Bookmark
                        {
                            AccountId = file.Drive.Provider.HomeAccountId, DriveId = file.Drive.Provider.DriveId,
                            ItemId = file.Id, Name = file.Name, DriveName = file.Drive.DisplayName,
                            IsFolder = file.IsFolder, AddedAt = DateTimeOffset.UtcNow
                        };
                        if (action == FileAction.AddBookmark) await store.AddAsync(bookmark);
                        else await store.RemoveAsync(bookmark);
                        file.Drive.BookmarkMessage = (action == FileAction.AddBookmark ? "Bookmarks_Added" : "Bookmarks_Removed").GetLocalized();
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
                file.Drive.ErrorMessage = action is FileAction.AddBookmark or FileAction.RemoveBookmark
                    ? BookmarkErrors.Message(exception, "Bookmarks_SaveFailed") : FileOperationErrors.GetMessage(exception);
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
