using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.IO;
using System.Net.Http;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class PreviewErrors
    {
        public static PreviewException Classify(Exception exception) => exception switch
        {
            PreviewException preview => preview,
            ApiException api when api.ResponseStatusCode == 401 => new(PreviewFailure.Authentication),
            ApiException api when api.ResponseStatusCode == 403 => new(PreviewFailure.AccessDenied),
            ApiException api when api.ResponseStatusCode == 404 => new(PreviewFailure.NotFound),
            ApiException api when api.ResponseStatusCode == 429 || api.ResponseStatusCode >= 500 => new(PreviewFailure.Network, true),
            MsalException => new(PreviewFailure.Authentication),
            HttpRequestException => new(PreviewFailure.Network, true),
            TimeoutException or OperationCanceledException => new(PreviewFailure.Timeout, true),
            InvalidDataException => new(PreviewFailure.InvalidContent),
            _ => new(PreviewFailure.Unknown)
        };
    }
}
