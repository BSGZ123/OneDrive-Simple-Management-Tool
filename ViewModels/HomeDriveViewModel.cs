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
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsagePercentText))]
        private bool _hasUsage;
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(UsagePercentText), nameof(IsNearlyFull))]
        private double _usagePercent;
        public string UsagePercentText => HasUsage ? (UsagePercent / 100).ToString("P0", CultureInfo.CurrentCulture) : "";
        public bool IsNearlyFull => UsagePercent >= 90;
        // Set only while HasUsage is true, so totals never count an unknown capacity as zero.
        public long? UsedBytes { get; private set; }
        public long? TotalBytes { get; private set; }
        [ObservableProperty] private string _capacityText = "Home_LoadingQuota".GetLocalized();
        [ObservableProperty] private string _remainingText = "";
        [ObservableProperty] private string _errorMessage = "";
        public bool HasError => ErrorMessage.Length > 0;
        partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

        public void Apply(HomeQuota quota)
        {
            long? total = Valid(quota?.Total), used = Valid(quota?.Used), remaining = Valid(quota?.Remaining);
            bool hasUsage = total > 0 && used.HasValue;
            UsedBytes = hasUsage ? used : null;
            TotalBytes = hasUsage ? total : null;
            HasUsage = hasUsage;
            UsagePercent = HasUsage ? Math.Clamp(used.Value * 100d / total.Value, 0, 100) : 0;
            CapacityText = total == null && used == null ? "Home_QuotaUnavailable".GetLocalized() :
                string.Format("Home_Capacity".GetLocalized(), FormatBytes(used), FormatBytes(total));
            RemainingText = remaining.HasValue ? string.Format("Home_Remaining".GetLocalized(), FormatBytes(remaining)) : "";
            ErrorMessage = "";
            IsLoading = false;
        }

        public void Fail(string message)
        {
            UsedBytes = TotalBytes = null;
            HasUsage = false;
            CapacityText = "Home_QuotaUnavailable".GetLocalized();
            RemainingText = "";
            ErrorMessage = message;
            IsLoading = false;
        }

        private static long? Valid(long? value) => value >= 0 ? value : null;

        internal static string FormatBytes(double? value)
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
