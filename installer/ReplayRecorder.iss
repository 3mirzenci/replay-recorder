; Replay Recorder installer (Inno Setup 6). Built by build.ps1, which publishes the apps first.
;   /DSourceDir=<folder>   published app folder (default ..\dist\ReplayRecorder)
;   /DAppVersion=1.2.3
;   /DBundled              ffmpeg and mpv are inside the package (offline installer)
;
; Command line at install time (used by the in-app updater):
;   /SILENT /UPDATE=1        in-place upgrade; starts the recorder again afterwards
;   /OPENLIBRARY=1           also reopen the library

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\ReplayRecorder"
#endif

[Setup]
AppId={{8A5C3E2B-6D1F-4F7A-9B0E-C11D2E3F4A5B}
AppName=Replay Recorder
AppVersion={#AppVersion}
AppVerName=Replay Recorder {#AppVersion}
AppPublisher=3mirzenci
AppPublisherURL=https://github.com/3mirzenci/replay-recorder
AppUpdatesURL=https://github.com/3mirzenci/replay-recorder/releases
VersionInfoVersion={#AppVersion}
; Per-user install: no admin prompt, matches where the app keeps its settings.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Replay Recorder
DisableProgramGroupPage=yes
DisableDirPage=auto
OutputDir=..\dist
; A fixed file name, so the "latest release" download link never changes.
#ifdef Bundled
OutputBaseFilename=ReplayRecorder-Setup-offline
#else
OutputBaseFilename=ReplayRecorder-Setup
#endif
SetupIconFile=..\assets\replayrecorder.ico
UninstallDisplayIcon={app}\ReplayRecorder.exe
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Start the recorder automatically when Windows starts"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut for the library"; Flags: unchecked
#ifndef Bundled
Name: "tools"; Description: "Install ffmpeg and mpv with winget (required, only what's missing)"; Check: ToolsMissing
#endif

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[InstallDelete]
; Files from older versions that the new one no longer ships.
Type: files; Name: "{app}\*.pdb"

[Icons]
Name: "{autoprograms}\Replay Recorder"; Filename: "{app}\ReplayRecorder.exe"; Comment: "Watch, trim and export your clips"
Name: "{autoprograms}\Replay Recorder (tray)"; Filename: "{app}\ReplayRecorder.Recorder.exe"; Comment: "Start the instant-replay recorder"
Name: "{autodesktop}\Replay Recorder"; Filename: "{app}\ReplayRecorder.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Replay Recorder"; \
    ValueData: """{app}\ReplayRecorder.Recorder.exe"""; Tasks: startup

[Run]
#ifndef Bundled
Filename: "winget.exe"; Parameters: "install --id Gyan.FFmpeg -e --silent --accept-source-agreements --accept-package-agreements"; \
    StatusMsg: "Installing ffmpeg with winget..."; Flags: runhidden waituntilterminated; Tasks: tools; Check: FfmpegMissing
Filename: "winget.exe"; Parameters: "install --id shinchiro.mpv -e --silent --accept-source-agreements --accept-package-agreements"; \
    StatusMsg: "Installing the mpv player with winget..."; Flags: runhidden waituntilterminated; Tasks: tools; Check: MpvMissing
#endif
; Normal install: offered on the last page.
Filename: "{app}\ReplayRecorder.Recorder.exe"; Description: "Start the recorder now"; Flags: postinstall nowait skipifsilent
Filename: "{app}\ReplayRecorder.exe"; Description: "Open the library"; Flags: postinstall nowait skipifsilent unchecked
; In-app update: put things back the way they were.
Filename: "{app}\ReplayRecorder.Recorder.exe"; Flags: nowait; Check: IsUpdate
Filename: "{app}\ReplayRecorder.exe"; Flags: nowait; Check: IsUpdate and OpenLibrary

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/IM ReplayRecorder.Recorder.exe /F"; Flags: runhidden; RunOnceId: "KillRecorder"
Filename: "taskkill.exe"; Parameters: "/IM ReplayRecorder.exe /F"; Flags: runhidden; RunOnceId: "KillLibrary"

[Code]
function IsUpdate(): Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function OpenLibrary(): Boolean;
begin
  Result := ExpandConstant('{param:OPENLIBRARY|0}') = '1';
end;

function OnPath(const Exe: String): Boolean;
begin
  Result := FileSearch(Exe, GetEnv('PATH')) <> '';
end;

function FfmpegMissing(): Boolean;
begin
  Result := not (OnPath('ffmpeg.exe') or FileExists(ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe'))
                 or FileExists(ExpandConstant('{app}\ffmpeg\ffmpeg.exe')));
end;

function MpvMissing(): Boolean;
begin
  Result := not (OnPath('mpv.exe') or FileExists(ExpandConstant('{commonpf64}\MPV Player\mpv.exe'))
                 or FileExists(ExpandConstant('{commonpf64}\mpv\mpv.exe')) or FileExists(ExpandConstant('{app}\mpv\mpv.exe')));
end;

function ToolsMissing(): Boolean;
begin
  Result := FfmpegMissing() or MpvMissing();
end;

// The recorder and library may be running from the install folder (always, for an update).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var Code: Integer;
begin
  Exec('taskkill.exe', '/IM ReplayRecorder.Recorder.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Exec('taskkill.exe', '/IM ReplayRecorder.exe /F', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;

// Uninstall: remove the autostart entry whether or not it was set by the installer.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Replay Recorder');
end;
