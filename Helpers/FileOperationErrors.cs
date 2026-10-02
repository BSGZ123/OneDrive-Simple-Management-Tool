using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using System;
using System.IO;
using System.Net.Http;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class FileOperationErrors
    {
        public static string GetMessage(Exception exception) => (exception switch
        {
            ApiException api when api.ResponseStatusCode == 401 => "FileOperation_AuthenticationFailed",
            ApiException api when api.ResponseStatusCode == 403 => "FileOperation_AccessDenied",
            ApiException api when api.ResponseStatusCode == 404 => "FileOperation_NotFound",
            ApiException api when api.ResponseStatusCode == 409 => "FileOperation_NameConflict",
            ApiException api when api.ResponseStatusCode == 429 => "FileOperation_TooManyRequests",
            MsalException => "FileOperation_AuthenticationFailed",
            HttpRequestException => "FileOperation_NetworkFailed",
            OperationCanceledException => "FileOperation_NetworkFailed",
            InvalidDataException => "FileOperation_InvalidResponse",
            _ => "FileOperation_Failed"
        }).GetLocalized();
    }
}
