using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace OneDrive_Simple_Management_Tool.Views.Preview
{
    internal static class PreviewImageDecoder
    {
        public static async Task<ImageSource> DecodeAsync(byte[] bytes, bool svg, CancellationToken token)
        {
            using var memory = new MemoryStream(bytes);
            using var stream = memory.AsRandomAccessStream();
            try
            {
                if (svg)
                {
                    // SVG rendering uses a bounded raster surface as well as the input byte limit.
                    var source = new SvgImageSource
                    {
                        RasterizePixelWidth = PreviewOptions.DecodePixelLimit,
                        RasterizePixelHeight = PreviewOptions.DecodePixelLimit
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
                var image = new BitmapImage();
                if (decoder.PixelWidth >= decoder.PixelHeight)
                    image.DecodePixelWidth = (int)Math.Min(decoder.PixelWidth, PreviewOptions.DecodePixelLimit);
                else
                    image.DecodePixelHeight = (int)Math.Min(decoder.PixelHeight, PreviewOptions.DecodePixelLimit);
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
