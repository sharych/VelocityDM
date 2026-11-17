using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
using MonoTorrent;
using MonoTorrent.Client;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.App.ViewModels;

public enum SidebarFilter
{
    All,
    Downloading,
    Queued,
    Paused,
    Completed,
    Error,
    Programs,
    Compressed,
    Video,
    Music,
    Documents,
    Images,
    Other,
    TorrentsAll,
    TorrentsDownloading,
    TorrentsSeeding,
    TorrentsPaused
}

public sealed record SpeedLimitPreset(string Label, long BytesPerSecond)
{
    public override string ToString() => Label;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly DownloadManagerService _manager;
    private readonly TorrentManagerService _torrentManager;
    private readonly Dictionary<Guid, DownloadItemViewModel> _lookup = new();
    private readonly Dictionary<Guid, TorrentItemViewModel> _torrentLookup = new();

    private SidebarFilter _activeFilter = SidebarFilter.All;
    private string _searchQuery = string.Empty;
    private DownloadItemViewModel? _selectedItem;
    private TorrentItemViewModel? _selectedTorrent;
    private string? _pendingClipboardUrl;
    private bool _clipboardMonitorEnabled;
    private SpeedLimitPreset _selectedSpeedLimit;
    private string _statusBarSummary = "Ready";
    private string _totalSpeedText = "0 B/s";
    private int _allCount;
    private int _downloadingCount;
    private int _queuedCount;
    private int _pausedCount;
    private int _completedCount;
    private int _errorCount;
    private int _programsCount;
    private int _compressedCount;
    private int _videoCount;
    private int _musicCount;
    private int _documentsCount;
    private int _imagesCount;
    private int _otherCount;
    private int _torrentsAllCount;
    private int _torrentsDownloadingCount;
    private int _torrentsSeedingCount;
    private int _torrentsPausedCount;
    private string _statusMessage = string.Empty;

    public MainViewModel(DownloadManagerService manager, TorrentManagerService torrentManager)
    {
        _manager = manager;
        _torrentManager = torrentManager;

        Items = new ObservableCollection<DownloadItemViewModel>();
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = FilterHttpPredicate;
        FilteredItems.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.QueueOrder), ListSortDirection.Ascending));

        Torrents = new ObservableCollection<TorrentItemViewModel>();
        FilteredTorrents = CollectionViewSource.GetDefaultView(Torrents);
        FilteredTorrents.Filter = FilterTorrentPredicate;
        FilteredTorrents.SortDescriptions.Add(new SortDescription(nameof(TorrentItemViewModel.CreatedAt), ListSortDirection.Descending));

        SpeedPresets = new List<SpeedLimitPreset>
        {
            new("Unlimited", 0),
            new("250 KB/s", 250 * 1024L),
            new("500 KB/s", 500 * 1024L),
            new("1 MB/s", 1024 * 1024L),
            new("2 MB/s", 2 * 1024 * 1024L),
            new("5 MB/s", 5 * 1024 * 1024L),
            new("10 MB/s", 10 * 1024 * 1024L),
            new("25 MB/s", 25 * 1024 * 1024L)
        };
        _selectedSpeedLimit = SpeedPresets[0];

        _manager.TaskAdded += OnTaskAdded;
        _manager.TaskUpdated += OnTaskUpdated;
        _manager.TaskRemoved += OnTaskRemoved;
        _manager.SettingsUpdated += OnSettingsUpdated;
        _torrentManager.TorrentsUpdated += OnTorrentsUpdated;
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; }
    public ICollectionView FilteredItems { get; }

    public ObservableCollection<TorrentItemViewModel> Torrents { get; }
    public ICollectionView FilteredTorrents { get; }

    public IReadOnlyList<SpeedLimitPreset> SpeedPresets { get; }
    public DownloadManagerService Manager => _manager;
    public TorrentManagerService TorrentManager => _torrentManager;

    public SidebarFilter ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                OnPropertyChanged(nameof(IsTorrentView));
                OnPropertyChanged(nameof(IsHttpView));
                FilteredItems.Refresh();
                FilteredTorrents.Refresh();
            }
        }
    }

    public bool IsTorrentView => ActiveFilter is SidebarFilter.TorrentsAll
        or SidebarFilter.TorrentsDownloading
        or SidebarFilter.TorrentsSeeding
        or SidebarFilter.TorrentsPaused;

    public bool IsHttpView => !IsTorrentView;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                FilteredItems.Refresh();
                FilteredTorrents.Refresh();
            }
        }
    }

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public TorrentItemViewModel? SelectedTorrent
    {
        get => _selectedTorrent;
        set => SetProperty(ref _selectedTorrent, value);
    }

    public string? PendingClipboardUrl
    {
        get => _pendingClipboardUrl;
        set
        {
            if (SetProperty(ref _pendingClipboardUrl, value))
            {
                OnPropertyChanged(nameof(HasClipboardBanner));
                OnPropertyChanged(nameof(ClipboardBannerLabel));
            }
        }
    }

    public bool HasClipboardBanner => !string.IsNullOrWhiteSpace(PendingClipboardUrl);

    public string ClipboardBannerLabel =>
        PendingClipboardUrl != null && PendingClipboardUrl.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)
            ? "CLIPBOARD MAGNET DETECTED:"
            : "CLIPBOARD LINK DETECTED:";

    public bool ClipboardMonitorEnabled
    {
        get => _clipboardMonitorEnabled;
        set
        {
            if (SetProperty(ref _clipboardMonitorEnabled, value))
            {
                var updated = _manager.Settings;
                if (updated.ClipboardMonitoringEnabled != value)
                {
                    updated.ClipboardMonitoringEnabled = value;
                    _ = _manager.UpdateSettingsAsync(updated);
                }
            }
        }
    }

    public SpeedLimitPreset SelectedSpeedLimit
    {
        get => _selectedSpeedLimit;
        set
        {
            if (value != null && SetProperty(ref _selectedSpeedLimit, value))
            {
                _manager.UpdateGlobalSpeedLimit(value.BytesPerSecond);
            }
        }
    }

    public string StatusBarSummary { get => _statusBarSummary; private set => SetProperty(ref _statusBarSummary, value); }
    public string TotalSpeedText { get => _totalSpeedText; private set => SetProperty(ref _totalSpeedText, value); }
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    public int AllCount { get => _allCount; private set => SetProperty(ref _allCount, value); }
    public int DownloadingCount { get => _downloadingCount; private set => SetProperty(ref _downloadingCount, value); }
    public int QueuedCount { get => _queuedCount; private set => SetProperty(ref _queuedCount, value); }
    public int PausedCount { get => _pausedCount; private set => SetProperty(ref _pausedCount, value); }
    public int CompletedCount { get => _completedCount; private set => SetProperty(ref _completedCount, value); }
    public int ErrorCount { get => _errorCount; private set => SetProperty(ref _errorCount, value); }
    public int ProgramsCount { get => _programsCount; private set => SetProperty(ref _programsCount, value); }
    public int CompressedCount { get => _compressedCount; private set => SetProperty(ref _compressedCount, value); }
    public int VideoCount { get => _videoCount; private set => SetProperty(ref _videoCount, value); }
    public int MusicCount { get => _musicCount; private set => SetProperty(ref _musicCount, value); }
    public int DocumentsCount { get => _documentsCount; private set => SetProperty(ref _documentsCount, value); }
    public int ImagesCount { get => _imagesCount; private set => SetProperty(ref _imagesCount, value); }
    public int OtherCount { get => _otherCount; private set => SetProperty(ref _otherCount, value); }

    public int TorrentsAllCount { get => _torrentsAllCount; private set => SetProperty(ref _torrentsAllCount, value); }
    public int TorrentsDownloadingCount { get => _torrentsDownloadingCount; private set => SetProperty(ref _torrentsDownloadingCount, value); }
    public int TorrentsSeedingCount { get => _torrentsSeedingCount; private set => SetProperty(ref _torrentsSeedingCount, value); }
    public int TorrentsPausedCount { get => _torrentsPausedCount; private set => SetProperty(ref _torrentsPausedCount, value); }

    public async Task InitializeAsync()
    {
        await _manager.InitializeAsync();
        var settings = _manager.Settings;
        ApplySettingsToViewModel(settings);
        await _torrentManager.InitializeAsync(settings);

        var initialTasks = _manager.GetTasksSnapshot();
        var initialTorrents = _torrentManager.GetSnapshots();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Items.Clear();
            _lookup.Clear();
            foreach (var task in initialTasks)
            {
                var vm = new DownloadItemViewModel(task);
                _lookup[task.Id] = vm;
                Items.Add(vm);
            }
            if (Items.Count > 0) SelectedItem = Items[0];

            SyncTorrentsOnUi(initialTorrents);
            RefreshAggregates();
            FilteredItems.Refresh();
            FilteredTorrents.Refresh();
        });
    }

    public async Task QuickDownloadClipboardUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingClipboardUrl))
            return;

        var url = PendingClipboardUrl;
        PendingClipboardUrl = null;
        if (url.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            await _torrentManager.AddMagnetAsync(url);
            ActiveFilter = SidebarFilter.TorrentsAll;
        }
        else
        {
            await _manager.AddDownloadAsync(new AddDownloadOptions(url, StartImmediately: true));
        }
    }

    public async Task ResumeSelectedAsync(bool forceStart = false)
    {
        if (IsTorrentView)
        {
            if (SelectedTorrent != null)
                await _torrentManager.StartOrResumeAsync(SelectedTorrent.Id);
            return;
        }

        if (SelectedItem != null)
            await _manager.StartOrResumeAsync(SelectedItem.Id, forceStart);
    }

    public async Task PauseSelectedAsync()
    {
        if (IsTorrentView)
        {
            if (SelectedTorrent != null)
                await _torrentManager.PauseAsync(SelectedTorrent.Id);
            return;
        }

        if (SelectedItem != null)
            await _manager.PauseAsync(SelectedItem.Id);
    }

    public async Task RemoveSelectedAsync(bool deleteFiles)
    {
        if (IsTorrentView)
        {
            if (SelectedTorrent != null)
                await _torrentManager.RemoveAsync(SelectedTorrent.Id, deleteFiles);
            return;
        }

        if (SelectedItem != null)
            await _manager.RemoveAsync(SelectedItem.Id, deleteFiles);
    }

    public async Task MoveSelectedAsync(int delta)
    {
        if (SelectedItem == null) return;
        await _manager.MoveInQueueAsync(SelectedItem.Id, delta);
    }

    public async Task VerifySelectedSha256Async(string? expectedHash = null)
    {
        if (SelectedItem == null) return;

        StatusMessage = $"Computing SHA-256 for {SelectedItem.FileName}...";
        var hash = await _manager.ComputeAndVerifySha256Async(SelectedItem.Id, expectedHash);
        StatusMessage = hash != null
            ? $"SHA-256: {hash}"
            : "Cannot compute SHA-256 (file not completed or missing).";
    }

    public void OpenSelectedFile()
    {
        if (SelectedItem == null || !File.Exists(SelectedItem.TargetFilePath))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = SelectedItem.TargetFilePath,
            UseShellExecute = true
        });
    }

    public void OpenSelectedFolder()
    {
        if (IsTorrentView)
        {
            if (SelectedTorrent != null && Directory.Exists(SelectedTorrent.SaveDirectory))
                Process.Start("explorer.exe", $"\"{SelectedTorrent.SaveDirectory}\"");
            return;
        }

        if (SelectedItem == null) return;

        if (File.Exists(SelectedItem.TargetFilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{SelectedItem.TargetFilePath}\"");
        }
        else if (Directory.Exists(SelectedItem.SaveDirectory))
        {
            Process.Start("explorer.exe", $"\"{SelectedItem.SaveDirectory}\"");
        }
    }

    public void OpenDefaultDownloadFolder()
    {
        var dir = _manager.Settings.DefaultDownloadDirectory;
        Directory.CreateDirectory(dir);
        Process.Start("explorer.exe", $"\"{dir}\"");
    }

    public (int ActiveTotal, double TotalDownSpeed) GetBackgroundSummary()
    {
        int httpActive = DownloadingCount;
        int torrentActive = TorrentsDownloadingCount;
        double speed = Items.Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.RawSpeedBytesPerSec)
            + Torrents.Sum(t => t.RawDownloadSpeed);
        return (httpActive + torrentActive, speed);
    }

    private void OnTaskAdded(DownloadTask task)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_lookup.ContainsKey(task.Id))
            {
                var vm = new DownloadItemViewModel(task);
                _lookup[task.Id] = vm;
                Items.Add(vm);
                SelectedItem = vm;
                RefreshAggregates();
                FilteredItems.Refresh();
            }
        });
    }

    private void OnTaskUpdated(DownloadTask task)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_lookup.TryGetValue(task.Id, out var vm))
            {
                bool filterRelevantChanged = vm.Status != task.Status
                    || vm.Category != task.Category
                    || vm.QueueOrder != task.QueueOrder;

                vm.UpdateFrom(task);
                RefreshAggregates();

                if (filterRelevantChanged)
                    FilteredItems.Refresh();
            }
        });
    }

    private void OnTaskRemoved(Guid taskId)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_lookup.Remove(taskId, out var vm))
            {
                bool wasSelected = SelectedItem == vm;
                Items.Remove(vm);
                if (wasSelected) SelectedItem = Items.FirstOrDefault();
                RefreshAggregates();
            }
        });
    }

    private void OnTorrentsUpdated(IReadOnlyList<TorrentStatusSnapshot> snapshots)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.BeginInvoke(() =>
        {
            bool filterChanged = SyncTorrentsOnUi(snapshots);
            RefreshAggregates();
            if (filterChanged) FilteredTorrents.Refresh();
        });
    }

    private bool SyncTorrentsOnUi(IReadOnlyList<TorrentStatusSnapshot> snapshots)
    {
        bool structuralOrStateChange = false;
        var incomingIds = new HashSet<Guid>(snapshots.Select(s => s.Id));

        for (int i = Torrents.Count - 1; i >= 0; i--)
        {
            if (!incomingIds.Contains(Torrents[i].Id))
            {
                _torrentLookup.Remove(Torrents[i].Id);
                if (SelectedTorrent == Torrents[i]) SelectedTorrent = null;
                Torrents.RemoveAt(i);
                structuralOrStateChange = true;
            }
        }

        foreach (var snap in snapshots)
        {
            if (_torrentLookup.TryGetValue(snap.Id, out var vm))
            {
                if (vm.State != snap.State) structuralOrStateChange = true;
                vm.UpdateFrom(snap);
            }
            else
            {
                var created = new TorrentItemViewModel(snap);
                _torrentLookup[snap.Id] = created;
                Torrents.Add(created);
                SelectedTorrent ??= created;
                structuralOrStateChange = true;
            }
        }

        if (SelectedTorrent == null && Torrents.Count > 0)
            SelectedTorrent = Torrents[0];

        return structuralOrStateChange;
    }

    private void OnSettingsUpdated(AppSettings settings)
    {
        _ = _torrentManager.UpdateSettingsAsync(settings);
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ApplySettingsToViewModel(settings));
    }

    private void ApplySettingsToViewModel(AppSettings settings)
    {
        if (_clipboardMonitorEnabled != settings.ClipboardMonitoringEnabled)
        {
            _clipboardMonitorEnabled = settings.ClipboardMonitoringEnabled;
            OnPropertyChanged(nameof(ClipboardMonitorEnabled));
        }

        var match = SpeedPresets.FirstOrDefault(p => p.BytesPerSecond == settings.GlobalSpeedLimitBytesPerSec);
        if (match == null && settings.GlobalSpeedLimitBytesPerSec > 0)
        {
            match = new SpeedLimitPreset(ByteFormatter.FormatSpeed(settings.GlobalSpeedLimitBytesPerSec), settings.GlobalSpeedLimitBytesPerSec);
        }

        if (match != null && !Equals(_selectedSpeedLimit, match))
        {
            _selectedSpeedLimit = match;
            OnPropertyChanged(nameof(SelectedSpeedLimit));
        }

        RefreshAggregates();
    }

    private void RefreshAggregates()
    {
        int downloading = 0, queued = 0, paused = 0, completed = 0, error = 0;
        int programs = 0, compressed = 0, video = 0, music = 0, docs = 0, images = 0, other = 0;
        double httpSpeed = 0;

        foreach (var item in Items)
        {
            switch (item.Status)
            {
                case DownloadStatus.Downloading:
                case DownloadStatus.Probing:
                    downloading++;
                    httpSpeed += item.RawSpeedBytesPerSec;
                    break;
                case DownloadStatus.Queued: queued++; break;
                case DownloadStatus.Paused: paused++; break;
                case DownloadStatus.Completed: completed++; break;
                case DownloadStatus.Error:
                case DownloadStatus.Cancelled: error++; break;
            }

            switch (item.Category)
            {
                case FileCategory.Programs: programs++; break;
                case FileCategory.Compressed: compressed++; break;
                case FileCategory.Video: video++; break;
                case FileCategory.Music: music++; break;
                case FileCategory.Documents: docs++; break;
                case FileCategory.Images: images++; break;
                default: other++; break;
            }
        }

        int tDownloading = 0, tSeeding = 0, tPaused = 0;
        double torrentDlSpeed = 0;
        double torrentUlSpeed = 0;
        foreach (var t in Torrents)
        {
            torrentDlSpeed += t.RawDownloadSpeed;
            torrentUlSpeed += t.RawUploadSpeed;
            if (t.State is TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing or TorrentState.FetchingHashes or TorrentState.Starting)
                tDownloading++;
            else if (t.State == TorrentState.Seeding)
                tSeeding++;
            else
                tPaused++;
        }

        AllCount = Items.Count;
        DownloadingCount = downloading;
        QueuedCount = queued;
        PausedCount = paused;
        CompletedCount = completed;
        ErrorCount = error;

        ProgramsCount = programs;
        CompressedCount = compressed;
        VideoCount = video;
        MusicCount = music;
        DocumentsCount = docs;
        ImagesCount = images;
        OtherCount = other;

        TorrentsAllCount = Torrents.Count;
        TorrentsDownloadingCount = tDownloading;
        TorrentsSeedingCount = tSeeding;
        TorrentsPausedCount = tPaused;

        double totalDl = httpSpeed + torrentDlSpeed;
        TotalSpeedText = torrentUlSpeed > 100
            ? $"↓ {ByteFormatter.FormatSpeed(totalDl)}  ↑ {ByteFormatter.FormatSpeed(torrentUlSpeed)}"
            : (totalDl > 0.5 ? ByteFormatter.FormatSpeed(totalDl) : "0 B/s");

        var settings = _manager.Settings;
        StatusBarSummary = $"HTTP: {downloading}/{settings.MaxConcurrentDownloads} active, {queued} queued  •  Torrents: {tDownloading} DL, {tSeeding} seeding";
    }

    private bool FilterHttpPredicate(object obj)
    {
        if (obj is not DownloadItemViewModel item)
            return false;

        bool matchesFilter = ActiveFilter switch
        {
            SidebarFilter.All => true,
            SidebarFilter.Downloading => item.Status is DownloadStatus.Downloading or DownloadStatus.Probing,
            SidebarFilter.Queued => item.Status == DownloadStatus.Queued,
            SidebarFilter.Paused => item.Status == DownloadStatus.Paused,
            SidebarFilter.Completed => item.Status == DownloadStatus.Completed,
            SidebarFilter.Error => item.Status is DownloadStatus.Error or DownloadStatus.Cancelled,
            SidebarFilter.Programs => item.Category == FileCategory.Programs,
            SidebarFilter.Compressed => item.Category == FileCategory.Compressed,
            SidebarFilter.Video => item.Category == FileCategory.Video,
            SidebarFilter.Music => item.Category == FileCategory.Music,
            SidebarFilter.Documents => item.Category == FileCategory.Documents,
            SidebarFilter.Images => item.Category == FileCategory.Images,
            SidebarFilter.Other => item.Category == FileCategory.Other,
            _ => true
        };

        if (!matchesFilter)
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery))
            return true;

        return item.FileName.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
            || item.Url.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
            || item.CategoryLabel.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    private bool FilterTorrentPredicate(object obj)
    {
        if (obj is not TorrentItemViewModel t)
            return false;

        bool matchesFilter = ActiveFilter switch
        {
            SidebarFilter.TorrentsDownloading => t.State is TorrentState.Downloading or TorrentState.Metadata or TorrentState.Hashing or TorrentState.FetchingHashes or TorrentState.Starting,
            SidebarFilter.TorrentsSeeding => t.State == TorrentState.Seeding,
            SidebarFilter.TorrentsPaused => t.State is TorrentState.Paused or TorrentState.Stopped or TorrentState.Error,
            _ => true
        };

        if (!matchesFilter)
            return false;

        if (string.IsNullOrWhiteSpace(SearchQuery))
            return true;

        return t.Name.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase)
            || t.InfoHashHex.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase);
    }
}
