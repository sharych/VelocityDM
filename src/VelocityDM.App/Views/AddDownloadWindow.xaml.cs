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
    private bool _userEditedFolder;
    private bool _userEditedFileName;
    private bool _userEditedCategory;
    private bool _suppressFieldTracking;

    public AddDownloadOptions? ResultOptions { get; private set; }
    public AppSettings? UpdatedSettingsToPersist { get; private set; }

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
        if (!_suppressFieldTracking)
        {
            _userEditedCategory = true;
        }
        if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
        {
            UpdateSaveDirectoryFromCategory();
        }
    }

    private void UrlTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = UrlTextBox.Text.Trim();
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

    private void SaveDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFieldTracking)
            return;

        _userEditedFolder = true;
        var entered = SaveDirTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(entered))
        {
            _baseDirectory = entered;
        }
    }

    private void CreateHierarchyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressFieldTracking)
            return;

        UpdateSaveDirectoryFromCategory();
    }

    private void CreateHierarchy_Click(object sender, RoutedEventArgs e)
    {
        string targetRoot = string.IsNullOrWhiteSpace(_baseDirectory)
            ? SaveDirTextBox.Text.Trim()
            : _baseDirectory;

        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            MessageBox.Show(this, "Please choose a valid folder first.", "VelocityDM", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            _suppressFieldTracking = true;
            if (!_userEditedFileName || string.IsNullOrWhiteSpace(FileNameTextBox.Text))
            {
                FileNameTextBox.Text = probe.FileName;
            }
            if (!_userEditedCategory)
            {
                CategoryComboBox.SelectedItem = probe.SuggestedCategory;
            }
            if (!_userEditedFolder || CreateHierarchyCheckBox.IsChecked == true)
            {
                UpdateSaveDirectoryFromCategory();
            }
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
            CustomFileName: _userEditedFileName && !string.IsNullOrWhiteSpace(FileNameTextBox.Text) ? FileNameTextBox.Text.Trim() : null,
            CustomDirectory: (_userEditedFolder || rememberLocation || useHierarchy) ? customDir : null,
            CategoryOverride: _userEditedCategory && CategoryComboBox.SelectedItem is FileCategory cat ? cat : null,
            Connections: (int)Math.Round(ConnectionsSlider.Value),
            ExpectedSha256: string.IsNullOrWhiteSpace(Sha256TextBox.Text) ? null : Sha256TextBox.Text.Trim(),
            StartImmediately: startImmediately);

        DialogResult = true;
        Close();
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
