#ifndef MyAppVersion
  #define MyAppVersion "2.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\installer-app"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist\release"
#endif

[Setup]
AppId={{8D4F61C2-9B3E-4E12-A7C4-65F2D9B31A08}
AppName=VelocityDM
AppVersion={#MyAppVersion}
AppVerName=VelocityDM {#MyAppVersion}
AppPublisher=VelocityDM
AppPublisherURL=https://github.com/sharych/VelocityDM
AppSupportURL=https://github.com/sharych/VelocityDM/issues
AppUpdatesURL=https://github.com/sharych/VelocityDM/releases
DefaultDirName={autopf}\VelocityDM
DefaultGroupName=VelocityDM
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=VelocityDM-Setup-v{#MyAppVersion}
SetupIconFile=..\src\VelocityDM.App\Assets\VelocityDM.ico
UninstallDisplayIcon={app}\VelocityDM.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany=VelocityDM
VersionInfoDescription=VelocityDM Parallel Download & Torrent Manager Installer
VersionInfoProductName=VelocityDM
VersionInfoProductVersion={#MyAppVersion}.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "installdotnet"; Description: "Download & install Microsoft .NET 8.0 Desktop Runtime (not detected on this PC)"; GroupDescription: "Runtime Prerequisites:"; Check: not IsDotNet8DesktopInstalled
Name: "installdotnet_already"; Description: "Microsoft .NET 8.0 Desktop Runtime is already installed (reinstall / repair)"; GroupDescription: "Runtime Prerequisites:"; Flags: unchecked; Check: IsDotNet8DesktopInstalled
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "chromehost"; Description: "Register Chrome & Edge Native Messaging integration"; GroupDescription: "Browser & System Integration:"
Name: "startup"; Description: "Start VelocityDM minimized in System Tray on Windows startup"; GroupDescription: "Browser & System Integration:"; Flags: unchecked
Name: "torrentassoc"; Description: "Associate .torrent files and magnet: links with VelocityDM"; GroupDescription: "Browser & System Integration:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\extension\chrome\*"; DestDir: "{app}\extension\chrome"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\VelocityDM"; Filename: "{app}\VelocityDM.exe"; IconFilename: "{app}\VelocityDM.exe"
Name: "{group}\Uninstall VelocityDM"; Filename: "{uninstallexe}"
Name: "{autodesktop}\VelocityDM"; Filename: "{app}\VelocityDM.exe"; IconFilename: "{app}\VelocityDM.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "VelocityDM"; ValueData: """{app}\VelocityDM.exe"" --minimized"; Flags: uninsdeletevalue; Tasks: startup
Root: HKCU; Subkey: "Software\Classes\.torrent"; ValueType: string; ValueName: ""; ValueData: "VelocityDM.Torrent"; Flags: uninsdeletevalue; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\VelocityDM.Torrent"; ValueType: string; ValueName: ""; ValueData: "BitTorrent Metadata File"; Flags: uninsdeletekey; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\VelocityDM.Torrent\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\VelocityDM.exe"",0"; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\VelocityDM.Torrent\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VelocityDM.exe"" --add-url ""%1"""; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: ""; ValueData: "URL:Magnet Protocol"; Flags: uninsdeletekey; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\magnet"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\magnet\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: """{app}\VelocityDM.exe"",0"; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Classes\magnet\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\VelocityDM.exe"" --add-url ""%1"""; Tasks: torrentassoc
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\com.velocitydm.host"; Flags: uninsdeletekey; Tasks: chromehost
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\com.velocitydm.host"; Flags: uninsdeletekey; Tasks: chromehost

[Run]
Filename: "{app}\VelocityDM.exe"; Parameters: "--register-browser"; Tasks: chromehost; Flags: runhidden waituntilterminated
Filename: "{app}\VelocityDM.exe"; Description: "{cm:LaunchProgram,VelocityDM}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\VelocityDM\ChromeExtension"
Type: files; Name: "{localappdata}\VelocityDM\com.velocitydm.host.json"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  DotNetInstallerDownloaded: Boolean;

function IsDotNet8DesktopInstalled(): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  FindRec: TFindRec;
  SharedDir: String;
begin
  Result := False;
  if RegGetValueNames(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if Pos('8.0.', Names[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;

  SharedDir := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.0.*');
  if FindFirst(SharedDir, FindRec) then
  begin
    try
      Result := True;
    finally
      FindClose(FindRec);
    end;
  end;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DotNetInstallerDownloaded := False;
end;

function NeedDotNetDownload(): Boolean;
begin
  Result := (WizardIsTaskSelected('installdotnet') and (not IsDotNet8DesktopInstalled()))
         or WizardIsTaskSelected('installdotnet_already');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    if NeedDotNetDownload() then
    begin
      DownloadPage.Clear;
      DownloadPage.Add('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', 'windowsdesktop-runtime-win-x64.exe', '');
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
          DotNetInstallerDownloaded := True;
          Result := True;
        except
          if DownloadPage.AbortedByUser then
            Log('Download aborted by user.')
          else
            SuppressibleMsgBox(AddPeriod(GetExceptionMessage), mbCriticalError, MB_OK, IDOK);
          Result := False;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if (CurStep = ssInstall) and DotNetInstallerDownloaded then
  begin
    WizardForm.StatusLabel.Caption := 'Installing Microsoft .NET 8.0 Desktop Runtime...';
    if not Exec(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'), '/install /passive /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    begin
      MsgBox('Failed to launch .NET 8.0 Desktop Runtime installer (error ' + IntToStr(ResultCode) + ').', mbError, MB_OK);
    end;
  end;
end;
