using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Windows.Graphics.Imaging;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal static class PreviewImageDecoder
    {
        public static async Task<(double Width, double Height)> ReadSizeAsync(byte[] bytes, bool svg, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var memory = new MemoryStream(bytes);
                if (svg)
                {
                    using var xml = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var root = XDocument.Load(xml).Root;
                    if (root?.Name.LocalName != "svg") throw new PreviewException(PreviewFailure.InvalidContent);
                    double width = Length(root.Attribute("width")?.Value), height = Length(root.Attribute("height")?.Value);
                    string[] viewBox = root.Attribute("viewBox")?.Value.Split([' ', ',', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                    if (viewBox?.Length == 4 && double.TryParse(viewBox[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double vw)
                        && double.TryParse(viewBox[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double vh) && vw > 0 && vh > 0)
                    {
                        if (width <= 0 && height > 0) width = height * vw / vh;
                        if (height <= 0 && width > 0) height = width * vh / vw;
                        if (width <= 0 && height <= 0) { width = vw; height = vh; }
                    }
                    return (Bound(width, 300), Bound(height, 150));
                }
                using var stream = memory.AsRandomAccessStream();
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                if ((ulong)decoder.PixelWidth * decoder.PixelHeight > PreviewOptions.MaxImagePixels)
                    throw new PreviewException(PreviewFailure.TooLarge);
                return (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
            }
            catch (OperationCanceledException) { throw; }
            catch (PreviewException) { throw; }
            catch (Exception exception) { throw new PreviewException(PreviewFailure.InvalidContent, inner: exception); }

            static double Bound(double value, double fallback) => double.IsFinite(value) && value > 0 ? Math.Min(value, 100000) : fallback;
            static double Length(string value)
            {
                if (value == null) return 0;
                value = value.Trim().ToLowerInvariant();
                foreach (var (unit, multiplier) in new[] { ("px", 1d), ("pt", 96d / 72), ("pc", 16d),
                    ("in", 96d), ("cm", 96d / 2.54), ("mm", 96d / 25.4), ("q", 96d / 101.6) })
                {
                    if (value.EndsWith(unit, StringComparison.Ordinal) &&
                        double.TryParse(value[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out double length))
                        return length * multiplier;
                }
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) ? result : 0;
            }
        }

        public static async Task<ImageSource> DecodeAsync(byte[] bytes, bool svg, CancellationToken token, int maxEdge = PreviewOptions.DecodePixelLimit)
        {
            using var memory = new MemoryStream(bytes);
            using var stream = memory.AsRandomAccessStream();
            try
            {
                if (svg)
                {
                    // SVG rendering uses a bounded raster surface as well as the input byte limit.
                    var size = await ReadSizeAsync(bytes, true, token);
                    double factor = maxEdge / Math.Max(size.Width, size.Height);
                    var source = new SvgImageSource
                    {
                        RasterizePixelWidth = size.Width * factor,
                        RasterizePixelHeight = size.Height * factor
                    };
                    var status = await source.SetSourceAsync(stream).AsTask(token);
                    token.ThrowIfCancellationRequested();
                    if (status != SvgImageSourceLoadStatus.Success) throw new PreviewException(PreviewFailure.InvalidContent);
                    return source;
                }
                var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token);
                token.ThrowIfCancellationRequested();
                if ((ulong)decoder.PixelWidth * decoder.PixelHeight > PreviewOptions.MaxImagePixels)
                    throw new PreviewException(PreviewFailure.TooLarge);
                stream.Seek(0);
                var image = new BitmapImage { DecodePixelType = DecodePixelType.Physical };
                if (decoder.PixelWidth >= decoder.PixelHeight)
                    image.DecodePixelWidth = (int)Math.Min(decoder.PixelWidth, maxEdge);
                else
                    image.DecodePixelHeight = (int)Math.Min(decoder.PixelHeight, maxEdge);
                await image.SetSourceAsync(stream).AsTask(token);
                token.ThrowIfCancellationRequested();
                return image;
            }
            catch (OperationCanceledException) { throw; }
            catch (PreviewException) { throw; }
            catch (Exception exception) { throw new PreviewException(PreviewFailure.InvalidContent, inner: exception); }
        }
    }
}
