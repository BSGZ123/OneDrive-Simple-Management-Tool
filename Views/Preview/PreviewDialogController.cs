using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Automation;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System;
using System.ComponentModel;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal sealed class PreviewDialogController
    {
        private readonly ContentDialog _dialog;
        private readonly Grid _host;
        private PreviewViewModel _viewModel;
        private bool _started;
        private readonly Grid _layout;
        private XamlRoot _root;

        public PreviewDialogController(ContentDialog dialog, Grid host)
        {
            _dialog = dialog;
            _host = host;
            // The same shell serves every format, including TXT, so responsive behavior stays consistent.
            if (host.Parent is Panel parent) parent.Children.Remove(host);
            dialog.Content = null;
            _layout = new Grid { RowSpacing = 8 };
            for (int i = 0; i < 5; i++)
                _layout.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            var status = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var progress = new ProgressRing { Width = 20, Height = 20 };
            Bind(progress, ProgressRing.IsActiveProperty, nameof(PreviewViewModel.IsLoading));
            var statusText = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            Bind(statusText, TextBlock.TextProperty, nameof(PreviewViewModel.StatusText));
            status.Children.Add(progress);
            status.Children.Add(statusText);
            _layout.Children.Add(status);
            var error = new InfoBar { IsClosable = false, Severity = InfoBarSeverity.Error };
            Bind(error, InfoBar.IsOpenProperty, nameof(PreviewViewModel.HasError));
            Bind(error, InfoBar.MessageProperty, nameof(PreviewViewModel.ErrorMessage));
            Grid.SetRow(error, 1);
            _layout.Children.Add(error);
            Grid.SetRow(host, 3);
            _layout.Children.Add(host);
            var notice = new TextBlock { TextWrapping = TextWrapping.Wrap };
            Bind(notice, TextBlock.TextProperty, nameof(PreviewViewModel.Notice));
            Grid.SetRow(notice, 4);
            _layout.Children.Add(notice);
            dialog.Content = _layout;
            dialog.Resources["ContentDialogMaxWidth"] = 1200d;
            var title = new TextBlock { IsTextSelectionEnabled = true, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2, TextWrapping = TextWrapping.Wrap };
            Bind(title, TextBlock.TextProperty, nameof(PreviewViewModel.FileName));
            dialog.Title = title;
            Bind(dialog, ContentDialog.PrimaryButtonTextProperty, nameof(PreviewViewModel.RetryText));
            Bind(dialog, ContentDialog.PrimaryButtonCommandProperty, nameof(PreviewViewModel.RetryCommand));
            Bind(dialog, ContentDialog.IsPrimaryButtonEnabledProperty, nameof(PreviewViewModel.CanRetry));
            Bind(dialog, ContentDialog.SecondaryButtonTextProperty, nameof(PreviewViewModel.DownloadText));
            Bind(dialog, ContentDialog.CloseButtonTextProperty, nameof(PreviewViewModel.CloseText));
            dialog.DefaultButton = ContentDialogButton.Close;
            dialog.Opened += Opened;
            dialog.Closing += (_, _) => { if (_viewModel != null) _ = _viewModel.CloseAsync(); };
            dialog.Closed += Closed;
            dialog.Unloaded += Unloaded;
            dialog.PrimaryButtonClick += (_, args) => args.Cancel = true;
            dialog.SecondaryButtonClick += (_, _) =>
            {
                if (_viewModel != null) _viewModel.DownloadRequested = true;
            };
        }

        private async void Opened(ContentDialog sender, ContentDialogOpenedEventArgs args)
        {
            if (_started) return;
            _started = true;
            _viewModel = (PreviewViewModel)_dialog.DataContext;
            ToolTipService.SetToolTip((FrameworkElement)_dialog.Title, _viewModel.FileName);
            _root = _dialog.XamlRoot;
            _root.Changed += RootChanged;
            Resize();
            if (_viewModel.IsText)
            {
                var toolbar = CreateTextToolbar();
                Grid.SetRow(toolbar, 2);
                _layout.Children.Add(toolbar);
            }
            var renderer = new PreviewRenderer(_host, _viewModel);
            _viewModel.Configure(renderer.RenderAsync, renderer.Reset);
            await _viewModel.StartAsync();
        }

        private async void Closed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            if (_root != null) _root.Changed -= RootChanged;
            if (_viewModel != null) await _viewModel.CloseAsync();
        }

        private void Unloaded(object sender, RoutedEventArgs args)
        {
            // ContentDialog can transiently unload while moving into its popup or resizing.
            _dialog.DispatcherQueue.TryEnqueue(async () =>
            {
                if (!_started || _dialog.IsLoaded) return;
                if (_root != null) _root.Changed -= RootChanged;
                if (_viewModel != null) await _viewModel.CloseAsync();
            });
        }

        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();

        private void Resize()
        {
            _layout.Width = Math.Max(220, Math.Min(1100, _root.Size.Width - 96));
            _layout.Height = Math.Max(100, Math.Min(780, _root.Size.Height - 220));
        }

        private CommandBar CreateTextToolbar()
        {
            var toolbar = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed };
            var names = PreviewTextDecoder.Encodings.Select(value => value == "Auto" ? "Preview_AutoEncoding".GetLocalized() : value).ToArray();
            var encoding = new ComboBox { Width = 135, ItemsSource = names,
                SelectedIndex = Array.IndexOf(PreviewTextDecoder.Encodings, _viewModel.SelectedEncoding) };
            AutomationProperties.SetName(encoding, "Preview_Encoding".GetLocalized());
            ToolTipService.SetToolTip(encoding, "Preview_EncodingHelp".GetLocalized());
            Bind(encoding, Control.IsEnabledProperty, nameof(PreviewViewModel.CanChangeEncoding));
            encoding.SelectionChanged += async (_, _) =>
            {
                if (encoding.SelectedIndex < 0) return;
                string value = PreviewTextDecoder.Encodings[encoding.SelectedIndex];
                if (value != _viewModel.SelectedEncoding) await _viewModel.ChangeEncodingAsync(value);
            };
            PropertyChangedEventHandler changed = (_, args) =>
            {
                if (args.PropertyName == nameof(PreviewViewModel.SelectedEncoding))
                    encoding.SelectedIndex = Array.IndexOf(PreviewTextDecoder.Encodings, _viewModel.SelectedEncoding);
            };
            _viewModel.PropertyChanged += changed;
            _dialog.Closed += (_, _) => _viewModel.PropertyChanged -= changed;
            toolbar.Content = encoding;
            Add("Preview_FontSmaller", Symbol.Remove, () => _viewModel.ReadingFontSize -= 2);
            Add("Preview_FontLarger", Symbol.Add, () => _viewModel.ReadingFontSize += 2);
            Add("Preview_FontReset", Symbol.FontSize, () => _viewModel.ReadingFontSize = 16);
            var wrap = new AppBarToggleButton { Label = "Preview_Wrap".GetLocalized(), IsChecked = _viewModel.WrapText };
            wrap.Click += (_, _) => _viewModel.WrapText = wrap.IsChecked == true;
            toolbar.SecondaryCommands.Add(wrap);
            if (_viewModel.Kind == PreviewKind.Markdown)
            {
                var source = new AppBarToggleButton { Label = "Preview_Source".GetLocalized(), IsChecked = _viewModel.ShowSource };
                source.Click += (_, _) => _viewModel.ShowSource = source.IsChecked == true;
                toolbar.SecondaryCommands.Add(source);
            }
            return toolbar;

            void Add(string key, Symbol symbol, Action action)
            {
                var button = new AppBarButton { Label = key.GetLocalized(), Icon = new SymbolIcon(symbol) };
                ToolTipService.SetToolTip(button, key.GetLocalized());
                button.Click += (_, _) => action();
                toolbar.PrimaryCommands.Add(button);
            }
        }

        private static void Bind(FrameworkElement target, DependencyProperty property, string path) =>
            target.SetBinding(property, new Binding { Path = new PropertyPath(path), Mode = BindingMode.OneWay });
    }
}
