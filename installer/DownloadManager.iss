; Inno Setup script for Download Manager (per-user install, no admin rights).
; Build with scripts\build-installer.ps1, which publishes a self-contained app into
; artifacts\installer-app and the Chrome extension into artifacts\installer-extension.
;
; Layout matches scripts\install.ps1 so the in-app updater and repair script keep working:
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\app\           program files (replaced on upgrade)
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\extension\     unpacked Chrome extension
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\native-host\   browser bridge manifest
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\install.json   installed version/channel
;   %LOCALAPPDATA%\Inbound Download Manager\   user data: jobs, settings, logs - kept on uninstall

#ifndef AppVersion
  #define AppVersion "0.3.2"
#endif
#ifndef SourceRoot
  #define SourceRoot "..\artifacts"
#endif

#define AppName "Inbound Download Manager"
#define HostExe "DownloadManager.Host.exe"
#define UiExe "DownloadManager.Desktop.WinUI.exe"
#define NativeHostName "com.kaank.downloadmanager"
#define ExtensionId "pknddmneejadnnngffojnoecojhdfalc"

[Setup]
AppId={{6F4B7C2E-3D91-4E8A-9B57-2C1D0A8E5F13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName=Inbound Download Manager {#AppVersion}
AppPublisher=Kaan
DefaultDirName={localappdata}\Programs\{#AppName}
UsePreviousAppDir=no
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#SourceRoot}\installer
OutputBaseFilename=DownloadManagerSetup-{#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\app\{#HostExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "startup"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"

[Files]
Source: "{#SourceRoot}\installer-app\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\installer-extension\*"; DestDir: "{app}\extension"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\scripts\apply-update.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\repair.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\register-native-host.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\scripts\stop-for-setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
; Extracted to {tmp} before files are replaced (the installed copy may be older or missing).
Source: "..\scripts\stop-for-setup.ps1"; Flags: dontcopy

[InstallDelete]
; Upgrades replace the program folder completely, like install.ps1 does.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\extension"

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\{#HostExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}\app"; IconFilename: "{app}\app\{#HostExe}"; Tasks: desktopicon

[Registry]
; Chrome native messaging host (the browser bridge).
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostName}.json"; Flags: uninsdeletekey
; Launch at sign-in is owned by the app's Options setting (the Host writes this value from it);
; setup configures the setting independently of the optional launch. Uninstall removes the value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DownloadManagerHost"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}\app"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent
Filename: "{app}\INSTALL.txt"; Description: "Show how to add the Chrome extension"; Flags: postinstall shellexec skipifsilent unchecked

[UninstallDelete]
Type: files; Name: "{app}\install.json"
Type: files; Name: "{app}\INSTALL.txt"
Type: filesandordirs; Name: "{app}\native-host"

[Code]
function JsonEscape(const Value: String): String;
begin
  Result := Value;
  StringChangeEx(Result, '\', '\\', True);
  StringChangeEx(Result, '"', '\"', True);
end;

procedure WriteUtf8(const FileName, Text: String);
begin
  // No BOM: JSON readers such as Chrome's native-messaging loader reject it.
  if not SaveStringsToUTF8FileWithoutBOM(FileName, [Text], False) then
    RaiseException('Could not write ' + FileName);
end;

// Restart Manager cannot close the tray-only Host, so ask it to exit through
// its own shutdown (as tray Exit does) before files are replaced or removed.
function StopRunningInstance(const Script, AppDir: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not DirExists(AppDir) then
    exit;
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
              '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + Script + '" -AppDir "' + AppDir + '"',
              '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := False
  else
    Result := ResultCode = 0;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ExtractTemporaryFile('stop-for-setup.ps1');
  if not StopRunningInstance(ExpandConstant('{tmp}\stop-for-setup.ps1'), ExpandConstant('{app}\app')) then
    Result := '{#AppName} is still running and could not be closed. Exit it from the tray icon, then run Setup again.';
end;

function InitializeUninstall(): Boolean;
var
  Script: String;
begin
  Result := True;
  Script := ExpandConstant('{app}\stop-for-setup.ps1');
  if FileExists(Script) and not StopRunningInstance(Script, ExpandConstant('{app}\app')) then
  begin
    MsgBox('{#AppName} is still running and could not be closed. Exit it from the tray icon, then uninstall again.', mbError, MB_OK);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Root, ManifestDir, StartupArgument: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  Root := ExpandConstant('{app}');
  WriteUtf8(Root + '\install.json',
    '{' + #13#10 +
    '  "version": "{#AppVersion}",' + #13#10 +
    '  "channel": "stable",' + #13#10 +
    '  "installRoot": "' + JsonEscape(Root) + '",' + #13#10 +
    '  "installedAt": "' + GetDateTimeString('yyyy-mm-dd"T"hh:nn:ss', '-', ':') + '"' + #13#10 +
    '}');

  ManifestDir := Root + '\native-host';
  ForceDirectories(ManifestDir);
  WriteUtf8(ManifestDir + '\{#NativeHostName}.json',
    '{' + #13#10 +
    '  "name": "{#NativeHostName}",' + #13#10 +
    '  "description": "{#AppName} browser bridge",' + #13#10 +
    '  "path": "' + JsonEscape(Root + '\app\DownloadManager.NativeHost.exe') + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_origins": [ "chrome-extension://{#ExtensionId}/" ]' + #13#10 +
    '}');

  SaveStringToFile(Root + '\INSTALL.txt',
    '{#AppName} {#AppVersion} is installed in:' + #13#10 +
    '  ' + Root + '\app' + #13#10 + #13#10 +
    'Add the Chrome extension (one time):' + #13#10 +
    '  1. Open chrome://extensions' + #13#10 +
    '  2. Turn on Developer mode (top right)' + #13#10 +
    '  3. Click "Load unpacked" and select:' + #13#10 +
    '     ' + Root + '\extension' + #13#10 +
    '  4. Check that the extension ID is {#ExtensionId}' + #13#10 + #13#10 +
    'The app lives in the notification area (tray). Closing the main window hides it;' + #13#10 +
    'downloads continue. Use Exit in the tray menu to stop it.' + #13#10 + #13#10 +
    'Uninstall from Settings > Apps. Your download list and settings are kept in' + #13#10 +
    '  ' + ExpandConstant('{localappdata}\{#AppName}') + #13#10 +
    'Uninstall does not delete your download list, settings or downloaded files.' + #13#10, False);

  if WizardIsTaskSelected('startup') then
    StartupArgument := '--configure-sign-in-launch=enabled'
  else
    StartupArgument := '--configure-sign-in-launch=disabled';
  if not Exec(Root + '\app\{#HostExe}', '--background ' + StartupArgument,
              Root + '\app', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('Could not configure sign-in startup.');
  if ResultCode <> 0 then
    RaiseException('Could not configure sign-in startup. Exit {#AppName} and run Setup again.');
end;
