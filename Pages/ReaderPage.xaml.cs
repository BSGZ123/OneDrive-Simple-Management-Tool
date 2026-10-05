using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using CommunityToolkit.Mvvm.DependencyInjection;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels;
using OneDrive_Simple_Management_Tool.Views.Reader;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.System;

namespace OneDrive_Simple_Management_Tool.Pages
{
    public sealed partial class ReaderPage : Page
    {
        private bool _dialogOpen;
        private int _tocCount = -1;
        private readonly ReaderWorkspace _workspace;
        internal ReaderWebViewController Controller { get; private set; }
        public ReaderViewModel ViewModel { get; }
        public ReaderPage() : this(Ioc.Default.GetRequiredService<ReaderWorkspace>()) { }
        public ReaderPage(ApplicationDataPaths paths) : this(new ReaderWorkspace(paths)) { }
        private ReaderPage(ReaderWorkspace workspace)
        {
            _workspace = workspace;
            ViewModel = new ReaderViewModel(workspace.CreateSession(() => Controller = new ReaderWebViewController(WebHost, workspace.Paths)));
            InitializeComponent();
            ViewModel.Session.Changed += RefreshContents;
            ViewModel.Session.ExternalLinkRequested += ConfirmExternalLink;
            ViewModel.Session.BackRequested += GoBack;
            Unloaded += async (_, _) => await CloseAsync();
            SizeChanged += (_, _) => ReaderSplit.DisplayMode = ActualWidth >= 900 ? SplitViewDisplayMode.Inline : SplitViewDisplayMode.Overlay;
        }
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is ReaderOpenRequest request) await OpenAsync(request);
        }
        public Task OpenAsync(ReaderOpenRequest request) => _workspace.OpenAsync(ViewModel.Session, request);
        public Task OpenLocalAsync(string path) => _workspace.OpenAsync(ViewModel.Session, new(path));
        public Task CloseAsync() => ViewModel.Session.CloseAsync();
        private void RefreshContents()
        {
            if (_tocCount == ViewModel.Session.Toc.Count) return;
            _tocCount = ViewModel.Session.Toc.Count;
            ContentsTree.RootNodes.Clear();
            var nodes = new Dictionary<string, TreeViewNode>();
            foreach (var item in ViewModel.Session.Toc)
            {
                var node = new TreeViewNode { Content = item, IsExpanded = item.Depth == 0 };
                nodes.Add(item.Id, node);
                if (item.ParentId != null) nodes[item.ParentId].Children.Add(node); else ContentsTree.RootNodes.Add(node);
            }
        }
        private async void TocInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
        {
            if (args.InvokedItem is TreeViewNode node && node.Content is ReaderTocEntry item) await ViewModel.Session.NavigateAsync(item.Id);
            if (ReaderSplit.DisplayMode == SplitViewDisplayMode.Overlay) ReaderSplit.IsPaneOpen = false;
        }
        private void ContentsClicked(object sender, RoutedEventArgs e) => ReaderSplit.IsPaneOpen = !ReaderSplit.IsPaneOpen;
        private void BackClicked(object sender, RoutedEventArgs e) => GoBack();
        private async void GoBack()
        {
            await CloseAsync();
            if (Frame?.CanGoBack == true) Frame.GoBack();
        }
        private async void ConfirmExternalLink(Uri uri)
        {
            if (_dialogOpen || ViewModel.Session.State != ReaderState.Ready || !ReaderProtocol.ExternalUri(uri.AbsoluteUri, out _)) return;
            _dialogOpen = true;
            string session = ViewModel.Session.SessionId;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Reader_ExternalTitle".GetLocalized(),
                    Content = new TextBlock { Text = uri.AbsoluteUri, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 },
                    PrimaryButtonText = "Reader_OpenBrowser".GetLocalized(), CloseButtonText = "Reader_Cancel".GetLocalized(), DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && session == ViewModel.Session.SessionId && ViewModel.Session.State == ReaderState.Ready)
                    await Launcher.LaunchUriAsync(uri);
            }
            catch { }
            finally { _dialogOpen = false; }
        }
        private async void RestoreBackupClicked(object sender, RoutedEventArgs e) => await RecoverAsync(true);
        private async void RebuildClicked(object sender, RoutedEventArgs e) => await RecoverAsync(false);
        private async Task RecoverAsync(bool backup)
        {
            if (_dialogOpen || ViewModel.Session.State is ReaderState.Closing or ReaderState.Closed) return;
            _dialogOpen = true;
            string session = ViewModel.Session.SessionId;
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = "Reader_SaveOptions.Content".GetLocalized(),
                    Content = (backup ? "Reader_ConfirmRestore" : "Reader_ConfirmRebuild").GetLocalized(),
                    PrimaryButtonText = "Reader_Continue".GetLocalized(), CloseButtonText = "Reader_Cancel".GetLocalized(), DefaultButton = ContentDialogButton.Close
                };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && session == ViewModel.Session.SessionId
                    && ViewModel.Session.State is not (ReaderState.Closing or ReaderState.Closed))
                    await ViewModel.Session.RecoverStorageAsync(backup);
            }
            catch { } // The page may leave its XamlRoot while the dialog is open.
            finally { _dialogOpen = false; }
        }
    }
}
