using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VelocityDM.App.Helpers;
using VelocityDM.Core.Models;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Views;

public partial class AddDownloadWindow : Window
{
    private readonly DownloadManagerService _manager;
    private readonly AppSettings _settings;
    private CancellationTokenSource? _probeCts;
    private string _baseDirectory;
    private string _lastAutoPopulatedSaveDir = string.Empty;
    private string _lastAutoPopulatedFileName = string.Empty;
    private bool _userEditedFolder;
    private bool _userEditedCategory;
    private bool _suppressUpdates;

    public bool ProbeResolvedFileName { get; private set; }
    public AddDownloadOptions? ResultOptions { get; private set; }
    public AppSettings? UpdatedSettingsToPersist { get; private set; }

    public AddDownloadWindow(DownloadManagerService manager, string? prefillUrl = null, bool bringToFront = false)
    {
        InitializeComponent();
        _manager = manager;
        _settings = manager.Settings;
        _baseDirectory = FileCategoryClassifier.SanitizeCorruptedCategoryNesting(_settings.DefaultDownloadDirectory);

        _suppressUpdates = true;
        CategoryComboBox.ItemsSource = Enum.GetValues<FileCategory>();
        CategoryComboBox.SelectedItem = FileCategory.Other;
        ConnectionsSlider.Value = _settings.DefaultConnectionsPerDownload;
        RememberLocationCheckBox.IsChecked = _settings.RememberCustomDownloadLocation;
        CreateHierarchyCheckBox.IsChecked = _settings.CategorizeIntoSubfolders;
        UpdateSaveDirectoryFromCategory();
        _suppressUpdates = false;

        if (bringToFront)
        {
            Loaded += (_, _) =>
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
            Loaded += async (_, _) => await ProbeCurrentUrlAsync();
        }
        else
        {
            Loaded += (_, _) => UrlTextBox.Focus();
        }
    }

    private void ConnectionsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConnectionsValueText != null)
        {
            int count = (int)Math.Round(e.NewValue);
            ConnectionsValueText.Text = count == 1 ? "1 connection (single stream)" : $"{count} parallel connections";
        }
    }

    private void CategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressUpdates)
        {
            _userEditedCategory = true;
        }
        SyncManualFolderEdit();
        if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
        {
            UpdateSaveDirectoryFromCategory();
        }
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = UrlTextBox.Text.Trim();
        if (!HasUserEditedFileName() && Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var seg = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(seg) && seg != "/")
            {
                var guessed = FileCategoryClassifier.SanitizeFileName(Uri.UnescapeDataString(seg));
                SetAutoFileName(guessed);
                if (!_userEditedCategory)
                {
                    _suppressUpdates = true;
                    CategoryComboBox.SelectedItem = FileCategoryClassifier.Classify(guessed);
                    _suppressUpdates = false;
                    if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
                        UpdateSaveDirectoryFromCategory();
                }
            }
        }
    }

    private void CreateHierarchyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressUpdates)
            return;

        SyncManualFolderEdit();
        UpdateSaveDirectoryFromCategory();
    }

    private void CreateHierarchy_Click(object sender, RoutedEventArgs e)
    {
        SyncManualFolderEdit();
        string targetRoot = _baseDirectory;
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            MessageBox.Show(this, "Please choose a valid folder first.", "VelocityDM", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            FileCategoryClassifier.CreateCategoryHierarchy(targetRoot);
            _suppressUpdates = true;
            CreateHierarchyCheckBox.IsChecked = true;
            _suppressUpdates = false;
            UpdateSaveDirectoryFromCategory();
            ProbeInfoTextBlock.Text = $"Created category folders (Programs, Compressed, Video, Music, Documents, Images, Other, Torrents) in {targetRoot}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create category folders:\n{ex.Message}", "VelocityDM", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        await ProbeCurrentUrlAsync();
    }

    private async Task ProbeCurrentUrlAsync()
    {
        var url = UrlTextBox.Text.Trim();
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
            if (!HasUserEditedFileName() || string.IsNullOrWhiteSpace(FileNameTextBox.Text))
            {
                SetAutoFileName(probe.FileName);
                ProbeResolvedFileName = true;
            }
            if (!_userEditedCategory)
            {
                _suppressUpdates = true;
                CategoryComboBox.SelectedItem = probe.SuggestedCategory;
                _suppressUpdates = false;
            }
            if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
            {
                UpdateSaveDirectoryFromCategory();
            }

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

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SaveDirTextBox.Text)
            ? SaveDirTextBox.Text
            : (Directory.Exists(_baseDirectory) ? _baseDirectory : _settings.DefaultDownloadDirectory);

        var dialog = new OpenFolderDialog
        {
            Title = "Choose Download Folder",
            InitialDirectory = initial
        };
        if (dialog.ShowDialog(this) == true)
        {
            _userEditedFolder = true;
            _baseDirectory = dialog.FolderName;
            UpdateSaveDirectoryFromCategory();
        }
    }

    private void DownloadNow_Click(object sender, RoutedEventArgs e) => AcceptDialog(startImmediately: true);

    private void QueueLater_Click(object sender, RoutedEventArgs e) => AcceptDialog(startImmediately: false);

    private void AcceptDialog(bool startImmediately)
    {
        var url = UrlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(this, "Please enter a valid HTTP or HTTPS download URL.", "Invalid URL", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SyncManualFolderEdit();

        bool useHierarchy = CreateHierarchyCheckBox.IsChecked == true;
        bool rememberLocation = RememberLocationCheckBox.IsChecked == true;
        string enteredDir = SaveDirTextBox.Text.Trim();
        string rootDir = string.IsNullOrWhiteSpace(_baseDirectory) ? enteredDir : _baseDirectory;

        if (useHierarchy && !string.IsNullOrWhiteSpace(rootDir))
        {
            try
            {
                FileCategoryClassifier.CreateCategoryHierarchy(rootDir);
            }
            catch
            {
                // Ignore hierarchy pre-creation errors; individual target directory will still be created
            }
        }

        bool baseChanged = _userEditedFolder &&
                           !string.IsNullOrWhiteSpace(rootDir) &&
                           !string.Equals(rootDir, _settings.DefaultDownloadDirectory, StringComparison.OrdinalIgnoreCase);

        if ((rememberLocation && (baseChanged || useHierarchy != _settings.CategorizeIntoSubfolders)) ||
            _settings.RememberCustomDownloadLocation != rememberLocation)
        {
            var updated = _settings.Clone();
            updated.RememberCustomDownloadLocation = rememberLocation;
            if (rememberLocation && !string.IsNullOrWhiteSpace(rootDir))
            {
                updated.DefaultDownloadDirectory = rootDir;
                updated.CategorizeIntoSubfolders = useHierarchy;
            }
            UpdatedSettingsToPersist = updated;
        }

        string fileName = FileNameTextBox.Text.Trim();
        string? customDir = (_userEditedFolder || useHierarchy) && !string.IsNullOrWhiteSpace(enteredDir) ? enteredDir : null;

        ResultOptions = new AddDownloadOptions(
            Url: uri.AbsoluteUri,
            CustomFileName: (HasUserEditedFileName() || ProbeResolvedFileName) && !string.IsNullOrWhiteSpace(fileName) ? fileName : null,
            CustomDirectory: customDir,
            CategoryOverride: _userEditedCategory && CategoryComboBox.SelectedItem is FileCategory cat ? cat : null,
            Connections: (int)Math.Round(ConnectionsSlider.Value),
            ExpectedSha256: string.IsNullOrWhiteSpace(Sha256TextBox.Text) ? null : Sha256TextBox.Text.Trim(),
            StartImmediately: startImmediately);

        DialogResult = true;
        Close();
    }

    private void SyncManualFolderEdit()
    {
        string entered = SaveDirTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(entered) &&
            !string.Equals(entered, _lastAutoPopulatedSaveDir, StringComparison.OrdinalIgnoreCase))
        {
            _userEditedFolder = true;
            _baseDirectory = entered;
        }
    }

    private bool HasUserEditedFileName()
    {
        string current = FileNameTextBox.Text.Trim();
        return !string.IsNullOrWhiteSpace(current) &&
               !string.Equals(current, _lastAutoPopulatedFileName, StringComparison.Ordinal);
    }

    private void SetAutoFileName(string name)
    {
        _lastAutoPopulatedFileName = name;
        FileNameTextBox.Text = name;
    }

    private void UpdateSaveDirectoryFromCategory()
    {
        var cat = CategoryComboBox.SelectedItem is FileCategory c ? c : FileCategory.Other;
        bool useHierarchy = CreateHierarchyCheckBox != null && CreateHierarchyCheckBox.IsChecked == true;
        string root = string.IsNullOrWhiteSpace(_baseDirectory) ? _settings.DefaultDownloadDirectory : _baseDirectory;
        string resolved = FileCategoryClassifier.ResolveSaveDirectory(root, cat, useHierarchy);

        _lastAutoPopulatedSaveDir = resolved;
        SaveDirTextBox.Text = resolved;
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeCts?.Cancel();
        _probeCts?.Dispose();
        base.OnClosed(e);
    }
}
