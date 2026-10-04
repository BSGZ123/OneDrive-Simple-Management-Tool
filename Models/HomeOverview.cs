using System;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed record HomeDrive(string AccountId, string DriveId, string DisplayName);

    // Null means the service did not report this value; it must not appear as zero usage.
    public sealed record HomeQuota(long? Total, long? Used, long? Remaining);

    public sealed class HomeOverviewException(string resourceKey) : Exception(resourceKey)
    {
        public string ResourceKey { get; } = resourceKey;
    }
}
