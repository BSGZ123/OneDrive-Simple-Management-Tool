using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class ReaderProtocol
    {
        public const long MaximumBookBytes = 100_000_000;
        public const string Origin = "https://reader.invalid";
        public static string PageUrl(string session) => Origin + "/reader/index.html#" + session;
        public static bool IsId(string value) => value is { Length: >= 16 and <= 80 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        public static bool IsTocId(string value) => value is { Length: >= 5 and <= 8 } && value.StartsWith("toc-", StringComparison.Ordinal) && value.AsSpan(4).IndexOfAnyExceptInRange('0', '9') < 0;
        public static bool InRange(double number, double min, double max) => double.IsFinite(number) && number >= min && number <= max;
        public static bool ValidLocation(ReaderLocation location) => location != null &&
            (location.Cfi == null || location.Cfi is { Length: > 9 and <= 4096 } && location.Cfi.StartsWith("epubcfi(", StringComparison.Ordinal) && location.Cfi.EndsWith(')')) &&
            (location.Fraction == null || InRange(location.Fraction.Value, 0, 1));
        public static bool ValidSettings(ReaderSettings settings) => settings != null && InRange(settings.FontSize, 12, 40)
            && InRange(settings.LineHeight, 1, 2.5) && InRange(settings.Width, 320, 1400)
            && settings.Flow is "paginated" or "scrolled" && settings.Theme is "light" or "dark" or "sepia"
            && settings.Zoom is "fit-page" or "fit-width";
        public static bool ValidIdentity(ReaderIdentity identity) => identity != null && (identity.Kind == "local"
            ? identity.AccountId == null && identity.DriveId == null && IsHash(identity.ItemId)
            : identity.Kind == "onedrive" && ValidText(identity.AccountId, 2048) && ValidText(identity.DriveId, 2048) && ValidText(identity.ItemId, 2048));
        public static bool ValidText(string text, int maximum) => !string.IsNullOrWhiteSpace(text) && text.Length <= maximum;
        public static bool IsHash(string text) => text?.Length == 64 && text.All(Uri.IsHexDigit);
        public static bool ExternalUri(string value, out Uri uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
            && value.Length <= 2048 && uri.Scheme is "https" or "http" && uri.UserInfo.Length == 0;
        public static bool FrameUri(string value) => value == "about:blank" || value.StartsWith("blob:" + Origin + "/", StringComparison.Ordinal)
            && Guid.TryParse(value[(value.LastIndexOf('/') + 1)..].Split('#')[0], out _);

        public static bool Fields(JsonElement value, params string[] allowed)
        {
            if (value.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return value.EnumerateObject().All(p => seen.Add(p.Name) && allowed.Contains(p.Name, StringComparer.Ordinal));
        }
        public static string String(JsonElement value, string name) => value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
        public static bool Integer(JsonElement value, string name, int min, int max, out int number)
        {
            number = 0;
            return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out number) && number >= min && number <= max;
        }
        public static ReaderLocation Location(JsonElement payload)
        {
            if (!Fields(payload, "cfi", "fraction")) return null;
            var location = payload.Deserialize(ReaderJsonContext.Default.ReaderLocation);
            return ValidLocation(location) ? location : null;
        }
        public static ReaderWireMessage Parse(string source, string json, string session)
        {
            if (source != PageUrl(session) || json == null || json.Length > 32768 || Encoding.UTF8.GetByteCount(json) > 32768) return null;
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 20 });
                if (!Fields(document.RootElement, "version", "sessionId", "requestId", "type", "payload")) return null;
                var message = document.RootElement.Deserialize(ReaderJsonContext.Default.ReaderWireMessage);
                return message?.Version == 1 && message.SessionId == session && message.Payload.ValueKind == JsonValueKind.Object
                    && (message.RequestId == null || IsId(message.RequestId)) ? message : null;
            }
            catch (JsonException) { return null; }
        }
        public static string Command<T>(string session, string request, string type, T payload, JsonTypeInfo<T> info) =>
            JsonSerializer.Serialize(new ReaderWireMessage(1, session, request, type, JsonSerializer.SerializeToElement(payload, info)),
                ReaderJsonContext.Default.ReaderWireMessage);
    }
}
