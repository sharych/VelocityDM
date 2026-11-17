using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Data;
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
    Other
}

public sealed record SpeedLimitPreset(string Label, long BytesPerSecond);

public sealed class MainViewModel : ObservableObject
{
    private readonly DownloadManagerService _manager;
    private readonly Dictionary<Guid, DownloadItemViewModel> _lookup = new();

    private SidebarFilter _activeFilter = SidebarFilter.All;
    private string _searchQuery = string.Empty;
    private DownloadItemViewModel? _selectedItem;
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
    private string _statusMessage = string.Empty;

    public MainViewModel(DownloadManagerService manager)
    {
        _manager = manager;
        Items = new ObservableCollection<DownloadItemViewModel>();
        FilteredItems = CollectionViewSource.GetDefaultView(Items);
        FilteredItems.Filter = FilterPredicate;
        FilteredItems.SortDescriptions.Add(new SortDescription(nameof(DownloadItemViewModel.QueueOrder), ListSortDirection.Ascending));

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
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; }
    public ICollectionView FilteredItems { get; }
    public IReadOnlyList<SpeedLimitPreset> SpeedPresets { get; }

    public SidebarFilter ActiveFilter
    {
        get => _activeFilter;
        set
        {
            if (SetProperty(ref _activeFilter, value))
            {
                FilteredItems.Refresh();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                FilteredItems.Refresh();
            }
        }
    }

    public DownloadItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public string? PendingClipboardUrl
    {
        get => _pendingClipboardUrl;
        set
        {
            if (SetProperty(ref _pendingClipboardUrl, value))
                OnPropertyChanged(nameof(HasClipboardBanner));
        }
    }

    public bool HasClipboardBanner => !string.IsNullOrWhiteSpace(PendingClipboardUrl);

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

    public async Task InitializeAsync()
    {
        await _manager.InitializeAsync();
        var settings = _manager.Settings;
        ApplySettingsToViewModel(settings);

        var initialTasks = _manager.GetTasksSnapshot();
        Application.Current.Dispatcher.Invoke(() =>
        {
            Items.Clear();
            _lookup.Clear();
            foreach (var task in initialTasks)
            {
                var vm = new DownloadItemViewModel(task);
                _lookup[task.Id] = vm;
                Items.Add(vm);
            }

            if (Items.Count > 0)
            {
                SelectedItem = Items[0];
            }

            RefreshAggregates();
            FilteredItems.Refresh();
        });
    }

    public DownloadManagerService Manager => _manager;

    public async Task QuickDownloadClipboardUrlAsync()
    {
        if (string.IsNullOrWhiteSpace(PendingClipboardUrl))
            return;

        var url = PendingClipboardUrl;
        PendingClipboardUrl = null;
        await _manager.AddDownloadAsync(new AddDownloadOptions(url, StartImmediately: true));
    }

    public async Task ResumeSelectedAsync(bool forceStart = false)
    {
        if (SelectedItem == null)
            return;
        await _manager.StartOrResumeAsync(SelectedItem.Id, forceStart);
    }

    public async Task PauseSelectedAsync()
    {
        if (SelectedItem == null)
            return;
        await _manager.PauseAsync(SelectedItem.Id);
    }

    public async Task RemoveSelectedAsync(bool deleteFiles)
    {
        if (SelectedItem == null)
            return;
        await _manager.RemoveAsync(SelectedItem.Id, deleteFiles);
    }

    public async Task MoveSelectedAsync(int delta)
    {
        if (SelectedItem == null)
            return;
        await _manager.MoveInQueueAsync(SelectedItem.Id, delta);
    }

    public async Task VerifySelectedSha256Async(string? expectedHash = null)
    {
        if (SelectedItem == null)
            return;

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
        if (SelectedItem == null)
            return;

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

    private void OnTaskAdded(DownloadTask task)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
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
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_lookup.TryGetValue(task.Id, out var vm))
            {
                bool filterRelevantChanged = vm.Status != task.Status
                    || vm.Category != task.Category
                    || vm.QueueOrder != task.QueueOrder;

                vm.UpdateFrom(task);
                RefreshAggregates();

                if (filterRelevantChanged)
                {
                    FilteredItems.Refresh();
                }
            }
        });
    }

    private void OnTaskRemoved(Guid taskId)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_lookup.Remove(taskId, out var vm))
            {
                bool wasSelected = SelectedItem == vm;
                Items.Remove(vm);
                if (wasSelected)
                {
                    SelectedItem = Items.FirstOrDefault();
                }
                RefreshAggregates();
            }
        });
    }

    private void OnSettingsUpdated(AppSettings settings)
    {
        Application.Current.Dispatcher.BeginInvoke(() => ApplySettingsToViewModel(settings));
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
        double totalSpeed = 0;

        foreach (var item in Items)
        {
            switch (item.Status)
            {
                case DownloadStatus.Downloading:
                case DownloadStatus.Probing:
                    downloading++;
                    totalSpeed += item.RawSpeedBytesPerSec;
                    break;
                case DownloadStatus.Queued:
                    queued++;
                    break;
                case DownloadStatus.Paused:
                    paused++;
                    break;
                case DownloadStatus.Completed:
                    completed++;
                    break;
                case DownloadStatus.Error:
                case DownloadStatus.Cancelled:
                    error++;
                    break;
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

        TotalSpeedText = totalSpeed > 0.5 ? ByteFormatter.FormatSpeed(totalSpeed) : "0 B/s";
        var settings = _manager.Settings;
        StatusBarSummary = $"Active: {downloading}/{settings.MaxConcurrentDownloads}  •  Queued: {queued}  •  Completed: {completed}";
    }

    private bool FilterPredicate(object obj)
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
}
