using CommunityToolkit.Mvvm.ComponentModel;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using System;
using System.Globalization;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public partial class HomeDriveViewModel(HomeDrive drive) : ObservableObject
    {
        public HomeDrive Drive { get; } = drive;
        public string DisplayName => Drive.DisplayName;
        [ObservableProperty] private bool _isLoading = true;
        [ObservableProperty] private bool _hasUsage;
        [ObservableProperty] private double _usagePercent;
        [ObservableProperty] private string _capacityText = "Home_LoadingQuota".GetLocalized();
        [ObservableProperty] private string _remainingText = "";
        [ObservableProperty] private string _errorMessage = "";
        public bool HasError => ErrorMessage.Length > 0;
        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        public void Apply(HomeQuota quota)
        {
            long? total = Valid(quota?.Total), used = Valid(quota?.Used), remaining = Valid(quota?.Remaining);
            HasUsage = total > 0 && used.HasValue;
            UsagePercent = HasUsage ? Math.Clamp(used.Value * 100d / total.Value, 0, 100) : 0;
            CapacityText = total == null && used == null ? "Home_QuotaUnavailable".GetLocalized() :
                string.Format("Home_Capacity".GetLocalized(), FormatBytes(used), FormatBytes(total));
            RemainingText = remaining.HasValue ? string.Format("Home_Remaining".GetLocalized(), FormatBytes(remaining)) : "";
            ErrorMessage = "";
            IsLoading = false;
        }

        public void Fail(string message)
        {
            HasUsage = false;
            CapacityText = "Home_QuotaUnavailable".GetLocalized();
            RemainingText = "";
            ErrorMessage = message;
            IsLoading = false;
        }

        private static long? Valid(long? value) => value >= 0 ? value : null;

        private static string FormatBytes(long? value)
        {
            if (!value.HasValue) return "Home_Unknown".GetLocalized();
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
            double size = value.Value;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
            return size.ToString(unit == 0 ? "0" : "0.#", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }
}
