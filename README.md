<p align="center">
  <img src="src/VelocityDM.App/Assets/VelocityDM.png" width="96" alt="VelocityDM Logo" />
</p>

# VelocityDM — Multi-Segment Parallel Download & Torrent Manager

A fast, native Windows (.NET 8 WPF) download manager inspired by Internet Download Manager (IDM), supporting **multi-segment parallel HTTP downloads**, **BitTorrent / Magnet downloads**, **background System Tray mode**, and **Google Chrome browser integration**.

## Features
- **Multi-Segment Parallel HTTP Downloads (1–32 Connections)**: Splits byte-range-capable downloads into contiguous byte slices written concurrently via `RandomAccess.WriteAsync` into `.vdm.part` files (no slow end-of-download merge step), with automatic fallback to single-stream mode when servers ignore `Range` headers or omit `Content-Length`.
- **BitTorrent & Magnet Section (powered by MonoTorrent)**:
  - Add `.torrent` files or `magnet:?xt=urn:btih:...` links (including from clipboard!).
  - Supports DHT, Peer Exchange (PEX), Local Peer Discovery, fast-resume persistence, sequential piece ordering (for streaming media), per-file download priorities (`High`, `Normal`, `Low`, `DoNotDownload`), UL/DL rate limits, and optional auto-stop seeding at 100%.
- **Background System Tray Mode (IDM-style)**:
  - Closing the window hides VelocityDM to the Windows System Tray so active HTTP downloads, torrents, clipboard monitor, and the Chrome bridge keep running in the background.
  - Enforces single-instance execution via Mutex + Named Pipe IPC and optionally starts minimized with Windows.
- **Google Chrome Extension Integration**:
  - Bundled Manifest V3 extension (`extension/chrome/`) intercepts Chrome downloads, forwards `Cookie` and `Referer` headers, adds a `"Download with VelocityDM"` right-click menu, and connects over both the local bridge (`127.0.0.1:17890`) and Chrome **Native Messaging Host** (`com.velocitydm.host`) so VelocityDM auto-launches even when closed.
  - Built-in **Chrome Ext** setup wizard extracts the extension, registers the Native Messaging Host in `HKCU`, copies the path, and opens `chrome://extensions`.
- **Pause, Resume & Crash-Safe Recovery**: Atomically persists HTTP queue, segments, torrents, and settings in `%LOCALAPPDATA%\VelocityDM\`.
- **Batch Link Import & Wildcard Expander**: Supports multi-line URLs and IDM-style ranges like `archive_part[01-10].zip` or `slice_[a-f].bin`.
- **Auto-Categorization & SHA-256 Verification**: Organizes downloads into `Programs`, `Compressed`, `Video`, `Music`, `Documents`, `Images`, `Other`, and `Torrents`, with optional expected SHA-256 verification.

## Chrome Extension Setup (One-Time Load)
Because Google Chrome requires user confirmation to load unpacked local extensions:
1. Click **Chrome Ext** in VelocityDM's toolbar and click **Install Host & Open Chrome Extensions** (this registers the Native Messaging Host and copies the extension folder path).
2. In `chrome://extensions`, toggle **Developer mode** ON (top-right).
3. Click **Load unpacked**, paste the copied path (`Ctrl+V`), and select the folder.

## Build, Test & Publish

Requires [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows:

```powershell
# Build & run all unit + integration tests
dotnet build VelocityDM.sln -c Release
dotnet test VelocityDM.sln -c Release --no-build

# Publish single-file executable (uses installed .NET 8 Desktop Runtime)
dotnet publish src/VelocityDM.App/VelocityDM.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
```

## License
[MIT](LICENSE)
