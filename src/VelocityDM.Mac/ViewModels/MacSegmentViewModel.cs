using Avalonia.Media;
using VelocityDM.App.Helpers;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Models;

namespace VelocityDM.Mac.ViewModels;

public sealed class MacSegmentViewModel : ObservableObject
{
    private static readonly IBrush ActiveBrush = Brush.Parse("#10B981");
    private static readonly IBrush CompletedBrush = Brush.Parse("#38BDF8");
    private static readonly IBrush PausedBrush = Brush.Parse("#F59E0B");
    private static readonly IBrush ErrorBrush = Brush.Parse("#EF4444");
    private static readonly IBrush PendingBrush = Brush.Parse("#475569");

    private int _index;
    private string _rangeText = string.Empty;
    private double _progressPercent;
    private string _downloadedText = string.Empty;
    private string _speedText = "—";
    private string _statusText = "Pending";
    private IBrush _statusBrush = PendingBrush;

    public int Index
    {
        get => _index;
        private set => SetProperty(ref _index, value);
    }

    public string Header => $"#{Index + 1}";
    public string RangeText { get => _rangeText; private set => SetProperty(ref _rangeText, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public string ProgressText => $"{ProgressPercent:0.0}%";
    public string DownloadedText { get => _downloadedText; private set => SetProperty(ref _downloadedText, value); }
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public IBrush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

    public void UpdateFrom(SegmentState state)
    {
        if (Index != state.Index)
        {
            Index = state.Index;
            OnPropertyChanged(nameof(Header));
        }

        RangeText = state.EndOffset >= state.StartOffset
            ? $"{ByteFormatter.FormatBytes(state.StartOffset)} – {ByteFormatter.FormatBytes(state.EndOffset)}"
            : "Single Stream";

        if (Math.Abs(ProgressPercent - state.ProgressPercent) > 0.01)
        {
            ProgressPercent = state.ProgressPercent;
            OnPropertyChanged(nameof(ProgressText));
        }

        DownloadedText = state.TotalBytes > 0
            ? $"{ByteFormatter.FormatBytes(state.DownloadedBytes)} / {ByteFormatter.FormatBytes(state.TotalBytes)}"
            : ByteFormatter.FormatBytes(state.DownloadedBytes);

        SpeedText = ByteFormatter.FormatSpeed(state.SpeedBytesPerSec);
        StatusText = state.Status.ToString();
        StatusBrush = state.Status switch
        {
            SegmentStatus.Active => ActiveBrush,
            SegmentStatus.Completed => CompletedBrush,
            SegmentStatus.Paused => PausedBrush,
            SegmentStatus.Error => ErrorBrush,
            _ => PendingBrush
        };
    }
}
