using System.Diagnostics;
using System.Windows;
using VelocityDM.App.Helpers;

namespace VelocityDM.App.Views;

public partial class ChromeExtensionWindow : Window
{
    public ChromeExtensionWindow()
    {
        InitializeComponent();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        string extDir = BrowserRegistrationService.ExtractExtensionFiles();
        ExtPathTextBox.Text = extDir;
        bool registered = BrowserRegistrationService.IsNativeHostRegistered();
        NativeHostStatusText.Text = registered
            ? "Native Messaging Host Status: Registered for Chrome & Edge ✓"
            : "Native Messaging Host Status: Not yet registered (click Install below)";
    }

    private void SetupAndOpenChrome_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string extDir = BrowserRegistrationService.ExtractExtensionFiles();
            BrowserRegistrationService.RegisterNativeMessagingHost();
            ExtPathTextBox.Text = extDir;
            Clipboard.SetText(extDir);
            RefreshStatus();
            BrowserRegistrationService.OpenChromeExtensionsPage();
            MessageBox.Show(
                this,
                "Native Messaging Host has been registered and the extension folder path was copied to your clipboard!\n\nIn Chrome:\n1. Turn ON 'Developer mode' (top-right)\n2. Click 'Load unpacked'\n3. Press Ctrl+V in the folder dialog and select the folder.",
                "Ready in Chrome",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Setup error: {ex.Message}", "VelocityDM", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string extDir = BrowserRegistrationService.ExtractExtensionFiles();
        Process.Start("explorer.exe", $"\"{extDir}\"");
    }
}
