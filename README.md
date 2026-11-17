<p align="center">
  <img src="src/VelocityDM.App/Assets/VelocityDM.png" width="96" alt="VelocityDM Logo" />
</p>

# VelocityDM — Multi-Segment Parallel Download Manager

A fast, native Windows (.NET 8 WPF) download manager inspired by Internet Download Manager (IDM), supporting **multi-segment parallel downloading per file** and **concurrent queue downloading across multiple links**.

## Features
- **Multi-Segment Parallel Downloading (1–32 Connections)**: Splits byte-range-capable downloads into contiguous byte slices written concurrently via `RandomAccess.WriteAsync` into `.vdm.part` files (zero end-of-download merge overhead), with automatic fallback to single-stream mode when servers ignore `Range` headers or omit `Content-Length`.
- **Smart HTTP Link Probing**: Probes URLs via `HEAD` with `GET Range: bytes=0-0` fallback to follow redirects, read `Content-Disposition` filenames, detect `Accept-Ranges` / `Content-Range`, and capture `ETag` / `Last-Modified` validators.
- **Pause, Resume & Crash-Safe Recovery**: Saves per-segment progress and settings atomically (`%LOCALAPPDATA%\VelocityDM\downloads.json` and `settings.json`). Interrupted active downloads recover safely as `Paused` on startup and validate `ETag` / `Last-Modified` before resuming.
- **Concurrent Queue & Global Speed Limiter**: Configurable `MaxConcurrentDownloads`, queue reordering (`Move Up`, `Move Down`, `Force Start`), `Pause All` / `Resume All`, and a live shared token-bucket global bandwidth cap (`Unlimited` or preset/custom KB/s).
- **Batch Link Import & Wildcard Expander**: Paste multi-line URL lists or IDM-style wildcard patterns such as `https://example.com/archive_part[01-10].zip` or `slice_[a-f].bin`.
- **Windows Clipboard Monitor**: Watches clipboard updates (`AddClipboardFormatListener`) and shows a non-intrusive inline capture bar when a downloadable URL is copied.
- **Auto-Categorization & SHA-256 Verification**: Organizes files into `Programs`, `Compressed`, `Video`, `Music`, `Documents`, `Images`, and `Other`, and computes/verifies optional expected SHA-256 checksums.
- **Live Parallel Segments Map**: Watch each connection's byte range, progress bar, speed, and status update in real time.

## Project Structure
- `src/VelocityDM.Core` — Download engine, HTTP probe, parallel segment runner, rate limiter, atomic JSON state store, category classifier, and batch link parser.
- `src/VelocityDM.App` — Native Windows WPF desktop UI, dialogs, clipboard monitor, and embedded multi-resolution app icon.
- `tests/VelocityDM.Tests` — xUnit unit tests and local HTTP integration test server suite.

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
