using Microsoft.Graph.Models;
using OneDrive_Simple_Management_Tool.Helpers;
using System.Xml.Linq;
using Windows.Storage;

namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    // File rendering/storage pickers are the platform boundary. The drive, mutation
    // and conversion view models and Graph service are the real production sources.
    public sealed class FileViewModel(DriveViewModel drive, DriveItem item)
    {
        public DriveViewModel Drive { get; } = drive;
        public string Id => item.Id;
        public string Name => item.Name;
        public bool IsFolder => item.Folder != null;
        public bool IsImage => !IsFolder && item.Image != null;
        public bool CanConvert => !IsFolder && FileConversionRules.Supports(Name);
    }
}

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class SaveFilePickerHelper
    {
        public static Task<StorageFile> PickAsync(string name, string extension) =>
            throw new InvalidOperationException("Tests must supply an explicit picker.");
    }

    public static class ResourceHelper
    {
        public static string Language { get; set; } = "en-US";
        public static string GetLocalized(this string key) => XDocument
            .Load(Path.Combine(AppContext.BaseDirectory, "Strings", Language, "Resources.resw"))
            .Root!.Elements("data").Single(element => (string)element.Attribute("name") == key.Replace('/', '.'))
            .Element("value")!.Value;
    }
}
