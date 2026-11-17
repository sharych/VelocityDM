using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VelocityDM.App.ViewModels;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Views;

public sealed class BatchUrlCandidate : ObservableObject
{
    private bool _isSelected = true;

    public BatchUrlCandidate(string url) => Url = url;

    public string Url { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public partial class BatchImportWindow : Window
{
    private readonly ObservableCollection<BatchUrlCandidate> _candidates = new();
    private readonly string _defaultDir;

    public IReadOnlyList<string> SelectedUrls { get; private set; } = Array.Empty<string>();
    public string? CustomDirectory { get; private set; }
    public int Connections { get; private set; }
    public bool StartImmediately { get; private set; }

    public BatchImportWindow(DownloadManagerService manager)
    {
        InitializeComponent();
        var settings = manager.Settings;
        _defaultDir = settings.DefaultDownloadDirectory;

        PreviewListBox.ItemsSource = _candidates;
        ConnectionsSlider.Value = settings.DefaultConnectionsPerDownload;

        Loaded += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                var parsed = BatchLinkParser.ParseAndExpand(Clipboard.GetText());
                if (parsed.Count > 1)
                {
                    RawUrlsTextBox.Text = Clipboard.GetText();
                }
            }
            RawUrlsTextBox.Focus();
        };
    }

    private void RawUrlsTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var expanded = BatchLinkParser.ParseAndExpand(RawUrlsTextBox.Text);
        _candidates.Clear();
        foreach (var url in expanded)
        {
            _candidates.Add(new BatchUrlCandidate(url));
        }
        PreviewHeaderText.Text = $"Expanded Links Preview ({_candidates.Count})";
    }

    private void ConnectionsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ConnLabel != null)
        {
            ConnLabel.Text = $"Connections: {(int)Math.Round(e.NewValue)}";
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates) c.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _candidates) c.IsSelected = false;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select Batch Save Directory",
            InitialDirectory = Directory.Exists(_defaultDir) ? _defaultDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) == true)
        {
            CustomDirTextBox.Text = dialog.FolderName;
        }
    }

    private void StartNow_Click(object sender, RoutedEventArgs e) => Accept(true);

    private void QueuePaused_Click(object sender, RoutedEventArgs e) => Accept(false);

    private void Accept(bool startNow)
    {
        var chosen = _candidates.Where(c => c.IsSelected).Select(c => c.Url).ToList();
        if (chosen.Count == 0)
        {
            MessageBox.Show(this, "Please paste and select at least one valid HTTP/HTTPS link.", "No Links Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedUrls = chosen;
        CustomDirectory = string.IsNullOrWhiteSpace(CustomDirTextBox.Text) ? null : CustomDirTextBox.Text.Trim();
        Connections = (int)Math.Round(ConnectionsSlider.Value);
        StartImmediately = startNow;
        DialogResult = true;
        Close();
    }
}
