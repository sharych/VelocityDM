using System.Collections.ObjectModel;
using System.Windows.Media;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Models;

namespace VelocityDM.App.ViewModels;

public sealed class DownloadItemViewModel : ObservableObject
{
    private static readonly Brush DownloadingBrush = FreezeBrush("#10B981");
    private static readonly Brush QueuedBrush = FreezeBrush("#38BDF8");
    private static readonly Brush PausedBrush = FreezeBrush("#F59E0B");
    private static readonly Brush CompletedBrush = FreezeBrush("#22C55E");
    private static readonly Brush ErrorBrush = FreezeBrush("#EF4444");
    private static readonly Brush ProbingBrush = FreezeBrush("#A855F7");

    private string _fileName = string.Empty;
    private string _url = string.Empty;
    private string? _finalUrl;
    private string _saveDirectory = string.Empty;
    private string _targetFilePath = string.Empty;
    private FileCategory _category;
    private DownloadStatus _status;
    private string _statusDisplay = string.Empty;
    private Brush _statusBrush = QueuedBrush;
    private string _sizeText = "Unknown";
    private string _downloadedText = "0 B";
    private double _progressPercent;
    private string _speedText = "—";
    private string _etaText = "—";
    private string _connectionsText = "8";
    private string _rangeSupportText = "Pending probe";
    private string? _errorMessage;
    private string? _expectedSha256;
    private string? _actualSha256;
    private bool? _hashVerified;
    private int _queueOrder;
    private double _rawSpeedBytesPerSec;

    public DownloadItemViewModel(DownloadTask model)
    {
        Id = model.Id;
        CreatedAt = model.CreatedAt;
        UpdateFrom(model);
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string AddedAtText => CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");

    public ObservableCollection<SegmentViewModel> Segments { get; } = new();

    public string FileName { get => _fileName; private set => SetProperty(ref _fileName, value); }
    public string Url { get => _url; private set => SetProperty(ref _url, value); }
    public string? FinalUrl { get => _finalUrl; private set => SetProperty(ref _finalUrl, value); }
    public string SaveDirectory { get => _saveDirectory; private set => SetProperty(ref _saveDirectory, value); }
    public string TargetFilePath { get => _targetFilePath; private set => SetProperty(ref _targetFilePath, value); }

    public FileCategory Category
    {
        get => _category;
        private set
        {
            if (SetProperty(ref _category, value))
                OnPropertyChanged(nameof(CategoryLabel));
        }
    }

    public string CategoryLabel => Category.ToString();

    public DownloadStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string StatusDisplay { get => _statusDisplay; private set => SetProperty(ref _statusDisplay, value); }
    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }
    public string DownloadedText { get => _downloadedText; private set => SetProperty(ref _downloadedText, value); }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (SetProperty(ref _progressPercent, value))
                OnPropertyChanged(nameof(ProgressText));
        }
    }

    public string ProgressText => $"{ProgressPercent:0.0}%";
    public string SpeedText { get => _speedText; private set => SetProperty(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => SetProperty(ref _etaText, value); }
    public string ConnectionsText { get => _connectionsText; private set => SetProperty(ref _connectionsText, value); }
    public string RangeSupportText { get => _rangeSupportText; private set => SetProperty(ref _rangeSupportText, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public int QueueOrder { get => _queueOrder; private set => SetProperty(ref _queueOrder, value); }
    public double RawSpeedBytesPerSec { get => _rawSpeedBytesPerSec; private set => SetProperty(ref _rawSpeedBytesPerSec, value); }

    public string? ExpectedSha256
    {
        get => _expectedSha256;
        private set
        {
            if (SetProperty(ref _expectedSha256, value))
                OnPropertyChanged(nameof(HashSummaryText));
        }
    }

    public string? ActualSha256
    {
        get => _actualSha256;
        private set
        {
            if (SetProperty(ref _actualSha256, value))
                OnPropertyChanged(nameof(HashSummaryText));
        }
    }

    public bool? HashVerified
    {
        get => _hashVerified;
        private set
        {
            if (SetProperty(ref _hashVerified, value))
                OnPropertyChanged(nameof(HashSummaryText));
        }
    }

    public string HashSummaryText
    {
        get
        {
            if (HashVerified == true)
                return $"Verified ✓ ({ActualSha256?[..Math.Min(16, ActualSha256.Length)]}...)";
            if (HashVerified == false)
                return "Mismatch ✗";
            if (!string.IsNullOrWhiteSpace(ActualSha256))
                return ActualSha256;
            if (!string.IsNullOrWhiteSpace(ExpectedSha256))
                return $"Expected: {ExpectedSha256[..Math.Min(16, ExpectedSha256.Length)]}... (Pending)";
            return "Not computed";
        }
    }

    public void UpdateFrom(DownloadTask model)
    {
        FileName = model.FileName;
        Url = model.Url;
        FinalUrl = model.FinalUrl ?? model.Url;
        SaveDirectory = model.SaveDirectory;
        TargetFilePath = model.TargetFilePath;
        Category = model.Category;
        Status = model.Status;
        QueueOrder = model.QueueOrder;
        RawSpeedBytesPerSec = model.SpeedBytesPerSec;
        ErrorMessage = model.ErrorMessage;
        ExpectedSha256 = model.ExpectedSha256;
        ActualSha256 = model.ActualSha256;
        HashVerified = model.HashVerified;

        SizeText = model.TotalBytes is > 0
            ? ByteFormatter.FormatBytes(model.TotalBytes)
            : (model.Status == DownloadStatus.Completed ? ByteFormatter.FormatBytes(model.DownloadedBytes) : "Unknown");

        DownloadedText = model.TotalBytes is > 0
            ? $"{ByteFormatter.FormatBytes(model.DownloadedBytes)} / {ByteFormatter.FormatBytes(model.TotalBytes)}"
            : ByteFormatter.FormatBytes(model.DownloadedBytes);

        ProgressPercent = model.ProgressPercent;
        SpeedText = ByteFormatter.FormatSpeed(model.SpeedBytesPerSec);
        EtaText = model.Status == DownloadStatus.Downloading ? ByteFormatter.FormatEta(model.EtaSeconds) : "—";

        if (model.Segments.Count > 0)
        {
            int active = model.ActiveSegmentCount;
            ConnectionsText = model.Status == DownloadStatus.Downloading
                ? $"{active} / {model.Segments.Count}"
                : $"{model.Segments.Count} seg";
            RangeSupportText = model.SupportsRanges
                ? $"Parallel Range ({model.Segments.Count} connections)"
                : "Single Stream (No Range)";
        }
        else
        {
            ConnectionsText = $"max {model.MaxConnections}";
            RangeSupportText = "Pending probe";
        }

        StatusDisplay = model.Status switch
        {
            DownloadStatus.Queued => "Queued",
            DownloadStatus.Probing => "Connecting...",
            DownloadStatus.Downloading => "Downloading",
            DownloadStatus.Paused => "Paused",
            DownloadStatus.Completed => "Completed",
            DownloadStatus.Error => "Error",
            DownloadStatus.Cancelled => "Cancelled",
            _ => model.Status.ToString()
        };

        StatusBrush = model.Status switch
        {
            DownloadStatus.Downloading => DownloadingBrush,
            DownloadStatus.Queued => QueuedBrush,
            DownloadStatus.Paused => PausedBrush,
            DownloadStatus.Completed => CompletedBrush,
            DownloadStatus.Error => ErrorBrush,
            DownloadStatus.Probing => ProbingBrush,
            _ => QueuedBrush
        };

        SyncSegments(model.Segments);
    }

    private void SyncSegments(IReadOnlyList<SegmentState> sourceSegments)
    {
        if (Segments.Count != sourceSegments.Count)
        {
            Segments.Clear();
            foreach (var seg in sourceSegments)
            {
                var vm = new SegmentViewModel();
                vm.UpdateFrom(seg);
                Segments.Add(vm);
            }
            return;
        }

        for (int i = 0; i < sourceSegments.Count; i++)
        {
            Segments[i].UpdateFrom(sourceSegments[i]);
        }
    }

    private static Brush FreezeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
