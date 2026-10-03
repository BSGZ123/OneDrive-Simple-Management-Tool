using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Text;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class PreviewTextDecoder
    {
        public static readonly string[] Encodings =
            ["Auto", "UTF-8", "UTF-16 LE", "UTF-16 BE", "UTF-32 LE", "UTF-32 BE", "GB18030", "GBK", "Big5"];

        public static (string Text, string EncodingName) Decode(byte[] bytes, string choice = "Auto")
        {
            string detected = null;
            int preamble = 0;
            // UTF-32 LE must precede UTF-16 LE, which shares the first two bytes.
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) { detected = "UTF-32 LE"; preamble = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) { detected = "UTF-32 BE"; preamble = 4; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { detected = "UTF-8"; preamble = 3; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { detected = "UTF-16 LE"; preamble = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { detected = "UTF-16 BE"; preamble = 2; }

            string name = choice == "Auto" ? detected ?? "UTF-8" : choice;
            int skip = detected == name ? preamble : 0;
            Encoding encoding = name switch
            {
                "UTF-8" => new UTF8Encoding(false, true),
                "UTF-16 LE" => new UnicodeEncoding(false, false, true),
                "UTF-16 BE" => new UnicodeEncoding(true, false, true),
                "UTF-32 LE" => new UTF32Encoding(false, false, true),
                "UTF-32 BE" => new UTF32Encoding(true, false, true),
                "GB18030" => Legacy(54936),
                "GBK" => Legacy(936),
                "Big5" => Legacy(950),
                _ => throw new PreviewException(PreviewFailure.TextEncoding)
            };
            try
            {
                string text = encoding.GetString(bytes, skip, bytes.Length - skip);
                // Unmarked UTF-16/32 and binary files should not look like successful UTF-8 text.
                if (text.IndexOf('\0') >= 0) throw new PreviewException(PreviewFailure.TextEncoding);
                return (text, name);
            }
            catch (DecoderFallbackException exception)
            {
                throw new PreviewException(PreviewFailure.TextEncoding, inner: exception);
            }
        }

        private static Encoding Legacy(int codePage) => CodePagesEncodingProvider.Instance.GetEncoding(
            codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
