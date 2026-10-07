using Microsoft.UI.Xaml.Data;
using System;
using System.IO;

namespace OneDrive_Simple_Management_Tool.Converters
{
    // Maps a file name to a Segoe Fluent Icons glyph by extension.
    public class FileGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language) =>
            Path.GetExtension(value as string ?? "").ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".ico" or ".heic" or ".tif" or ".tiff" => "",
                ".mp4" or ".mkv" or ".mov" or ".avi" or ".wmv" or ".webm" or ".flv" or ".m4v" => "",
                ".mp3" or ".flac" or ".wav" or ".m4a" or ".ogg" or ".aac" or ".wma" => "",
                ".pdf" => "",
                ".epub" or ".mobi" or ".azw3" => "",
                ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" => "",
                ".txt" or ".md" or ".doc" or ".docx" or ".rtf" or ".odt" or ".xls" or ".xlsx" or ".csv" or ".ppt" or ".pptx" => "",
                ".cs" or ".js" or ".ts" or ".py" or ".java" or ".c" or ".cpp" or ".h" or ".go" or ".rs" or ".html" or ".css"
                    or ".json" or ".xml" or ".yml" or ".yaml" or ".sh" or ".ps1" or ".sql" => "",
                _ => ""
            };

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
