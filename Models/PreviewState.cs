using System;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum PreviewKind { Markdown, Image, Pdf, Media, Text }
    public enum PreviewState { Preparing, Loading, Retrying, Ready, Empty, Failed, Closed }
    public enum PreviewFailure
    {
        None, Network, Timeout, Authentication, AccessDenied, NotFound,
        TooLarge, InvalidContent, Unsupported, RuntimeUnavailable, Unknown, TextEncoding
    }

    public sealed class PreviewException : Exception
    {
        public PreviewException(PreviewFailure failure, bool recoverable = false, Exception inner = null)
            : base(failure.ToString(), inner)
        {
            Failure = failure;
            Recoverable = recoverable;
        }
        public PreviewFailure Failure { get; }
        public bool Recoverable { get; }
    }

    public sealed record PreviewMetadata(long? Size, string Url);
    public sealed record PreviewContent(PreviewKind Kind, byte[] Bytes = null, string Text = null, Uri Uri = null, string EncodingName = null)
    {
        public bool IsEmpty => Kind is PreviewKind.Markdown or PreviewKind.Text && string.IsNullOrEmpty(Text);
    }

    public sealed class PreviewOptions
    {
        public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
        public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(15);
        public TimeSpan RenderTimeout { get; init; } = TimeSpan.FromSeconds(60);
        public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);
        public int MaxTextBytes { get; init; } = 2 * 1024 * 1024;
        public int MaxImageBytes { get; init; } = 20 * 1024 * 1024;
        public const ulong MaxImagePixels = 40_000_000;
        public const int DecodePixelLimit = 2048;
    }
}
