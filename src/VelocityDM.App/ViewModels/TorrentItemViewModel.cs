using System.Collections.ObjectModel;
using System.Windows.Media;
using MonoTorrent;
using MonoTorrent.Client;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Models;

namespace VelocityDM.App.ViewModels;

public sealed class TorrentFileViewModel : ObservableObject
{
    private string _path = string.Empty;
    private string _fullPath = string.Empty;
    private string _sizeText = string.Empty;
    private double _progressPercent;
    private Priority _priority;

    public string Path { get => _path; private set => SetProperty(ref _path, value); }
    public string FullPath { get => _fullPath; private set => SetProperty(ref _fullPath, value); }
    public string SizeText { get => _sizeText; private set => SetProperty(ref _sizeText, value); }

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

    public Priority Priority
    {
        get => _priority;
        private set
        {
            if (SetProperty(ref _priority, value))
                OnPropertyChanged(nameof(PriorityText));
        }
    }

    public string PriorityText => Priority.ToString();

    public void UpdateFrom(TorrentFileSnapshot file)
    {
        Path = file.Path;
        FullPath = file.FullPath;
        SizeText = ByteFormatter.FormatBytes(file.Length);
        ProgressPercent = file.ProgressPercent;
        Priority = file.Priority;
    }
}

public sealed class TorrentItemViewModel : ObservableObject
{
    private static readonly Brush DownloadingBrush = FreezeBrush("#10B981");
    private static readonly Brush SeedingBrush = FreezeBrush("#38BDF8");
    private static readonly Brush PausedBrush = FreezeBrush("#F59E0B");
    private static readonly Brush ErrorBrush = FreezeBrush("#EF4444");
    private static readonly Brush MetadataBrush = FreezeBrush("#A855F7");

    private string _name = string.Empty;
    private string _infoHashHex = string.Empty;
    private string? _magnetUri;
    private string _saveDirectory = string.Empty;
    private TorrentState _state;
    private string _statusDisplay = string.Empty;
    private Brush _statusBrush = PausedBrush;
    private double _progressPercent;
    private string _sizeSummaryText = string.Empty;
    private string _downloadSpeedText = "—";
    private string _uploadSpeedText = "—";
    private string _peersSummaryText = "0";
    private string _ratioText = "0.00";
    private bool _sequential;
    private string? _errorMessage;
    private long _rawDownloadSpeed;
    private long _rawUploadSpeed;
    private bool _isPaused;

    public TorrentItemViewModel(TorrentStatusSnapshot snapshot)
    {
        Id = snapshot.Id;
        CreatedAt = snapshot.CreatedAt;
        UpdateFrom(snapshot);
    }

    public Guid Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string AddedAtText => CreatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public ObservableCollection<TorrentFileViewModel> Files { get; } = new();

    public string Name { get => _name; private set => SetProperty(ref _name, value); }
    public string InfoHashHex { get => _infoHashHex; private set => SetProperty(ref _infoHashHex, value); }
    public string? MagnetUri { get => _magnetUri; private set => SetProperty(ref _magnetUri, value); }
    public string SaveDirectory { get => _saveDirectory; private set => SetProperty(ref _saveDirectory, value); }
    public TorrentState State { get => _state; private set => SetProperty(ref _state, value); }
    public bool IsPaused { get => _isPaused; private set => SetProperty(ref _isPaused, value); }
    public string StatusDisplay { get => _statusDisplay; private set => SetProperty(ref _statusDisplay, value); }
    public Brush StatusBrush { get => _statusBrush; private set => SetProperty(ref _statusBrush, value); }

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
    public string SizeSummaryText { get => _sizeSummaryText; private set => SetProperty(ref _sizeSummaryText, value); }
    public string DownloadSpeedText { get => _downloadSpeedText; private set => SetProperty(ref _downloadSpeedText, value); }
    public string UploadSpeedText { get => _uploadSpeedText; private set => SetProperty(ref _uploadSpeedText, value); }
    public string PeersSummaryText { get => _peersSummaryText; private set => SetProperty(ref _peersSummaryText, value); }
    public string RatioText { get => _ratioText; private set => SetProperty(ref _ratioText, value); }
    public bool Sequential { get => _sequential; private set => SetProperty(ref _sequential, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public long RawDownloadSpeed { get => _rawDownloadSpeed; private set => SetProperty(ref _rawDownloadSpeed, value); }
    public long RawUploadSpeed { get => _rawUploadSpeed; private set => SetProperty(ref _rawUploadSpeed, value); }

    public void UpdateFrom(TorrentStatusSnapshot snap)
    {
        Name = snap.Name;
        InfoHashHex = snap.InfoHashHex;
        MagnetUri = snap.MagnetUri;
        SaveDirectory = snap.SaveDirectory;
        State = snap.State;
        IsPaused = snap.IsPaused;
        ProgressPercent = snap.ProgressPercent;
        Sequential = snap.Sequential;
        ErrorMessage = snap.ErrorMessage;
        RawDownloadSpeed = snap.DownloadSpeedBytesPerSec;
        RawUploadSpeed = snap.UploadSpeedBytesPerSec;

        SizeSummaryText = snap.TotalBytes > 0
            ? $"{ByteFormatter.FormatBytes(snap.DownloadedBytes)} / {ByteFormatter.FormatBytes(snap.TotalBytes)}"
            : (snap.HasMetadata ? "0 B" : "Fetching metadata...");

        DownloadSpeedText = ByteFormatter.FormatSpeed(snap.DownloadSpeedBytesPerSec);
        UploadSpeedText = ByteFormatter.FormatSpeed(snap.UploadSpeedBytesPerSec);
        PeersSummaryText = $"{snap.ConnectedPeers} ({snap.Seeds}S / {snap.Leechers}L)";

        double ratio = snap.DownloadedBytes > 0 ? (double)snap.TotalUploadedBytes / snap.DownloadedBytes : 0.0;
        RatioText = ratio.ToString("0.00");

        StatusDisplay = snap.State switch
        {
            TorrentState.Downloading => "Downloading",
            TorrentState.Seeding => "Seeding",
            TorrentState.Metadata => "Metadata...",
            TorrentState.Hashing or TorrentState.FetchingHashes => "Hashing...",
            TorrentState.Paused => "Paused",
            TorrentState.Stopped => "Stopped",
            TorrentState.Error => "Error",
            _ => snap.State.ToString()
        };

        StatusBrush = snap.State switch
        {
            TorrentState.Downloading => DownloadingBrush,
            TorrentState.Seeding => SeedingBrush,
            TorrentState.Metadata or TorrentState.Hashing or TorrentState.FetchingHashes => MetadataBrush,
            TorrentState.Error => ErrorBrush,
            _ => PausedBrush
        };

        SyncFiles(snap.Files);
    }

    private void SyncFiles(IReadOnlyList<TorrentFileSnapshot> sourceFiles)
    {
        if (Files.Count != sourceFiles.Count)
        {
            Files.Clear();
            foreach (var f in sourceFiles)
            {
                var vm = new TorrentFileViewModel();
                vm.UpdateFrom(f);
                Files.Add(vm);
            }
            return;
        }

        for (int i = 0; i < sourceFiles.Count; i++)
        {
            Files[i].UpdateFrom(sourceFiles[i]);
        }
    }

    private static Brush FreezeBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
