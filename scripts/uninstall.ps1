[CmdletBinding()]
param(
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA "Programs\Inbound Download Manager"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$installRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\')) {
    throw "InstallRoot must be an application directory, not a drive root."
}
$appDir = Join-Path $installRoot "app"

# Closed as Setup closes them (stop-for-setup.ps1): this install's Host is asked to finish its writes and exit, and is
# waited for; the window, the bridges and the torrent worker are closed. Run in its own process: it ends with exit.
# Works when the app is not running, which the earlier "host shutdown" call under Stop did not (audit 4 M5).
$stopScript = @((Join-Path $installRoot "stop-for-setup.ps1"), (Join-Path $PSScriptRoot "stop-for-setup.ps1")) |
    Where-Object { Test-Path $_ } | Select-Object -First 1
$stopped = $false
if ($stopScript -and (Test-Path $appDir)) {
    $powershell = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
    # Windows PowerShell 5.1 turns native stderr into a terminating error under 'Stop' when output is captured, which
    # would end the script before the fallback below; a failure here only means the fallback runs.
    $ErrorActionPreference = "Continue"
    try {
        & $powershell -NoProfile -ExecutionPolicy Bypass -File $stopScript -AppDir $appDir
        $stopped = $LASTEXITCODE -eq 0
    }
    catch { $stopped = $false }
    finally { $ErrorActionPreference = "Stop" }
}
if (-not $stopped) {
    # Only copies running from this install: a test instance or a build elsewhere is someone else's.
    $ownRoot = [IO.Path]::GetFullPath($installRoot).TrimEnd('\') + '\'
    Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI, DownloadManager.NativeHost, DownloadManager.Server, inbound-torrent -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($ownRoot, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

# Chrome and Edge each have a key pointing at one manifest; Firefox has its own key and manifest.
# Keep in step with BrowserBridge.NativeHostKeyPaths and BrowserBridge.FirefoxNativeHostKeyPath.
# A key is removed only when it points at this install's manifest or at a file that no longer exists,
# so another program's working registration under the same name is left alone.
function Test-NativeKeyIsOurs([string]$registered, [string]$ownManifest) {
    if (-not $registered) { return $true }
    try {
        if (-not (Test-Path -LiteralPath $registered)) { return $true }
        return [IO.Path]::GetFullPath($registered) -ieq [IO.Path]::GetFullPath($ownManifest)
    }
    catch { return $false }
}

$hostName = "com.kaank.downloadmanager"
$chromiumManifest = Join-Path $installRoot "native-host\$hostName.json"
$firefoxManifest = Join-Path $installRoot "native-host\$hostName.firefox.json"
$nativeKeys = [ordered]@{
    "Chrome"  = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
    "Edge"    = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    "Firefox" = "HKCU:\Software\Mozilla\NativeMessagingHosts\$hostName"
}
foreach ($browser in $nativeKeys.Keys) {
    $nativeKey = $nativeKeys[$browser]
    $ownManifest = if ($browser -eq "Firefox") { $firefoxManifest } else { $chromiumManifest }
    if (-not (Test-Path $nativeKey)) { continue }
    $registered = (Get-Item $nativeKey).GetValue("")
    if (-not (Test-NativeKeyIsOurs $registered $ownManifest)) {
        Write-Warning "Left the $browser native-host registration alone: it points at $registered, not at this install."
        continue
    }
    Remove-Item $nativeKey -Recurse -Force
    Write-Host "Removed $browser native-host registration."
}

# The magnet and .torrent handler (Host TorrentHandlerRegistration), removed as Setup's uninstaller does
# (RemoveTorrentHandler): a class only when it opens this install's window, the shared entries once none is left.
$uiExe = Join-Path $appDir "DownloadManager.Desktop.WinUI.exe"
function Test-ClassIsOurs([string]$subKey) {
    $command = Get-ItemProperty -LiteralPath "HKCU:\$subKey\shell\open\command" -ErrorAction SilentlyContinue
    if (-not $command) { return $false }
    $text = [string]$command.'(default)'
    return $text.Trim().StartsWith('"' + $uiExe + '"', [StringComparison]::OrdinalIgnoreCase)
}
foreach ($class in @('Software\Classes\magnet', 'Software\Classes\Inbound.Magnet', 'Software\Classes\Inbound.Torrent')) {
    if (Test-ClassIsOurs $class) {
        Remove-Item -LiteralPath "HKCU:\$class" -Recurse -Force
        Write-Host "Removed the $($class.Split('\')[-1]) handler."
    }
}
if (-not (Test-Path 'HKCU:\Software\Classes\Inbound.Torrent')) {
    Remove-ItemProperty -LiteralPath 'HKCU:\Software\Classes\.torrent\OpenWithProgids' -Name 'Inbound.Torrent' -ErrorAction SilentlyContinue
    $torrentKey = Get-Item -LiteralPath 'HKCU:\Software\Classes\.torrent' -ErrorAction SilentlyContinue
    if ($torrentKey -and [string]$torrentKey.GetValue('') -ieq 'Inbound.Torrent') {
        $writable = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Classes\.torrent', $true)
        if ($writable) { $writable.DeleteValue('', $false); $writable.Dispose() }
    }
    foreach ($empty in @('HKCU:\Software\Classes\.torrent\OpenWithProgids', 'HKCU:\Software\Classes\.torrent')) {
        $key = Get-Item -LiteralPath $empty -ErrorAction SilentlyContinue
        if ($key -and $key.SubKeyCount -eq 0 -and $key.ValueCount -eq 0) { Remove-Item -LiteralPath $empty -Force }
    }
}
if (-not (Test-Path 'HKCU:\Software\Classes\Inbound.Torrent') -and -not (Test-Path 'HKCU:\Software\Classes\Inbound.Magnet')) {
    $registered = Get-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name 'Inbound Download Manager' -ErrorAction SilentlyContinue
    if ($registered -and $registered.'Inbound Download Manager' -ieq 'Software\Inbound Download Manager\Capabilities') {
        Remove-ItemProperty -LiteralPath 'HKCU:\Software\RegisteredApplications' -Name 'Inbound Download Manager'
    }
    Remove-Item -LiteralPath 'HKCU:\Software\Inbound Download Manager\Capabilities' -Recurse -Force -ErrorAction SilentlyContinue
    $vendor = Get-Item -LiteralPath 'HKCU:\Software\Inbound Download Manager' -ErrorAction SilentlyContinue
    if ($vendor -and $vendor.SubKeyCount -eq 0 -and $vendor.ValueCount -eq 0) { Remove-Item -LiteralPath 'HKCU:\Software\Inbound Download Manager' -Force }
}

# The torrent firewall rule Options > Torrents may have added, as Setup's uninstaller removes it (RemoveTorrentFirewallRule):
# found by its name without elevation, and only when it is this install's worker; netsh then asks once for elevation.
$worker = Join-Path $appDir "inbound-torrent.exe"
$ruleName = "Inbound Download Manager torrents"
$ruleIsOurs = $false
try {
    $rule = (New-Object -ComObject HNetCfg.FwPolicy2).Rules.Item($ruleName)
    $ruleIsOurs = [string]$rule.ApplicationName -ieq $worker
}
catch { $ruleIsOurs = $false }
if ($ruleIsOurs) {
    try {
        $netsh = Join-Path $env:SystemRoot "System32\netsh.exe"
        Start-Process -FilePath $netsh -Verb RunAs -Wait -WindowStyle Hidden `
            -ArgumentList "advfirewall firewall delete rule name=`"$ruleName`" dir=in program=`"$worker`""
        Write-Host "Removed the torrent firewall rule."
    }
    catch {
        Write-Warning "Left the torrent firewall rule '$ruleName': $($_.Exception.Message)"
    }
}

$runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if (Test-Path $runKey) {
    Remove-ItemProperty -Path $runKey -Name "DownloadManagerHost" -ErrorAction SilentlyContinue
}

# Current name plus the shortcut created by versions named plain "Download Manager".
foreach ($name in @("Inbound Download Manager.lnk")) {
    $shortcut = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\$name"
    if (Test-Path $shortcut) {
        Remove-Item $shortcut -Force
    }
}

if (Test-Path $appDir) {
    Remove-Item $appDir -Recurse -Force
    Write-Host "Removed application files."
}
# An unpacked extension folder an install from before the store listings left (Setup no longer ships one).
$extension = Join-Path $installRoot "extension"
if (Test-Path $extension) {
    Remove-Item $extension -Recurse -Force
    Write-Host "Removed an old unpacked extension folder. If that copy is still loaded in the browser, remove it there too."
}

# What Setup's [UninstallDelete] and an in-app update leave beside app\ (audit 4 M6): keep in step with the .iss.
foreach ($leftover in @('native-host', 'install.json', 'INSTALL.txt', 'firefox', 'app-rollback', 'extension-rollback',
        'app-new', 'extension-new', 'apply-update.ps1', 'repair.ps1', 'register-native-host.ps1', 'stop-for-setup.ps1')) {
    Remove-Item (Join-Path $installRoot $leftover) -Recurse -Force -ErrorAction SilentlyContinue
}
foreach ($pattern in @('app.failed-*', 'extension.failed-*', '*.delete-*')) {
    Get-ChildItem -LiteralPath $installRoot -Filter $pattern -Force -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}
if (-not (Get-ChildItem $installRoot -Force -ErrorAction SilentlyContinue)) {
    Remove-Item $installRoot -Force -ErrorAction SilentlyContinue
}

$dataRoot = Join-Path $env:LOCALAPPDATA "Inbound Download Manager"
if ($RemoveData) {
    Remove-Item $dataRoot -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Removed application data and the download list. Downloaded files and their .part files were not touched."
}
else {
    Write-Host "Kept download list and settings under $dataRoot (use -RemoveData to delete them)."
}
