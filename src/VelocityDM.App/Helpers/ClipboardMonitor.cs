using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VelocityDM.Core.Services;

namespace VelocityDM.App.Helpers;

/// <summary>
/// Watches Windows clipboard updates via AddClipboardFormatListener and raises UrlDetected for HTTP(S) links.
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private string? _lastSeenUrl;
    private DateTimeOffset _lastSeenTime = DateTimeOffset.MinValue;
    private bool _disposed;

    public bool IsEnabled { get; set; } = true;
    public bool OnlyKnownFileExtensions { get; set; } = true;

    public event Action<string>? UrlDetected;

    public void SuppressUrlTemporarily(string url)
    {
        _lastSeenUrl = url.Trim();
        _lastSeenTime = DateTimeOffset.UtcNow;
    }

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero)
            return;

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        AddClipboardFormatListener(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && IsEnabled)
        {
            TryInspectClipboard();
        }
        return IntPtr.Zero;
    }

    private void TryInspectClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText())
                return;

            string raw = Clipboard.GetText().Trim();
            if (string.IsNullOrWhiteSpace(raw) || raw.Contains('\n') || raw.Contains('\r'))
                return;

            if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            if (OnlyKnownFileExtensions && !FileCategoryClassifier.IsKnownDownloadableExtension(uri.AbsoluteUri))
                return;

            var now = DateTimeOffset.UtcNow;
            if (string.Equals(_lastSeenUrl, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastSeenTime).TotalSeconds < 5)
            {
                return;
            }

            _lastSeenUrl = uri.AbsoluteUri;
            _lastSeenTime = now;
            UrlDetected?.Invoke(uri.AbsoluteUri);
        }
        catch
        {
            // Clipboard can momentarily be locked by another process; ignore safely.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
