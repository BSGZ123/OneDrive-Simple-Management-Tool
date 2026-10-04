using OneDrive_Simple_Management_Tool.Services;
using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace OneDrive_Simple_Management_Tool.Helpers
{
    public static class AccountConfigurationErrors
    {
        public static string Key(Exception exception) => exception switch
        {
            OperationCanceledException => "Account_Cancelled",
            AccountAuthenticationException auth => "Account_" + auth.Failure,
            ConfigurationException configuration => "Configuration_" + configuration.Failure,
            CryptographicException => "Configuration_Protection",
            IOException or UnauthorizedAccessException => "Configuration_Unavailable",
            HttpRequestException => "Account_Network",
            _ => "Account_Failed"
        };

        public static string Message(Exception exception)
        {
            string key = Key(exception);
            SafeDiagnostics.Current.Record(exception switch
            {
                OperationCanceledException => DiagnosticEvent.Cancelled,
                AccountAuthenticationException => DiagnosticEvent.Authentication,
                _ => DiagnosticEvent.ConfigurationFailure
            }, exception is OperationCanceledException ? DiagnosticLevel.Info : DiagnosticLevel.Warning,
                exception is ConfigurationException configuration ? (int)configuration.Failure :
                exception is AccountAuthenticationException authentication ? (int)authentication.Failure : 0);
            return key.GetLocalized() + " (" + key + ")";
        }
    }
}
