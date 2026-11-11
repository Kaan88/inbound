; Inno Setup script for Download Manager (per-user install, no admin rights).
; Build with scripts\build-installer.ps1, which publishes a self-contained app into
; artifacts\installer-app and the Chrome extension into artifacts\installer-extension.
;
; Layout matches scripts\install.ps1 so the in-app updater and repair script keep working:
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\app\           program files (replaced on upgrade)
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\extension\     unpacked browser extension
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\native-host\   browser bridge manifest
;   %LOCALAPPDATA%\Programs\Inbound Download Manager\install.json   installed version/channel
;   %LOCALAPPDATA%\Inbound Download Manager\   user data: jobs, settings, logs - kept on uninstall

#ifndef AppVersion
  #define AppVersion "0.6.7"
#endif
#ifndef SourceRoot
  #define SourceRoot "..\artifacts"
#endif

#define AppName "Inbound Download Manager"
#define HostExe "DownloadManager.Host.exe"
#define UiExe "DownloadManager.Desktop.WinUI.exe"
#define NativeHostName "com.kaank.downloadmanager"
#define ExtensionId "pknddmneejadnnngffojnoecojhdfalc"
#define StoreExtensionId "olhbcdbjdnfoiinllnlpandggadfabfm"
#define EdgeStoreExtensionId "ifoagddcenmjpbklkhghbdmgplldcdci"
#define FirefoxExtensionId "inbound-bridge@kaan88"
#define ChromeStoreUrl "https://chromewebstore.google.com/detail/" + StoreExtensionId
#define EdgeStoreUrl "https://microsoftedge.microsoft.com/addons/detail/" + EdgeStoreExtensionId
; EXT.S4: the Firefox add-on is self-distributed. build-installer.ps1 passes /DFirefoxXpi=<signed XPI>; Setup
; installs it as {app}\firefox\{#FirefoxXpiName}, and Firefox updates it from the manifest's update_url.
#define FirefoxXpiName "inbound-bridge.xpi"
; The first extension version with an update_url (an older copy never updates and has to be added again).
#define FirefoxSelfUpdateSince "0.5.3"

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
#ifdef FirefoxXpi
Source: "{#FirefoxXpi}"; DestDir: "{app}\firefox"; DestName: "{#FirefoxXpiName}"; Flags: ignoreversion
#endif
; Extracted to {tmp} before files are replaced (the installed copy may be older or missing).
Source: "..\scripts\stop-for-setup.ps1"; Flags: dontcopy

[InstallDelete]
; Upgrades replace the program folder completely, like install.ps1 does.
Type: filesandordirs; Name: "{app}\app"
Type: filesandordirs; Name: "{app}\extension"
Type: filesandordirs; Name: "{app}\firefox"
; Leftovers of an interrupted or failed auto-update.
Type: filesandordirs; Name: "{app}\app-new"
Type: filesandordirs; Name: "{app}\extension-new"
Type: filesandordirs; Name: "{app}\app.failed-*"
Type: filesandordirs; Name: "{app}\extension.failed-*"
Type: filesandordirs; Name: "{app}\*.delete-*"

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}"; IconFilename: "{app}\app\{#HostExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}"; IconFilename: "{app}\app\{#HostExe}"; Tasks: desktopicon

[Registry]
; Native messaging host (the browser bridge), registered for Chrome and for Edge. Both keys point at
; the same manifest and are written whether or not the browser is installed. Edge's key is optional:
; the Host tries again at every start, so a failure there does not stop Setup. Uninstall removes them
; in CurUninstallStepChanged, only when they are this install's.
Root: HKCU; Subkey: "Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostName}.json"
Root: HKCU; Subkey: "Software\Microsoft\Edge\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostName}.json"; Flags: noerror
; Firefox reads its own key and a manifest of its own (allowed_extensions, not allowed_origins), written
; in CurStepChanged. Optional like Edge's.
Root: HKCU; Subkey: "Software\Mozilla\NativeMessagingHosts\{#NativeHostName}"; ValueType: string; ValueName: ""; ValueData: "{app}\native-host\{#NativeHostName}.firefox.json"; Flags: noerror
; Launch at sign-in is owned by the app's Options setting (the Host writes this value from it);
; setup configures the setting independently of the optional launch. Uninstall removes the value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "DownloadManagerHost"; Flags: uninsdeletevalue dontcreatekey

[Run]
Filename: "{app}\app\{#HostExe}"; Parameters: "--open"; WorkingDir: "{app}"; Description: "Start {#AppName} now"; Flags: postinstall nowait skipifsilent
Filename: "{app}\INSTALL.txt"; Description: "Show how to add the browser extension"; Flags: postinstall shellexec skipifsilent unchecked
Filename: "{#ChromeStoreUrl}"; Description: "Open the extension in the Chrome Web Store (Chrome)"; Flags: postinstall shellexec skipifsilent unchecked nowait
Filename: "{#EdgeStoreUrl}"; Description: "Open the extension in Microsoft Edge Add-ons (Edge)"; Flags: postinstall shellexec skipifsilent unchecked nowait
#ifdef FirefoxXpi
; Firefox opens the XPI and asks the user to add it. Ticked when Firefox is installed and no profile in the
; usual place has the add-on yet. Where one has it, offered unticked: a copy older than
; FirefoxSelfUpdateSince has no update_url and never updates, so it must be added again once; later copies
; update themselves. Not detected: Firefox from the Microsoft Store (no App Paths entry; the user adds the
; XPI by hand, as INSTALL.txt says) and profiles kept outside the usual folder (they get the ticked entry).
Filename: "{code:GetFirefoxExe}"; Parameters: """{app}\firefox\{#FirefoxXpiName}"""; Description: "Add the extension to Firefox (Firefox asks you to confirm)"; Flags: postinstall nowait skipifsilent; Check: FirefoxNeedsInbound
Filename: "{code:GetFirefoxExe}"; Parameters: """{app}\firefox\{#FirefoxXpiName}"""; Description: "Add the extension to Firefox again (needed once if your copy is older than {#FirefoxSelfUpdateSince}, so it updates itself)"; Flags: postinstall nowait skipifsilent unchecked; Check: FirefoxHasInboundAlready
#endif

[UninstallDelete]
Type: files; Name: "{app}\install.json"
Type: files; Name: "{app}\INSTALL.txt"
; Before 0.6.0 the window wrote its start-up trace beside its exe (now in the logs folder). Setup didn't
; install it, so an upgraded install's copy is named here.
Type: files; Name: "{app}\app\ui-trace.log"
Type: filesandordirs; Name: "{app}\native-host"
Type: filesandordirs; Name: "{app}\app-rollback"
Type: filesandordirs; Name: "{app}\extension-rollback"
Type: filesandordirs; Name: "{app}\app-new"
Type: filesandordirs; Name: "{app}\extension-new"
Type: filesandordirs; Name: "{app}\app.failed-*"
Type: filesandordirs; Name: "{app}\extension.failed-*"
Type: filesandordirs; Name: "{app}\*.delete-*"

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

// EXT.S4: Firefox's exe from App Paths (per user first), or '' when Firefox is not installed.
function FirefoxPath(): String;
var
  Key: String;
begin
  Result := '';
  Key := 'SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe';
  if not RegQueryStringValue(HKCU, Key, '', Result) then
    if not RegQueryStringValue(HKLM64, Key, '', Result) then
      if not RegQueryStringValue(HKLM32, Key, '', Result) then
        Result := '';
  Result := RemoveQuotes(Result);
  if (Result <> '') and not FileExists(Result) then
    Result := '';
end;

function GetFirefoxExe(Param: String): String;
begin
  Result := FirefoxPath();
end;

// Whether a Firefox profile in the usual folder already has the add-on (Firefox keeps it as <id>.xpi).
function FirefoxHasInbound(): Boolean;
var
  FindRec: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{userappdata}\Mozilla\Firefox\Profiles\');
  if FindFirst(Base + '*', FindRec) then
  try
    repeat
      if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (FindRec.Name <> '.') and (FindRec.Name <> '..') then
        Result := FileExists(Base + FindRec.Name + '\extensions\{#FirefoxExtensionId}.xpi');
    until Result or not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

function FirefoxNeedsInbound(): Boolean;
begin
  Result := (FirefoxPath() <> '') and not FirefoxHasInbound();
end;

function FirefoxHasInboundAlready(): Boolean;
begin
  Result := (FirefoxPath() <> '') and FirefoxHasInbound();
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

// The same rule as scripts\uninstall.ps1: a key is removed only when it points at this install's
// manifest or at a file that no longer exists, so another program's working registration stays.
procedure RemoveNativeHostKey(const SubKey: String);
var
  Registered: String;
begin
  if RegQueryStringValue(HKCU, SubKey, '', Registered) and FileExists(Registered) and
     (CompareText(Registered, ExpandConstant('{app}\native-host\{#NativeHostName}.json')) <> 0) then
    exit;
  RegDeleteKeyIncludingSubkeys(HKCU, SubKey);
end;

// The same rule for Firefox's key, which points at Firefox's own manifest.
procedure RemoveFirefoxNativeHostKey(const SubKey: String);
var
  Registered: String;
begin
  if RegQueryStringValue(HKCU, SubKey, '', Registered) and FileExists(Registered) and
     (CompareText(Registered, ExpandConstant('{app}\native-host\{#NativeHostName}.firefox.json')) <> 0) then
    exit;
  RegDeleteKeyIncludingSubkeys(HKCU, SubKey);
end;

var
  // Installs upgraded from 0.5.3 or earlier: Setup appends to the existing uninstall log, and the old
  // entries still carry "uninsdeletekey" for Chrome's key. Inno Setup runs them after usUninstall, so
  // the key goes whatever it holds by then. Another program's working registration found there at
  // usUninstall is written back at usPostUninstall.
  ForeignChromeRegistration: String;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Registered: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    ForeignChromeRegistration := '';
    if RegQueryStringValue(HKCU, 'Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}', '', Registered) and
       FileExists(Registered) and
       (CompareText(Registered, ExpandConstant('{app}\native-host\{#NativeHostName}.json')) <> 0) then
      ForeignChromeRegistration := Registered;
    RemoveNativeHostKey('Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}');
    RemoveNativeHostKey('Software\Microsoft\Edge\NativeMessagingHosts\{#NativeHostName}');
    RemoveFirefoxNativeHostKey('Software\Mozilla\NativeMessagingHosts\{#NativeHostName}');
  end
  else if (CurUninstallStep = usPostUninstall) and (ForeignChromeRegistration <> '') and
          not RegValueExists(HKCU, 'Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}', '') then
    RegWriteStringValue(HKCU, 'Software\Google\Chrome\NativeMessagingHosts\{#NativeHostName}', '', ForeignChromeRegistration);
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
    '  "allowed_origins": [ "chrome-extension://{#StoreExtensionId}/", "chrome-extension://{#EdgeStoreExtensionId}/", "chrome-extension://{#ExtensionId}/" ]' + #13#10 +
    '}');
  WriteUtf8(ManifestDir + '\{#NativeHostName}.firefox.json',
    '{' + #13#10 +
    '  "name": "{#NativeHostName}",' + #13#10 +
    '  "description": "{#AppName} browser bridge",' + #13#10 +
    '  "path": "' + JsonEscape(Root + '\app\DownloadManager.NativeHost.exe') + '",' + #13#10 +
    '  "type": "stdio",' + #13#10 +
    '  "allowed_extensions": [ "{#FirefoxExtensionId}" ]' + #13#10 +
    '}');

  SaveStringToFile(Root + '\INSTALL.txt',
    '{#AppName} {#AppVersion} is installed in:' + #13#10 +
    '  ' + Root + '\app' + #13#10 + #13#10 +
    'Add the extension to your browser (one time).' + #13#10 + #13#10 +
    'Chrome: install Inbound Download Manager Bridge from the Chrome Web Store:' + #13#10 +
    '  {#ChromeStoreUrl}' + #13#10 + #13#10 +
    'Edge: install Inbound Download Manager Bridge from Microsoft Edge Add-ons:' + #13#10 +
    '  {#EdgeStoreUrl}' + #13#10 + #13#10 +
#ifdef FirefoxXpi
    'Firefox: drag this file onto a Firefox window, or open Add-ons and themes,' + #13#10 +
    'the gear menu, Install Add-on From File, and choose it. Firefox updates it after that:' + #13#10 +
    '  ' + Root + '\firefox\{#FirefoxXpiName}' + #13#10 + #13#10 +
#else
    'Firefox: download inbound-bridge-<version>-firefox.xpi from the latest release,' + #13#10 +
    '  https://github.com/Kaan88/inbound/releases/latest' + #13#10 +
    'and drag it onto a Firefox window. Firefox updates it after that.' + #13#10 + #13#10 +
#endif
    'Or, in Chrome, load the unpacked copy that came with this install:' + #13#10 +
    '  1. Open chrome://extensions' + #13#10 +
    '  2. Turn on Developer mode (top right)' + #13#10 +
    '  3. Click "Load unpacked" and select:' + #13#10 +
    '     ' + Root + '\extension' + #13#10 +
    '  4. Check that the extension ID is {#ExtensionId}' + #13#10 + #13#10 +
    'Web UI (off by default):' + #13#10 +
    '  Options > Web UI turns on a page you can open from a browser on this PC or on your home network.' + #13#10 +
    '  Turning it on asks Windows once to allow it through the firewall; without that it works on this PC only.' + #13#10 +
    '  Uninstall does not remove that firewall rule. To remove it, open Windows Security > Firewall & network' + #13#10 +
    '  protection > Advanced settings > Inbound Rules and delete "Inbound Download Manager web UI".' + #13#10 + #13#10 +
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
