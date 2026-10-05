using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OneDrive_Simple_Management_Tool.Models
{
    public enum ReaderState { Idle, Preparing, Loading, Restoring, Ready, Failed, Closing, Closed }

    // Local identities never masquerade as an authenticated OneDrive account.
    public sealed record ReaderIdentity(string Kind, string AccountId, string DriveId, string ItemId);
    public sealed record ReaderLocation(string Cfi = null, double? Fraction = null)
    {
        [JsonIgnore] public bool IsEmpty => Cfi == null && Fraction == null;
    }
    public sealed record ReaderSettings
    {
        public double FontSize { get; init; } = 20;
        public double LineHeight { get; init; } = 1.6;
        public double Width { get; init; } = 720;
        public string Flow { get; init; } = "paginated";
        public string Theme { get; init; } = "light";
        public string Zoom { get; init; } = "fit-page";
    }
    public sealed record ReaderProgress(ReaderIdentity Identity, string ContentVersion, ReaderLocation Location, DateTimeOffset UpdatedAtUtc);
    public sealed record ReaderPreferences(ReaderSettings Settings, DateTimeOffset UpdatedAtUtc);
    public sealed record ReaderTocEntry(string Id, string ParentId, string Label, int Depth)
    {
        public override string ToString() => Label;
    }
    public sealed record ReaderOpenRequest(string LocalPath);
    public sealed class ReaderException(string code) : Exception(code) { public string Code { get; } = code; }
    public sealed record ReaderWireMessage(int Version, string SessionId, string RequestId, string Type, JsonElement Payload);
    public sealed record ReaderOpenPayload(ReaderSettings Settings, ReaderLocation Location);
    public sealed record ReaderTurnPayload(string Direction);
    public sealed record ReaderTocTargetPayload(string Id);
    public sealed record ReaderEmptyPayload;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReaderWireMessage))]
    [JsonSerializable(typeof(ReaderOpenPayload))]
    [JsonSerializable(typeof(ReaderTurnPayload))]
    [JsonSerializable(typeof(ReaderTocTargetPayload))]
    [JsonSerializable(typeof(ReaderEmptyPayload))]
    [JsonSerializable(typeof(ReaderSettings))]
    [JsonSerializable(typeof(ReaderLocation))]
    [JsonSerializable(typeof(List<ReaderProgress>))]
    [JsonSerializable(typeof(ReaderPreferences))]
    internal partial class ReaderJsonContext : JsonSerializerContext { }
}
