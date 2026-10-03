using OneDrive_Simple_Management_Tool.Models;
using System.Threading;
using System.Threading.Tasks;

namespace OneDrive_Simple_Management_Tool.Services
{
    public static class GraphPreviewSource
    {
        public static PreviewContentLoader Create(OneDrive provider, string itemId, PreviewKind kind, PreviewOptions options = null)
        {
            return new(kind, MetadataAsync, token => provider.GetItemContent(itemId, token), options);

            async Task<PreviewMetadata> MetadataAsync(CancellationToken token)
            {
                var item = await provider.GetItem(itemId, token);
                if (item?.Id != itemId || item.File == null || item.Folder != null || item.RemoteItem != null ||
                    (item.ParentReference?.DriveId != null && item.ParentReference.DriveId != provider.DriveId))
                    throw new PreviewException(PreviewFailure.InvalidContent);
                item.AdditionalData.TryGetValue("@microsoft.graph.downloadUrl", out object url);
                return new(item.Size, url?.ToString());
            }
        }
    }
}
