using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed record Bookmark
    {
        [JsonRequired] public string AccountId { get; init; }
        [JsonRequired] public string DriveId { get; init; }
        [JsonRequired] public string ItemId { get; init; }
        [JsonRequired] public string Name { get; init; }
        [JsonRequired] public string DriveName { get; init; }
        [JsonRequired] public bool IsFolder { get; init; }
        [JsonRequired] public DateTimeOffset AddedAt { get; init; }
        [JsonIgnore] public (string Account, string Drive, string Item) Identity => (AccountId, DriveId, ItemId);
    }

    public sealed record BookmarkLocation(Bookmark Bookmark, string FolderId, string SelectedItemId, string Notice = null);

    public sealed class BookmarkException(string resourceKey) : Exception(resourceKey)
    {
        public string ResourceKey { get; } = resourceKey;
    }

    [JsonSerializable(typeof(List<Bookmark>))]
    internal partial class BookmarkJsonContext : JsonSerializerContext { }
}
