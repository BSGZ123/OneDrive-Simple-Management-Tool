using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using OneDrive_Simple_Management_Tool.Helpers;
using OneDrive_Simple_Management_Tool.Models;
using OneDrive_Simple_Management_Tool.Services;
using System;
using System.Diagnostics;
using System.Threading;

namespace OneDrive_Simple_Management_Tool.ViewModels
{
    public sealed class TransferTaskControls : ObservableObject, IDisposable
    {
        private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
        private readonly TransferProgressEstimator _estimator = new();
        private TransferRecord _record;
        private Action<TransferRecord> _save;
        private Timer _timer;
        private bool _active;
        private bool _known;
        private long _remaining;
        private string _speedText = "—";
        private string _remainingText = string.Format("Transfer_Remaining".GetLocalized(), "—");
        public TransferTaskControls(TransferPolicy policy) => Policy = policy;
        public TransferPolicy Policy { get; }
        public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
        public string RemainingText { get => _remainingText; private set => SetProperty(ref _remainingText, value); }
        public int PriorityIndex
        {
            get => Policy.Priority;
            set
            {
                if (value is < 0 or > 2 || value == Policy.Priority) return;
                Policy.Priority = value;
                if (_record != null) { _record.Priority = value; _save(_record); }
                OnPropertyChanged();
            }
        }
        public double SpeedLimitKiB
        {
            get => Policy.BytesPerSecond / 1024;
            set
            {
                if (!double.IsFinite(value) || value < 0 || value > TransferPolicy.MaximumKiBPerSecond)
                {
                    OnPropertyChanged();
                    return;
                }
                int limit = (int)Math.Round(value);
                if (limit * 1024L == Policy.BytesPerSecond) { OnPropertyChanged(); return; }
                Policy.BytesPerSecond = limit * 1024L;
                _estimator.Reset();
                if (_record != null) { _record.SpeedLimitKiB = limit; _save(_record); }
                OnPropertyChanged();
            }
        }

        public void AttachRecord(TransferRecord record, Action<TransferRecord> save)
        {
            _record = record; _save = save;
            Policy.Priority = record.Priority;
            Policy.BytesPerSecond = record.SpeedLimitKiB * 1024L;
            OnPropertyChanged(nameof(PriorityIndex)); OnPropertyChanged(nameof(SpeedLimitKiB));
        }

        public void Update(long bytes, long total, bool active, bool totalKnown, bool completed)
        {
            _remaining = Math.Max(0, total - bytes);
            _known = totalKnown;
            if (active && !_active)
            {
                _active = true;
                _estimator.Reset();
                Tick();
                _timer = new Timer(_ => _dispatcher.TryEnqueue(Tick), null, 500, 500);
            }
            else if (!active)
            {
                Dispose();
                SpeedText = "—";
                RemainingText = string.Format("Transfer_Remaining".GetLocalized(), completed ? "00:00" : "—");
            }
        }

        private void Tick()
        {
            if (!_active) return;
            var estimate = _estimator.Sample(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency,
                Policy.TransferredBytes, _remaining, _known);
            SpeedText = estimate.Speed >= 1024 * 1024 ? $"{estimate.Speed / (1024.0 * 1024):0.0} MiB/s" : $"{estimate.Speed / 1024.0:0.0} KiB/s";
            string remaining = "—";
            if (estimate.Seconds is double seconds)
            {
                var duration = TimeSpan.FromSeconds(Math.Min(Math.Ceiling(seconds), TimeSpan.MaxValue.TotalSeconds - 1));
                remaining = duration.ToString(duration.TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");
            }
            RemainingText = string.Format("Transfer_Remaining".GetLocalized(), remaining);
        }

        public void Dispose()
        {
            _active = false;
            _timer?.Dispose();
            _timer = null;
            _estimator.Reset();
        }
    }
}
