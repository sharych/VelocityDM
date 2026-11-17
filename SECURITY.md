# Security & Trust Policy

## Verifying Downloads & Releases
Every official release on [GitHub Releases](https://github.com/sharych/VelocityDM/releases) is built from public source code via GitHub Actions CI (`windows-latest`) and includes:
- `VelocityDM-Setup-v<version>.exe` (Windows Installer)
- `VelocityDM-v<version>-win-x64-portable.zip` (Portable single-folder archive)
- `SHA256SUMS.txt` (SHA-256 checksums for all release assets)
- GitHub Artifact Attestations (build provenance linked to the exact git commit)

You can verify any downloaded archive or installer in PowerShell:
```powershell
Get-FileHash .\VelocityDM-Setup-v2.0.0.exe -Algorithm SHA256
```
Compare the output against the corresponding entry in `SHA256SUMS.txt`.

## Windows SmartScreen Note
Until binaries are signed with an Authenticode EV/OV certificate or build download reputation, Windows Defender SmartScreen may display an *"Unknown publisher"* prompt on new releases. You can independently inspect the full source code, build from source with `dotnet build`, or verify the CI-generated checksums and provenance attestation before running.

## Network, IPC & Registry Scope
- **Local Browser Bridge**: Listens exclusively on loopback (`127.0.0.1:17890` by default) and is never exposed to external network interfaces.
- **User-Scope Registry (`HKCU`)**:
  - `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.velocitydm.host` (only when Chrome integration is enabled/installed)
  - `HKCU\Software\Microsoft\Edge\NativeMessagingHosts\com.velocitydm.host`
  - `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\VelocityDM` (only when *Start with Windows* is explicitly enabled)
- **Local State**: Stored in `%LOCALAPPDATA%\VelocityDM\` (`downloads.json`, `torrents.json`, `settings.json`, `torrent-cache/`).

## Chrome Extension Permissions
The bundled Manifest V3 extension (`extension/chrome/`) requests:
- `downloads`: Intercept downloads to hand them off to VelocityDM (canceling Chrome's duplicate download only after VelocityDM confirms receipt).
- `cookies` + `<all_urls>`: Forward session cookies for authenticated downloads so direct multi-segment requests succeed.
- `nativeMessaging`: Launch VelocityDM automatically in background when closed.
- `contextMenus` & `storage`: Right-click *"Download with VelocityDM"* menu and extension preferences.

## Reporting a Vulnerability
Please open a GitHub Security Advisory or issue at https://github.com/sharych/VelocityDM/issues with reproduction steps.
