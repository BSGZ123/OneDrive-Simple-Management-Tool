using OneDrive_Simple_Management_Tool.ViewModels;

namespace OneDrive_Simple_Management_Tool.Models
{
    public sealed record DriveNavigationRequest(DriveViewModel Drive, BookmarkLocation Location);
}
