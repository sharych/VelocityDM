using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.Mac.Views;

public partial class AddDownloadWindow : Window
{
    private readonly DownloadManagerService _manager;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _probeCts;
    private string _baseDirectory;
    private bool _userEditedFolder;
    private bool _userEditedFileName;
    private bool _userEditedCategory;
    private bool _suppressFieldTracking;

    public bool Accepted { get; private set; }
    public AddDownloadOptions? ResultOptions { get; private set; }
    public AppSettings? UpdatedSettingsToPersist { get; private set; }

    public AddDownloadWindow() : this(new DownloadManagerService(), null, false)
    {
    }

    public AddDownloadWindow(DownloadManagerService manager, string? prefillUrl = null, bool bringToFront = false)
    {
        InitializeComponent();
        _manager = manager;
        _settings = manager.Settings;
        _baseDirectory = _settings.DefaultDownloadDirectory;

        _suppressFieldTracking = true;
        CategoryComboBox.ItemsSource = Enum.GetValues<FileCategory>();
        CategoryComboBox.SelectedItem = FileCategory.Other;
        ConnectionsSlider.Value = _settings.DefaultConnectionsPerDownload;
        UpdateConnectionsLabel((int)Math.Round(ConnectionsSlider.Value));
        RememberLocationCheckBox.IsChecked = _settings.RememberCustomDownloadLocation;
        CreateHierarchyCheckBox.IsChecked = _settings.CategorizeIntoSubfolders;
        UpdateSaveDirectoryFromCategory();
        _suppressFieldTracking = false;

        FileNameTextBox.TextChanged += (_, _) =>
        {
            if (!_suppressFieldTracking) _userEditedFileName = true;
        };

        if (bringToFront)
        {
            Opened += (_, _) =>
            {
                Topmost = true;
                Activate();
                Focus();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Topmost = false;
                };
                timer.Start();
            };
        }

        if (!string.IsNullOrWhiteSpace(prefillUrl))
        {
            UrlTextBox.Text = prefillUrl.Trim();
            Opened += async (_, _) => await ProbeCurrentUrlAsync();
        }
        else
        {
            Opened += (_, _) => UrlTextBox.Focus();
        }
    }

    private void ConnectionsSlider_ValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateConnectionsLabel((int)Math.Round(e.NewValue));
    }

    private void UpdateConnectionsLabel(int count)
    {
        if (ConnectionsValueText != null)
        {
            ConnectionsValueText.Text = count == 1 ? "1 connection (single stream)" : $"{count} parallel connections";
        }
    }

    private void CategoryComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_suppressFieldTracking)
            _userEditedCategory = true;

        if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
            UpdateSaveDirectoryFromCategory();
    }

    private void UrlTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        var text = (UrlTextBox.Text ?? string.Empty).Trim();
        if (!_userEditedFileName && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var seg = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
            {
                var guessed = FileCategoryClassifier.SanitizeFileName(Uri.UnescapeDataString(seg));
                _suppressFieldTracking = true;
                FileNameTextBox.Text = guessed;
                if (!_userEditedCategory)
                {
                    CategoryComboBox.SelectedItem = FileCategoryClassifier.Classify(guessed);
                    if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
                        UpdateSaveDirectoryFromCategory();
                }
                _suppressFieldTracking = false;
            }
        }
    }

    private void SaveDirTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressFieldTracking)
            return;

        _userEditedFolder = true;
        string entered = (SaveDirTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(entered))
            _baseDirectory = entered;
    }

    private void CreateHierarchyCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (_suppressFieldTracking)
            return;

        UpdateSaveDirectoryFromCategory();
    }

    private void CreateHierarchy_Click(object? sender, RoutedEventArgs e)
    {
        string targetRoot = string.IsNullOrWhiteSpace(_baseDirectory)
            ? (SaveDirTextBox.Text ?? string.Empty).Trim()
            : _baseDirectory;

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            ProbeInfoTextBlock.Text = "Please choose a valid folder first.";
            return;
        }

        try
        {
            FileCategoryClassifier.CreateCategoryHierarchy(targetRoot);
            _baseDirectory = targetRoot;
            _suppressFieldTracking = true;
            CreateHierarchyCheckBox.IsChecked = true;
            _suppressFieldTracking = false;
            UpdateSaveDirectoryFromCategory();
            ProbeInfoTextBlock.Text = $"Created category folders (Programs, Compressed, Video, Music, Documents, Images, Other, Torrents) in {targetRoot}";
        }
        catch (Exception ex)
        {
            ProbeInfoTextBlock.Text = $"Could not create category folders: {ex.Message}";
        }
    }

    private async void ProbeButton_Click(object? sender, RoutedEventArgs e) =>
        await ProbeCurrentUrlAsync();

    private async Task ProbeCurrentUrlAsync()
    {
        var url = (UrlTextBox.Text ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProbeInfoTextBlock.Text = "Please enter a valid http:// or https:// URL.";
            return;
        }

        _probeCts?.Cancel();
        _probeCts?.Dispose();
        _probeCts = new CancellationTokenSource();
        var token = _probeCts.Token;

        ProbeButton.IsEnabled = false;
        ProbeInfoTextBlock.Text = "Probing server headers and byte-range support...";

        try
        {
            ProbeResult probe = await _manager.ProbeUrlAsync(url, token);
            _suppressFieldTracking = true;
            if (!_userEditedFileName || string.IsNullOrWhiteSpace(FileNameTextBox.Text))
                FileNameTextBox.Text = probe.FileName;
            if (!_userEditedCategory)
                CategoryComboBox.SelectedItem = probe.SuggestedCategory;
            if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
                UpdateSaveDirectoryFromCategory();
            _suppressFieldTracking = false;

            string sizeDesc = probe.TotalBytes is > 0 ? ByteFormatter.FormatBytes(probe.TotalBytes) : "Unknown size";
            string rangeDesc = probe.SupportsRanges
                ? "Multi-segment Range supported ✓ (fast parallel download)"
                : "Single stream only (server does not advertise Range support)";

            ProbeInfoTextBlock.Text = $"{probe.FileName}  •  {sizeDesc}  •  {rangeDesc}";
        }
        catch (OperationCanceledException)
        {
            // Replaced by newer probe
        }
        catch (Exception ex)
        {
            ProbeInfoTextBlock.Text = $"Probe warning: {ex.Message}";
        }
        finally
        {
            ProbeButton.IsEnabled = true;
        }
    }

    private async void BrowseFolder_Click(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Download Folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && folders[0].Path.LocalPath is { Length: > 0 } localPath)
        {
            _userEditedFolder = true;
            _baseDirectory = localPath;
            UpdateSaveDirectoryFromCategory();
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close(false);
    }

    private void DownloadNow_Click(object? sender, RoutedEventArgs e) => AcceptDialog(startImmediately: true);
    private void QueueLater_Click(object? sender, RoutedEventArgs e) => AcceptDialog(startImmediately: false);

    private void AcceptDialog(bool startImmediately)
    {
        var url = (UrlTextBox.Text ?? string.Empty).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProbeInfoTextBlock.Text = "Please enter a valid HTTP or HTTPS download URL.";
            return;
        }

        string fileName = (FileNameTextBox.Text ?? string.Empty).Trim();
        string enteredDir = (SaveDirTextBox.Text ?? string.Empty).Trim();
        string sha = (Sha256TextBox.Text ?? string.Empty).Trim();

        bool useHierarchy = CreateHierarchyCheckBox.IsChecked == true;
        bool rememberLocation = RememberLocationCheckBox.IsChecked == true;
        string rootDir = string.IsNullOrWhiteSpace(_baseDirectory) ? enteredDir : _baseDirectory;

        if (useHierarchy && !string.IsNullOrWhiteSpace(rootDir))
        {
            try
            {
                FileCategoryClassifier.CreateCategoryHierarchy(rootDir);
            }
            catch
            {
                // Ignore hierarchy pre-creation errors; target directory will still be created
            }
        }

        if (rememberLocation && !string.IsNullOrWhiteSpace(rootDir))
        {
            var updated = _settings.Clone();
            updated.RememberCustomDownloadLocation = true;
            updated.DefaultDownloadDirectory = rootDir;
            updated.CategorizeIntoSubfolders = useHierarchy;
            UpdatedSettingsToPersist = updated;
        }
        else if (_settings.RememberCustomDownloadLocation != rememberLocation)
        {
            var updated = _settings.Clone();
            updated.RememberCustomDownloadLocation = rememberLocation;
            UpdatedSettingsToPersist = updated;
        }

        string? customDir = !string.IsNullOrWhiteSpace(enteredDir) ? enteredDir : null;

        ResultOptions = new AddDownloadOptions(
            Url: uri.AbsoluteUri,
            CustomFileName: _userEditedFileName && !string.IsNullOrWhiteSpace(fileName) ? fileName : null,
            CustomDirectory: (_userEditedFolder || rememberLocation || useHierarchy) ? customDir : null,
            CategoryOverride: _userEditedCategory && CategoryComboBox.SelectedItem is FileCategory cat ? cat : null,
            Connections: (int)Math.Round(ConnectionsSlider.Value),
            ExpectedSha256: string.IsNullOrWhiteSpace(sha) ? null : sha,
            StartImmediately: startImmediately);

        Accepted = true;
        Close(true);
    }

    private void UpdateSaveDirectoryFromCategory()
    {
        var cat = CategoryComboBox.SelectedItem is FileCategory c ? c : FileCategory.Other;
        bool useHierarchy = CreateHierarchyCheckBox != null && CreateHierarchyCheckBox.IsChecked == true;
        string root = string.IsNullOrWhiteSpace(_baseDirectory) ? _settings.DefaultDownloadDirectory : _baseDirectory;

        _suppressFieldTracking = true;
        SaveDirTextBox.Text = FileCategoryClassifier.ResolveSaveDirectory(root, cat, useHierarchy);
        _suppressFieldTracking = false;
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        base.OnClosed(e);
    }
}
