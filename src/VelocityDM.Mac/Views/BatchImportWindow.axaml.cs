using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Services;

namespace VelocityDM.Mac.Views;

public partial class BatchImportWindow : Window
{
    public sealed class BatchCandidateItem : ObservableObject
    {
        private bool _isSelected = true;
        public string Url { get; init; } = string.Empty;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }

    private readonly DownloadManagerService _manager;
    private readonly List<BatchCandidateItem> _candidates = new();

    public IReadOnlyList<string> SelectedUrls { get; private set; } = Array.Empty<string>();
    public string? CustomDirectory { get; private set; }
    public int Connections { get; private set; } = 8;
    public bool StartImmediately { get; private set; } = true;

    public BatchImportWindow() : this(new DownloadManagerService())
    {
    }

    public BatchImportWindow(DownloadManagerService manager)
    {
        InitializeComponent();
        _manager = manager;
        ConnectionsSlider.Value = manager.Settings.DefaultConnectionsPerDownload;
        Connections = (int)Math.Round(ConnectionsSlider.Value);
        ConnLabel.Text = $"Connections: {Connections}";
        CustomDirTextBox.Text = manager.Settings.DefaultDownloadDirectory;
    }

    private void RawUrlsTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var expanded = BatchLinkParser.ParseAndExpand(RawUrlsTextBox.Text ?? string.Empty);
        _candidates.Clear();
        foreach (var url in expanded)
            _candidates.Add(new BatchCandidateItem { Url = url, IsSelected = true });

        PreviewListBox.ItemsSource = null;
        PreviewListBox.ItemsSource = _candidates;
        PreviewHeaderText.Text = $"Expanded Links Preview ({_candidates.Count})";
    }

    private void SelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _candidates) item.IsSelected = true;
    }

    private void SelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var item in _candidates) item.IsSelected = false;
    }

    private void ConnectionsSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        Connections = (int)Math.Round(e.NewValue);
        if (ConnLabel != null)
            ConnLabel.Text = $"Connections: {Connections}";
    }

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Download Folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } localPath)
        {
            CustomDirTextBox.Text = localPath;
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close(false);
    private void QueuePaused_Click(object? sender, RoutedEventArgs e) => Accept(startImmediately: false);
    private void StartNow_Click(object? sender, RoutedEventArgs e) => Accept(startImmediately: true);

    private void Accept(bool startImmediately)
    {
        var selected = _candidates.Where(c => c.IsSelected).Select(c => c.Url).ToList();
        if (selected.Count == 0)
        {
            PreviewHeaderText.Text = "Select at least one valid HTTP/HTTPS link first";
            return;
        }

        SelectedUrls = selected;
        string enteredDir = (CustomDirTextBox.Text ?? string.Empty).Trim();
        CustomDirectory = string.Equals(enteredDir, _manager.Settings.DefaultDownloadDirectory, StringComparison.Ordinal)
            ? null
            : (string.IsNullOrWhiteSpace(enteredDir) ? null : enteredDir);
        StartImmediately = startImmediately;
        Close(true);
    }
}
