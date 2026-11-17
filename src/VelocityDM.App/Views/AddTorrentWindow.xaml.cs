using System.IO;
using System.Windows;
using Microsoft.Win32;
using VelocityDM.Core.Models;

namespace VelocityDM.App.Views;

public partial class AddTorrentWindow : Window
{
    public string Source { get; private set; } = string.Empty;
    public bool IsTorrentFile { get; private set; }
    public string SaveDirectory { get; private set; } = string.Empty;
    public bool Sequential { get; private set; }
    public bool StartImmediately { get; private set; }

    public AddTorrentWindow(AppSettings settings, string? prefillSource = null)
    {
        InitializeComponent();
        SaveDirTextBox.Text = Path.Combine(settings.DefaultDownloadDirectory, "Torrents");
        if (!string.IsNullOrWhiteSpace(prefillSource))
        {
            SourceTextBox.Text = prefillSource.Trim();
        }
        Loaded += (_, _) => SourceTextBox.Focus();
    }

    private void BrowseTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select .torrent File",
            Filter = "Torrent Files (*.torrent)|*.torrent|All Files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) == true)
        {
            SourceTextBox.Text = dialog.FileName;
        }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Torrent Save Folder",
            InitialDirectory = Directory.Exists(SaveDirTextBox.Text)
                ? SaveDirTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true)
        {
            SaveDirTextBox.Text = dialog.FolderName;
        }
    }

    private void StartTorrent_Click(object sender, RoutedEventArgs e) => Accept(true);
    private void AddPaused_Click(object sender, RoutedEventArgs e) => Accept(false);

    private void Accept(bool startNow)
    {
        var src = SourceTextBox.Text.Trim();
        bool isMagnet = src.StartsWith("magnet:?xt=", StringComparison.OrdinalIgnoreCase);
        bool isFile = File.Exists(src) && src.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

        if (!isMagnet && !isFile)
        {
            MessageBox.Show(this, "Please enter a valid magnet:? URI or select an existing .torrent file.", "Invalid Torrent Source", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Source = src;
        IsTorrentFile = isFile;
        SaveDirectory = SaveDirTextBox.Text.Trim();
        Sequential = SequentialCheckBox.IsChecked == true;
        StartImmediately = startNow;
        DialogResult = true;
        Close();
    }
}
