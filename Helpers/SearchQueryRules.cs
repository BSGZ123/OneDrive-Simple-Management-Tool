using System;
using System.Linq;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class SearchQueryRules
    {
        public static bool TryNormalize(string value, out string keyword)
        {
            keyword = value?.Trim();
            // Check the original input too: trimming must not hide pasted control characters.
            return !string.IsNullOrEmpty(keyword) && !value.Any(char.IsControl);
        }

        public static string ForGraph(string value)
        {
            if (!TryNormalize(value, out string keyword)) throw new ArgumentException("Invalid search keyword.", nameof(value));
            // OData string literal escaping precedes the SDK's URI encoding.
            return keyword.Replace("'", "''");
        }
    }
}
