using Avalonia.Controls;
using Avalonia.Interactivity;
using VelocityDM.Mac.Helpers;

namespace VelocityDM.Mac.Views;

public partial class ChromeExtensionWindow : Window
{
    public ChromeExtensionWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string extDir = MacBrowserRegistrationService.ExtractExtensionFiles();
        ExtPathTextBox.Text = extDir;
        bool registered = MacBrowserRegistrationService.IsNativeHostRegistered();
        NativeHostStatusText.Text = registered
            ? "Native Messaging Host Status: Registered for Google Chrome on macOS ✓"
            : "Native Messaging Host Status: Not yet registered (click Install below)";
    }

    private void SetupAndOpenChrome_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = MacBrowserRegistrationService.ExtractExtensionFiles();
            var browsers = MacBrowserRegistrationService.RegisterNativeMessagingHosts();
            ExtPathTextBox.Text = extDir;
            MacBrowserRegistrationService.CopyToMacClipboard(extDir);
            RefreshStatus();

            MacBrowserRegistrationService.RevealExtensionInFinder();
            MacBrowserRegistrationService.OpenChromeExtensionsPage();

            string browserList = browsers.Count > 0 ? string.Join(", ", browsers) : "Google Chrome";
            FeedbackText.Text = $"Registered Native Messaging Host for {browserList}, opened Finder at ChromeExtension, and copied the path to your clipboard!";
        }
        catch (Exception ex)
        {
            FeedbackText.Text = $"Setup error: {ex.Message}";
        }
    }

    private void OpenFolder_Click(object? sender, RoutedEventArgs e)
    {
        MacBrowserRegistrationService.RevealExtensionInFinder();
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close(false);
}
