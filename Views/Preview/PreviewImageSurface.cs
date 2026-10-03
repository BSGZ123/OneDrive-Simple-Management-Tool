using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using OneDrive_Simple_Management_Tool.ViewModels.Tools;
using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal sealed class PreviewImageSurface : Grid, IDisposable
    {
        private readonly PreviewViewModel _viewModel;
        private readonly bool _svg;
        private byte[] _bytes;
        private readonly Image _image = new() { Stretch = Stretch.Fill };
        private readonly ScrollViewer _scroll = new()
        {
            ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.1f, MaxZoomFactor = 8,
            HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Enabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        private readonly TextBlock _ratio = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 58 };
        private readonly CancellationToken _token;
        private CancellationTokenSource _decode;
        private bool _disposed, _fit = true;
        private double _requestedZoom = 1;
        private double _baseScale = 1;
        private double CurrentZoom => _scroll.ZoomFactor * _baseScale;
        private XamlRoot _root;
        private double _width, _height;
        private int _decodedEdge;
        private Point? _drag;
        private double _dragX, _dragY;

        internal PreviewImageSurface(byte[] bytes, bool svg, PreviewViewModel viewModel, CancellationToken token)
        {
            _bytes = bytes; _svg = svg; _viewModel = viewModel; _token = token;
            RowDefinitions.Add(new() { Height = GridLength.Auto });
            RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            var bar = new CommandBar { Content = _ratio, DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed };
            Add("Preview_ZoomOut", Symbol.ZoomOut, () => Zoom(CurrentZoom / 1.25));
            Add("Preview_ZoomIn", Symbol.ZoomIn, () => Zoom(CurrentZoom * 1.25));
            Add("Preview_Fit", Symbol.FullScreen, () => { _fit = true; Fit(); });
            Add("Preview_Original", Symbol.Refresh, () => Zoom(1));
            Children.Add(bar);
            _scroll.Content = _image;
            Grid.SetRow(_scroll, 1);
            Children.Add(_scroll);
            _scroll.SizeChanged += SizeChangedHandler;
            _scroll.ViewChanged += ViewChanged;
            _scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Wheel), true);
            _image.PointerPressed += PointerPressedHandler;
            _image.PointerMoved += PointerMovedHandler;
            _image.PointerReleased += PointerReleasedHandler;
            _image.PointerCaptureLost += PointerReleasedHandler;
            _image.ImageFailed += ImageFailed;
            Loaded += (_, _) =>
            {
                if (_disposed || _root != null) return;
                _root = XamlRoot;
                _root.Changed += RootChanged;
                if (_fit) Fit();
            };

            void Add(string key, Symbol icon, Action action)
            {
                var button = new AppBarButton { Label = key.GetLocalized(), Icon = new SymbolIcon(icon) };
                ToolTipService.SetToolTip(button, key.GetLocalized());
                button.Click += (_, _) => action();
                bar.PrimaryCommands.Add(button);
            }
        }

        internal async Task LoadAsync()
        {
            (_width, _height) = await PreviewImageDecoder.ReadSizeAsync(_bytes, _svg, _token);
            _token.ThrowIfCancellationRequested();
            _decodedEdge = Math.Min(PreviewOptions.DecodePixelLimit, PreviewImageLayout.DecodeEdge(_width, _height, 1, _svg));
            _image.Source = await PreviewImageDecoder.DecodeAsync(_bytes, _svg, _token, _decodedEdge);
            _token.ThrowIfCancellationRequested();
            Fit();
        }

        private void SizeChangedHandler(object sender, SizeChangedEventArgs args)
        {
            if (_fit) Fit();
            else SetDimensions();
        }

        private double PixelScale => _svg ? 1 : XamlRoot?.RasterizationScale ?? 1;
        private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            SetDimensions();
            if (_fit) Fit();
        }
        private void SetDimensions()
        {
            if (_width <= 0) return;
            _image.Width = _width / PixelScale * _baseScale;
            _image.Height = _height / PixelScale * _baseScale;
        }

        internal void Fit()
        {
            if (_width <= 0 || _disposed) return;
            _fit = true;
            SetDimensions();
            ChangeZoom(PreviewImageLayout.Fit(_width, _height, _scroll.ActualWidth, _scroll.ActualHeight, PixelScale));
        }

        internal void Zoom(double zoom)
        {
            _fit = false;
            ChangeZoom(Math.Clamp(zoom, 0.001, 8));
        }

        private void ChangeZoom(double zoom)
        {
            if (_disposed) return;
            double factor = zoom / CurrentZoom;
            double x = Math.Max(0, (_scroll.HorizontalOffset + _scroll.ViewportWidth / 2) * factor - _scroll.ViewportWidth / 2);
            double y = Math.Max(0, (_scroll.VerticalOffset + _scroll.ViewportHeight / 2) * factor - _scroll.ViewportHeight / 2);
            _requestedZoom = zoom;
            // Native ScrollViewer rejects MinZoomFactor below 0.1. Resize its base canvas
            // for very tall/large images, while retaining the true original-pixel ratio.
            _baseScale = zoom < 0.1 ? zoom * 10 : 1;
            SetDimensions();
            _scroll.ChangeView(x, y, (float)(zoom / _baseScale), true);
            _ratio.Text = $"{zoom:P0}";
            _ = ImproveAsync(zoom);
        }

        private void ViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
        {
            if (_disposed) return;
            _ratio.Text = $"{CurrentZoom:P0}";
            if (!args.IsIntermediate)
            {
                if (Math.Abs(CurrentZoom - _requestedZoom) > 0.001)
                {
                    _fit = false;
                    if (_baseScale != 1) { ChangeZoom(CurrentZoom); return; }
                }
                _ = ImproveAsync(CurrentZoom);
            }
        }

        private async Task ImproveAsync(double zoom)
        {
            if (_disposed || _width <= 0) return;
            int edge = PreviewImageLayout.DecodeEdge(_width, _height, zoom * (_svg ? XamlRoot?.RasterizationScale ?? 1 : 1), _svg);
            _decode?.Cancel();
            _decode?.Dispose();
            _decode = null;
            if (edge <= _decodedEdge) return;
            _decode = CancellationTokenSource.CreateLinkedTokenSource(_token);
            CancellationToken token = _decode.Token;
            try
            {
                await Task.Delay(180, token);
                ImageSource source = await PreviewImageDecoder.DecodeAsync(_bytes, _svg, token, edge);
                token.ThrowIfCancellationRequested();
                if (_disposed) return;
                _image.Source = source;
                _decodedEdge = edge;
                double desired = Math.Max(_width, _height) * Math.Min(zoom * (_svg ? XamlRoot?.RasterizationScale ?? 1 : 1), _svg ? 8 : 1);
                if (edge + 1 < desired)
                    _viewModel.Notice = "Preview_ImageResolutionLimited".GetLocalized();
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!_disposed && !token.IsCancellationRequested) _viewModel.Notice = "Preview_ImageResolutionLimited".GetLocalized();
            }
        }

        private void Wheel(object sender, PointerRoutedEventArgs args)
        {
            if ((InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & CoreVirtualKeyStates.Down) == 0) return;
            Zoom(CurrentZoom * (args.GetCurrentPoint(_scroll).Properties.MouseWheelDelta > 0 ? 1.25 : 0.8));
            args.Handled = true;
        }
        private void PointerPressedHandler(object sender, PointerRoutedEventArgs args)
        {
            var point = args.GetCurrentPoint(_scroll);
            if (!point.Properties.IsLeftButtonPressed || point.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse) return;
            _drag = point.Position; _dragX = _scroll.HorizontalOffset; _dragY = _scroll.VerticalOffset;
            _image.CapturePointer(args.Pointer);
            args.Handled = true;
        }
        private void PointerMovedHandler(object sender, PointerRoutedEventArgs args)
        {
            if (_drag is not Point origin) return;
            Point point = args.GetCurrentPoint(_scroll).Position;
            _scroll.ChangeView(_dragX + origin.X - point.X, _dragY + origin.Y - point.Y, null, true);
            args.Handled = true;
        }
        private void PointerReleasedHandler(object sender, PointerRoutedEventArgs args)
        {
            _drag = null;
            _image.ReleasePointerCaptures();
        }
        private void ImageFailed(object sender, ExceptionRoutedEventArgs args)
        {
            if (!_disposed && !_token.IsCancellationRequested) _viewModel.ReportFailure(PreviewFailure.InvalidContent);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _decode?.Cancel(); _decode?.Dispose();
            _scroll.SizeChanged -= SizeChangedHandler;
            _scroll.ViewChanged -= ViewChanged;
            if (_root != null) _root.Changed -= RootChanged;
            _image.Source = null;
            _scroll.Content = null;
            _bytes = null;
        }
    }
}
