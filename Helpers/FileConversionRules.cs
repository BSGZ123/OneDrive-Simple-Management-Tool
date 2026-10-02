using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class FileConversionRules
    {
        public static IReadOnlyList<string> Extensions { get; } = Array.AsReadOnly(new[]
        {
            // Existing formats supported by Graph's PDF conversion endpoint.
            ".doc", ".docx", ".odp", ".ods", ".odt", ".pps", ".ppsx", ".ppt", ".pptx", ".rtf", ".xls", ".xlsx"
        });

        public static bool Supports(string name) => Extensions.Contains(Path.GetExtension(name)?.ToLowerInvariant());
    }
}
