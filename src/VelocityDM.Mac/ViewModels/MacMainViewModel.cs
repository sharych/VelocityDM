using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using VelocityDM.App.Helpers;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.Mac.ViewModels;

public enum MacSidebarFilter
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
    Other
}

public sealed record SpeedLimitOption(string Label, long BytesPerSecond)
{
    public override string ToString() => Label;
}

public sealed class MacMainViewModel : ObservableObject
{
    private readonly DownloadManagerService _manager;
    private readonly List<MacDownloadItemViewModel> _allItems = new();
    private readonly Dictionary<Guid, MacDownloadItemViewModel> _lookup = new();

    private MacSidebarFilter _activeFilter = MacSidebarFilter.All;
    private string _searchQuery = string.Empty;
    private MacDownloadItemViewModel? _selectedItem;
    private SpeedLimitOption _selectedSpeedLimit;
    private string _statusBarSummary = "Ready";
    private string _totalSpeedText = "0 B/s";
    private string _statusMessage = string.Empty;

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

    public MacMainViewModel(DownloadManagerService manager)
    {
        _manager = manager;

        FilteredItems = new ObservableCollection<MacDownloadItemViewModel>();
        SpeedPresets = new List<SpeedLimitOption>
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
    }

    public ObservableCollection<MacDownloadItemViewModel> FilteredItems { get; }
    public IReadOnlyList<SpeedLimitOption> SpeedPresets { get; }
    public DownloadManagerService Manager => _manager;

    public MacSidebarFilter ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
                RefreshFilteredView();
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
                RefreshFilteredView();
        }
    }

    public MacDownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public SpeedLimitOption SelectedSpeedLimit
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

    public async Task InitializeAsync()
    {
        await _manager.InitializeAsync();
        var settings = _manager.Settings;
        var initialTasks = _manager.GetTasksSnapshot();

        Dispatcher.UIThread.Post(() =>
        {
            _allItems.Clear();
            _lookup.Clear();
            foreach (var task in initialTasks)
            {
                var vm = new MacDownloadItemViewModel(task);
                _lookup[task.Id] = vm;
                _allItems.Add(vm);
            }

            ApplySettingsToViewModel(settings);
            RefreshAggregates();
            RefreshFilteredView();
            if (FilteredItems.Count > 0) SelectedItem = FilteredItems[0];
        });
    }

    public async Task ResumeSelectedAsync(bool forceStart = false)
    {
        if (SelectedItem != null)
            await _manager.StartOrResumeAsync(SelectedItem.Id, forceStart);
    }

    public async Task PauseSelectedAsync()
    {
        if (SelectedItem != null)
            await _manager.PauseAsync(SelectedItem.Id);
    }

    public async Task RemoveSelectedAsync(bool deleteFiles)
    {
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
            FileName = "/usr/bin/open",
            ArgumentList = { SelectedItem.TargetFilePath },
            UseShellExecute = false
        });
    }

    public void OpenSelectedFolder()
    {
        if (SelectedItem == null) return;

        if (File.Exists(SelectedItem.TargetFilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { "-R", SelectedItem.TargetFilePath },
                UseShellExecute = false
            });
        }
        else if (Directory.Exists(SelectedItem.SaveDirectory))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/open",
                ArgumentList = { SelectedItem.SaveDirectory },
                UseShellExecute = false
            });
        }
    }

    public (int ActiveTotal, double TotalDownSpeed) GetBackgroundSummary()
    {
        double speed = _allItems
            .Where(i => i.Status == DownloadStatus.Downloading)
            .Sum(i => i.RawSpeedBytesPerSec);
        return (DownloadingCount, speed);
    }

    private void OnTaskAdded(DownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_lookup.ContainsKey(task.Id))
            {
                var vm = new MacDownloadItemViewModel(task);
                _lookup[task.Id] = vm;
                _allItems.Add(vm);
                RefreshAggregates();
                RefreshFilteredView();
                SelectedItem = vm;
            }
        });
    }

    private void OnTaskUpdated(DownloadTask task)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_lookup.TryGetValue(task.Id, out var vm))
            {
                bool filterChanged = vm.Status != task.Status
                    || vm.Category != task.Category
                    || vm.QueueOrder != task.QueueOrder;

                vm.UpdateFrom(task);
                RefreshAggregates();

                if (filterChanged)
                    RefreshFilteredView();
            }
        });
    }

    private void OnTaskRemoved(Guid taskId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_lookup.Remove(taskId, out var vm))
            {
                bool wasSelected = SelectedItem == vm;
                _allItems.Remove(vm);
                RefreshAggregates();
                RefreshFilteredView();
                if (wasSelected)
                    SelectedItem = FilteredItems.FirstOrDefault();
            }
        });
    }

    private void OnSettingsUpdated(AppSettings settings)
    {
        Dispatcher.UIThread.Post(() => ApplySettingsToViewModel(settings));
    }

    private void ApplySettingsToViewModel(AppSettings settings)
    {
        var match = SpeedPresets.FirstOrDefault(p => p.BytesPerSecond == settings.GlobalSpeedLimitBytesPerSec);
        if (match == null && settings.GlobalSpeedLimitBytesPerSec > 0)
        {
            match = new SpeedLimitOption(ByteFormatter.FormatSpeed(settings.GlobalSpeedLimitBytesPerSec), settings.GlobalSpeedLimitBytesPerSec);
        }

        if (match != null && !Equals(_selectedSpeedLimit, match))
        {
            _selectedSpeedLimit = match;
            OnPropertyChanged(nameof(SelectedSpeedLimit));
        }

        RefreshAggregates();
    }

    private void RefreshFilteredView()
    {
        var desired = _allItems
            .Where(MatchesFilter)
            .OrderBy(i => i.QueueOrder)
            .ToList();

        var previous = SelectedItem;
        FilteredItems.Clear();
        foreach (var item in desired)
        {
            FilteredItems.Add(item);
        }

        if (previous != null && FilteredItems.Contains(previous))
            SelectedItem = previous;
        else if (SelectedItem != null && !FilteredItems.Contains(SelectedItem))
            SelectedItem = FilteredItems.FirstOrDefault();
    }

    private bool MatchesFilter(MacDownloadItemViewModel item)
    {
        bool matchesFilter = ActiveFilter switch
        {
            MacSidebarFilter.All => true,
            MacSidebarFilter.Downloading => item.Status is DownloadStatus.Downloading or DownloadStatus.Probing,
            MacSidebarFilter.Queued => item.Status == DownloadStatus.Queued,
            MacSidebarFilter.Paused => item.Status == DownloadStatus.Paused,
            MacSidebarFilter.Completed => item.Status == DownloadStatus.Completed,
            MacSidebarFilter.Error => item.Status is DownloadStatus.Error or DownloadStatus.Cancelled,
            MacSidebarFilter.Programs => item.Category == FileCategory.Programs,
            MacSidebarFilter.Compressed => item.Category == FileCategory.Compressed,
            MacSidebarFilter.Video => item.Category == FileCategory.Video,
            MacSidebarFilter.Music => item.Category == FileCategory.Music,
            MacSidebarFilter.Documents => item.Category == FileCategory.Documents,
            MacSidebarFilter.Images => item.Category == FileCategory.Images,
            MacSidebarFilter.Other => item.Category == FileCategory.Other,
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

    private void RefreshAggregates()
    {
        int downloading = 0, queued = 0, paused = 0, completed = 0, error = 0;
        int programs = 0, compressed = 0, video = 0, music = 0, docs = 0, images = 0, other = 0;
        double httpSpeed = 0;

        foreach (var item in _allItems)
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

        AllCount = _allItems.Count;
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

        TotalSpeedText = httpSpeed > 0.5 ? ByteFormatter.FormatSpeed(httpSpeed) : "0 B/s";
        var settings = _manager.Settings;
        StatusBarSummary = $"HTTP: {downloading}/{settings.MaxConcurrentDownloads} active, {queued} queued";
    }
}
