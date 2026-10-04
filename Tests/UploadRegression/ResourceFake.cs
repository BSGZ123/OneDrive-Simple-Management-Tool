using System.Xml.Linq;

namespace OneDrive_Simple_Management_Tool.Helpers;

// Localization is a platform boundary; resolve the actual production resource strings.
public static class ResourceHelper
{
    public static string GetLocalized(this string key) => XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Strings", "en-US", "Resources.resw"))
        .Root.Elements("data").Single(e => (string)e.Attribute("name") == key).Element("value").Value;
}
