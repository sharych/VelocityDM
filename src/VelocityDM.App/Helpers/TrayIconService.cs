using System.IO;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;
using WpfApp = System.Windows.Application;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Manages the Windows Notification Area (System Tray) icon so VelocityDM runs in background like IDM.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly WinForms.NotifyIcon _notifyIcon;
    private readonly WinForms.ToolStripMenuItem _clipboardMenuItem;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? AddUrlRequested;
    public event Action? AddTorrentRequested;
    public event Action? PauseAllRequested;
    public event Action? ResumeAllRequested;
    public event Action<bool>? ClipboardToggleRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        Drawing.Icon? icon = null;
        try
        {
            var streamInfo = WpfApp.GetResourceStream(new Uri("pack://application:,,,/Assets/VelocityDM.ico"));
            if (streamInfo != null)
            {
                using Stream s = streamInfo.Stream;
                icon = new Drawing.Icon(s);
            }
        }
        catch
        {
            // Fallback below
        }

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open VelocityDM", null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("+ Add Download URL...", null, (_, _) => AddUrlRequested?.Invoke());
        menu.Items.Add("+ Add Torrent / Magnet...", null, (_, _) => AddTorrentRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Resume All Downloads", null, (_, _) => ResumeAllRequested?.Invoke());
        menu.Items.Add("Pause All Downloads", null, (_, _) => PauseAllRequested?.Invoke());

        _clipboardMenuItem = new WinForms.ToolStripMenuItem("Clipboard Watch")
        {
            CheckOnClick = true,
            Checked = true
        };
        _clipboardMenuItem.Click += (_, _) => ClipboardToggleRequested?.Invoke(_clipboardMenuItem.Checked);
        menu.Items.Add(_clipboardMenuItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Exit VelocityDM", null, (_, _) => ExitRequested?.Invoke());

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = icon ?? Drawing.SystemIcons.Application,
            Text = "VelocityDM — Background Download Manager",
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void UpdateTooltip(string text)
    {
        if (_disposed) return;
        // WinForms NotifyIcon.Text throws if >= 64 characters
        string safe = text.Length > 63 ? text[..63] : text;
        _notifyIcon.Text = safe;
    }

    public void UpdateClipboardChecked(bool enabled)
    {
        if (_disposed) return;
        _clipboardMenuItem.Checked = enabled;
    }

    public void ShowBalloon(string title, string message)
    {
        if (_disposed) return;
        _notifyIcon.ShowBalloonTip(2500, title, message, WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
