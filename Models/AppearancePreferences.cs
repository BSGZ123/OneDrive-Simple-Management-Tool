namespace OneDrive_Simple_Management_Tool.Models
{
    public enum AppearanceTheme { Default, Light, Dark }
    public enum AppearanceMaterial { None, Mica, MicaAlt, Acrylic }

    public sealed record AppearancePreferences
    {
        public int Version { get; init; } = 1;
        public AppearanceTheme Theme { get; init; }
        public AppearanceMaterial Material { get; init; }
    }
}
