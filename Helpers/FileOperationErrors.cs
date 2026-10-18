using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using System;
using System.IO;
using System.Net.Http;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class FileOperationErrors
    {
        public static string GetMessage(Exception exception) => GetKey(exception).GetLocalized();
        public static string GetKey(Exception exception) => exception switch
        {
            Services.UploadCompletionUnknownException => "Upload_CompletionUnknown",
            Services.AccountAccessException => "Account_RequiresSignIn",
            Services.AccountAuthenticationException auth => "Account_" + auth.Failure,
            ApiException api when api.ResponseStatusCode == 401 => "FileOperation_AuthenticationFailed",
            ApiException api when api.ResponseStatusCode == 403 => "FileOperation_AccessDenied",
            ApiException api when api.ResponseStatusCode == 404 => "FileOperation_NotFound",
            ApiException api when api.ResponseStatusCode == 409 => "FileOperation_NameConflict",
            ApiException api when api.ResponseStatusCode == 429 => "FileOperation_TooManyRequests",
            MsalException => "FileOperation_AuthenticationFailed",
            HttpRequestException http when (int?)http.StatusCode == 401 => "FileOperation_AuthenticationFailed",
            HttpRequestException http when (int?)http.StatusCode == 403 => "FileOperation_AccessDenied",
            HttpRequestException http when (int?)http.StatusCode == 404 => "FileOperation_NotFound",
            HttpRequestException http when (int?)http.StatusCode == 409 => "FileOperation_NameConflict",
            HttpRequestException http when (int?)http.StatusCode == 429 => "FileOperation_TooManyRequests",
            HttpRequestException => "FileOperation_NetworkFailed",
            OperationCanceledException => "FileOperation_NetworkFailed",
            InvalidDataException => "FileOperation_InvalidResponse",
            _ => "FileOperation_Failed"
        };
    }
}
