using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class BookmarkErrors
    {
        public static string Message(Exception exception, string fallback) => (exception switch
        {
            BookmarkException bookmark => bookmark.ResourceKey,
            ConfigurationException { Failure: ConfigurationFailure.Conflict } => "Bookmarks_Changed",
            ConfigurationException { Failure: ConfigurationFailure.Invalid or ConfigurationFailure.Version or ConfigurationFailure.Protection or ConfigurationFailure.Missing }
                => "Bookmarks_Damaged",
            _ => fallback
        }).GetLocalized();
    }
}
