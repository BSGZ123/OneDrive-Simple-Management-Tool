using System.Linq;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class FileNameRules
    {
        public static bool IsValid(string name) =>
            !string.IsNullOrWhiteSpace(name) && name == name.Trim() &&
            name != "." && name != ".." &&
            !name.Any(character => char.IsControl(character) || "\"*:<>?/\\|".Contains(character));
    }
}
