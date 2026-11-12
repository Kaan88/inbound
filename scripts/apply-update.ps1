[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDir,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$ResultPath,
    [int]$HostPid = 0,
    [switch]$NoStart,
    # Setup's entry in Settings > Apps (Inno Setup names it after the AppId in installer\DownloadManager.iss).
    # Tests pass a key of their own; nothing else should.
    [string]$UninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{6F4B7C2E-3D91-4E8A-9B57-2C1D0A8E5F13}_is1'
)

# Replaces the installed app with the one in $PackageDir. The new files are staged beside the
# old ones and swapped in with renames, never by deleting the live folder, so any failure can put
# the old version back instead of leaving a half-deleted one.

$ErrorActionPreference = "Stop"
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\')) {
    throw "InstallRoot must be an application directory, not a drive root."
}
$appDir = Join-Path $InstallRoot "app"
$backupDir = Join-Path $InstallRoot "app-rollback"
$newAppDir = Join-Path $InstallRoot "app-new"
$extensionDir = Join-Path $InstallRoot "extension"
$extensionBackupDir = Join-Path $InstallRoot "extension-rollback"
$newExtensionDir = Join-Path $InstallRoot "extension-new"
$metadataPath = Join-Path $InstallRoot "install.json"
$metadataBackup = $null
$rootScriptBackups = @{}
$appSwapped = $false
$extensionSwapped = $false
$script:frontendsWereOpen = $false
$script:waitedForHost = $false
$newProcesses = [Collections.Generic.List[System.Diagnostics.Process]]::new()
$appPrefix = [IO.Path]::GetFullPath($appDir).TrimEnd('\') + '\'

try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
    Start-Transcript -LiteralPath (Join-Path (Split-Path -Parent $ResultPath) "apply-update.log") -Force | Out-Null
}
catch { }

function Write-Result([string]$status, [string]$errorText) {
    $payload = [ordered]@{
        status = $status
        version = $Version
        error = $errorText
        at = (Get-Date).ToString("O")
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $ResultPath) -Force | Out-Null
    $temp = "$ResultPath.tmp"
    [System.IO.File]::WriteAllText($temp, ($payload | ConvertTo-Json -Depth 3), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temp -Destination $ResultPath -Force
}

function Get-InstallProcesses {
    @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($appPrefix, [StringComparison]::OrdinalIgnoreCase)
    })
}

# A window the user has up comes back after the update. The update's own progress window does not,
# and neither does a main window closed to the tray: that process is still running, but hidden.
function Note-OpenWindow($process) {
    if ($process.CommandLine -like '*update-progress*') { return }
    $window = Get-Process -Id $process.ProcessId -ErrorAction SilentlyContinue
    if ($window -and $window.MainWindowHandle -ne [IntPtr]::Zero) { $script:frontendsWereOpen = $true }
}

# Every process running from app\ must have exited, not just been told to: a killed process
# keeps its executable and DLLs locked until Windows has torn it down.
function Wait-InstallIdle([int]$seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        $running = @(Get-InstallProcesses)
        if ($running.Count -eq 0) { return }
        foreach ($process in $running) {
            if ($process.Name -eq 'DownloadManager.Desktop.WinUI.exe') { Note-OpenWindow $process }
            # The old Host was asked to exit and is waited for; anything else from this install
            # is a UI window, the browser bridge or the CLI, which must not hold files now.
            # A Host started since then (by the browser bridge, say) would hold files too.
            if ($process.ProcessId -ne $HostPid) {
                Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
            }
        }
        if ((Get-Date) -gt $deadline) {
            throw ("These programs did not exit for the update: " +
                (($running | ForEach-Object Name | Sort-Object -Unique) -join ', ') +
                ". Application files were not replaced.")
        }
        Start-Sleep -Milliseconds 250
    }
}

# A directory rename is atomic: the folder is either the old or the new version, never a mix.
# It fails while something holds a file inside, so it is retried briefly (antivirus scans).
function Move-Directory([string]$from, [string]$to, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($true) {
        try { [IO.Directory]::Move($from, $to); return }
        catch {
            if ((Get-Date) -gt $deadline) {
                $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
                throw "Could not rename '$from' to '$to' because a file in it is in use: $reason"
            }
            Start-Sleep -Milliseconds 500
        }
    }
}

# Moves the named items of $from into $to, noting each one in $moved as soon as it has gone, so
# a failure part way leaves an exact record of what to undo.
function Move-Items([string]$from, [string]$to, [string[]]$names, [Collections.Generic.List[string]]$moved, [int]$seconds = 10) {
    New-Item -ItemType Directory -Path $to -Force | Out-Null
    foreach ($name in $names) {
        $source = Join-Path $from $name
        $target = Join-Path $to $name
        $isFolder = Test-Path -LiteralPath $source -PathType Container
        $deadline = (Get-Date).AddSeconds($seconds)
        while ($true) {
            try {
                if ($isFolder) { [IO.Directory]::Move($source, $target) } else { [IO.File]::Move($source, $target) }
                if ($null -ne $moved) { $moved.Add($name) }
                break
            }
            catch {
                if ((Get-Date) -gt $deadline) {
                    $reason = if ($_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
                    throw "Could not move '$source': $reason"
                }
                Start-Sleep -Milliseconds 500
            }
        }
    }
}

# Setup records the version it installed in its uninstall entry, which Settings > Apps shows. An update
# swaps the files without Setup, so the entry is brought up to date here (UP.B2). Only an entry Setup made
# for this same folder is touched: a script install has none, and one is never created.
$script:uninstallValuesBefore = $null
$uninstallVersionNames = @('DisplayVersion', 'MajorVersion', 'MinorVersion', 'VersionMajor', 'VersionMinor')

function Update-UninstallEntry {
    if ([string]::IsNullOrWhiteSpace($UninstallKey) -or -not (Test-Path -LiteralPath $UninstallKey)) { return }
    $entry = Get-ItemProperty -LiteralPath $UninstallKey
    $location = [string]$entry.InstallLocation
    if ([string]::IsNullOrWhiteSpace($location) -or
        -not [string]::Equals([IO.Path]::GetFullPath($location).TrimEnd('\'), $InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
        return
    }
    $before = @{}
    foreach ($name in $uninstallVersionNames) {
        if ($null -ne $entry.PSObject.Properties[$name]) { $before[$name] = $entry.$name }
    }
    $script:uninstallValuesBefore = $before
    Set-ItemProperty -LiteralPath $UninstallKey -Name DisplayVersion -Value $Version
    # Setup also writes the major and minor numbers as DWORDs; they follow when they are there.
    $parsed = $null
    if ([Version]::TryParse(($Version -split '[-+]')[0], [ref]$parsed)) {
        foreach ($name in @('MajorVersion', 'VersionMajor')) {
            if ($before.ContainsKey($name)) { Set-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $parsed.Major -Type DWord }
        }
        foreach ($name in @('MinorVersion', 'VersionMinor')) {
            if ($before.ContainsKey($name)) { Set-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $parsed.Minor -Type DWord }
        }
    }
}

function Restore-UninstallEntry {
    $before = $script:uninstallValuesBefore
    if ($null -eq $before) { return }
    foreach ($name in $before.Keys) {
        $type = if ($name -eq 'DisplayVersion') { 'String' } else { 'DWord' }
        Set-ItemProperty -LiteralPath $UninstallKey -Name $name -Value $before[$name] -Type $type
    }
    # Setup always writes DisplayVersion; if it was somehow missing, the update's own value goes again.
    if (-not $before.ContainsKey('DisplayVersion')) { Remove-ItemProperty -LiteralPath $UninstallKey -Name DisplayVersion -ErrorAction SilentlyContinue }
}

function Get-ItemNames([string]$folder) { @(Get-ChildItem -LiteralPath $folder -Force | ForEach-Object Name) }

# What each in-place swap has moved so far, by live folder.
$script:inPlaceSwaps = @{}

# Puts $new in place of $live and keeps $live as $backup. Renaming the whole folder is atomic, but
# Windows refuses it while any program has the folder as its current directory. Programs opened
# from older versions of this app inherited app\ that way and may still be running (a downloaded
# installer, WinRAR). The files inside can still be moved, so that is the fallback.
function Swap-In([string]$live, [string]$new, [string]$backup) {
    if (-not (Test-Path -LiteralPath $live)) { Move-Directory $new $live; return }
    try { Move-Directory $live $backup 10 }
    catch {
        Write-Warning "$($_.Exception.Message) Replacing its contents instead."
        # A program may have started during those seconds (a Host, from the browser bridge). Its
        # files can be moved while it runs, so it must go first or it would run on from the backup.
        Wait-InstallIdle 30
        $record = @{
            Old = [Collections.Generic.List[string]]::new()
            New = [Collections.Generic.List[string]]::new()
        }
        $script:inPlaceSwaps[$live] = $record
        Move-Items $live $backup (Get-ItemNames $live) $record.Old
        Move-Items $new $live (Get-ItemNames $new) $record.New
        Remove-Item -LiteralPath $new -Recurse -Force
        return
    }
    Move-Directory $new $live
}

# Undoes Swap-In from wherever it stopped. An in-place swap is undone item by item, touching only
# what it moved: anything else in $live never left, and may be the very file that stopped it.
function Restore-Folder([string]$live, [string]$backup) {
    if (-not (Test-Path -LiteralPath $backup)) { return }
    $failed = "$live.failed-" + [guid]::NewGuid().ToString("N")
    $record = $script:inPlaceSwaps[$live]
    if ($null -ne $record) {
        Move-Items $live $failed $record.New.ToArray() $null
        # The new version may have created a file the old one also had (ui-trace.log), which would
        # stop the old one going back. Anything of that name now in $live was made since the swap.
        # One that cannot be moved costs that one file, not the rest of the old version.
        $stuck = @()
        foreach ($stray in @($record.Old | Where-Object { Test-Path -LiteralPath (Join-Path $live $_) })) {
            try { Move-Items $live $failed @($stray) $null }
            catch { Write-Warning $_.Exception.Message; $stuck += $stray }
        }
        Move-Items $backup $live @($record.Old | Where-Object { $stuck -notcontains $_ }) $null
        Remove-Item -LiteralPath $backup -Recurse -Force
        return
    }
    if (Test-Path -LiteralPath $live) { Move-Directory $live $failed 5 }
    Move-Directory $backup $live
}

function Remove-Directory([string]$path, [int]$seconds = 30) {
    if (-not (Test-Path -LiteralPath $path)) { return }
    # Renamed first, so a partly deleted folder never keeps a name the app uses.
    $doomed = "$path.delete-" + [guid]::NewGuid().ToString("N")
    Move-Directory $path $doomed $seconds
    try { Remove-Item -LiteralPath $doomed -Recurse -Force }
    catch { Write-Warning "Left '$doomed' behind: $($_.Exception.Message)" }
}

function Start-InstalledProgram([string]$name, [string]$arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = Join-Path $appDir $name
    $start.Arguments = $arguments
    # Not app\ itself: a process whose current directory is app\ holds that folder.
    $start.WorkingDirectory = $InstallRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $newProcesses.Add([System.Diagnostics.Process]::Start($start))
}

function Start-InstalledApplication {
    Start-InstalledProgram "DownloadManager.Host.exe" "--background"
    if ($script:frontendsWereOpen) { Start-InstalledProgram "DownloadManager.Desktop.WinUI.exe" "" }
}

# The new version must survive starting. A Host or window that crashes straight away means this
# version cannot run on this PC, and the rollback brings the old one back. Exit code 0 is a clean
# exit (for example another instance was found), which is not a failure.
function Confirm-Started([int]$seconds = 8) {
    $deadline = (Get-Date).AddSeconds($seconds)
    foreach ($process in $newProcesses) {
        $remaining = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
        if ($process.WaitForExit($remaining) -and $process.ExitCode -ne 0) {
            throw ("The new version did not start: {0} exited with code {1}." -f
                [IO.Path]::GetFileName($process.StartInfo.FileName), $process.ExitCode)
        }
    }
}

# Marks this version as one the user chose to skip. A Host from before 0.3.3 cannot read the result
# file and installs again the moment it starts, but it does respect a skipped version, and it can
# still install this one on request. Written while no Host is running, so nothing overwrites it.
function Set-SkippedVersion {
    $settingsPath = Join-Path (Split-Path -Parent (Split-Path -Parent $ResultPath)) "settings.json"
    $settings = [IO.File]::ReadAllText($settingsPath) | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName skippedUpdateVersion -NotePropertyValue $Version -Force
    $json = $settings | ConvertTo-Json -Depth 32
    $json | ConvertFrom-Json | Out-Null
    [IO.File]::WriteAllText("$settingsPath.tmp", $json, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath "$settingsPath.tmp" -Destination $settingsPath -Force
}

# The Host names this process as the one to watch just before this script can start, unless it is
# too old to read the result. Without that, nothing will ever report a failure.
function Test-HostIsWatching {
    try { ([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $ResultPath) "install-pending.json")) | ConvertFrom-Json).helperProcessId -eq $PID }
    catch { $false }
}

function Show-Failure([string]$text) {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($text, "Inbound Download Manager update", 'OK', 'Error') | Out-Null
    }
    catch { }
}

try {
    # Never hold a directory this script renames as our current directory.
    Set-Location -LiteralPath $PackageDir
    [Environment]::CurrentDirectory = (Get-Location).ProviderPath

    foreach ($targetPath in @($appDir, $backupDir, $newAppDir, $extensionDir, $extensionBackupDir, $newExtensionDir)) {
        if (-not [IO.Path]::GetFullPath($targetPath).StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Update target is outside the installation directory."
        }
        if ((Test-Path -LiteralPath $targetPath) -and
            ((Get-Item -LiteralPath $targetPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Update targets must not be directory links."
        }
    }

    $packageApp = Join-Path $PackageDir "app"
    if (-not (Test-Path (Join-Path $packageApp "DownloadManager.Host.exe"))) {
        throw "The update package has no application files."
    }

    # UI windows go first: an older window restarts the Host when it loses its connection,
    # which would race the wait below. They are reopened after the update.
    foreach ($frontend in @(Get-InstallProcesses | Where-Object Name -eq 'DownloadManager.Desktop.WinUI.exe')) {
        Note-OpenWindow $frontend
        Stop-Process -Id $frontend.ProcessId -Force -ErrorAction SilentlyContinue
    }

    if ($HostPid -gt 0 -and (Get-Process -Id $HostPid -ErrorAction SilentlyContinue)) {
        $script:waitedForHost = $true
        Wait-Process -Id $HostPid -Timeout 120 -ErrorAction SilentlyContinue
        if (Get-Process -Id $HostPid -ErrorAction SilentlyContinue) {
            throw "The Host did not exit for the update. Application files were not replaced."
        }
    }

    # Folders left by an earlier failed update; best effort, they are never used.
    Get-ChildItem -LiteralPath $InstallRoot -Directory -Force |
        Where-Object { $_.Name -like 'app.failed-*' -or $_.Name -like 'extension.failed-*' -or $_.Name -like '*.delete-*' } |
        ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force } catch { } }

    # Stage the new version beside the old one; nothing installed has changed yet.
    Remove-Directory $newAppDir
    Copy-Item -LiteralPath $packageApp -Destination $newAppDir -Recurse -Force
    $packageExtension = Join-Path $PackageDir "extension"
    $hasNewExtension = Test-Path (Join-Path $packageExtension "manifest.json")
    if ($hasNewExtension) {
        Remove-Directory $newExtensionDir
        Copy-Item -LiteralPath $packageExtension -Destination $newExtensionDir -Recurse -Force
    }
    if (Test-Path $metadataPath) { $metadataBackup = Get-Content -LiteralPath $metadataPath -Raw }

    # The previous rollback copy goes only now that this update's own is about to exist. The check
    # comes after it, right before the swap: deleting takes a moment, and a Host started meanwhile
    # (by the browser bridge) would hold files.
    Remove-Directory $backupDir
    Wait-InstallIdle 60
    $appSwapped = $true
    Swap-In $appDir $newAppDir $backupDir
    if ($hasNewExtension) {
        Remove-Directory $extensionBackupDir
        $extensionSwapped = $true
        Swap-In $extensionDir $newExtensionDir $extensionBackupDir
    }
    elseif (Test-Path -LiteralPath $extensionDir) {
        # Updates no longer carry the unpacked extension (the extensions are in the stores); best effort, the
        # old copy is only a folder to remove and is no reason to undo the update.
        try { Remove-Directory $extensionDir } catch { }
    }

    # Setup puts these beside app\, where swapping app\ never reaches them, and repair.ps1 would go on
    # running the version Setup installed. A script install has no copies, so none is added. An old copy
    # still works, so one that cannot be replaced is no reason to undo the update.
    foreach ($name in @("register-native-host.ps1", "repair.ps1", "stop-for-setup.ps1")) {
        $packagedScript = Join-Path $PackageDir $name
        $installedScript = Join-Path $InstallRoot $name
        if (-not (Test-Path -LiteralPath $packagedScript) -or -not (Test-Path -LiteralPath $installedScript)) { continue }
        try {
            $originalScript = [IO.File]::ReadAllBytes($installedScript)
            Copy-Item -LiteralPath $packagedScript -Destination "$installedScript.tmp" -Force
            Move-Item -LiteralPath "$installedScript.tmp" -Destination $installedScript -Force
            $rootScriptBackups[$installedScript] = $originalScript
        }
        catch {
            Write-Warning "Could not update ${name}: $($_.Exception.Message)"
            Remove-Item -LiteralPath "$installedScript.tmp" -Force -ErrorAction SilentlyContinue
        }
    }

    $register = Join-Path $PackageDir "register-native-host.ps1"
    if (Test-Path $register) {
        & $register -AppDir $appDir -Force
    }

    # Publish the version only after every file is in place.
    $metadata = [ordered]@{
        version = $Version
        channel = "stable"
        installRoot = $InstallRoot
        installedAt = (Get-Date).ToString("O")
    }
    [System.IO.File]::WriteAllText("$metadataPath.tmp", ($metadata | ConvertTo-Json -Depth 3),
        (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath "$metadataPath.tmp" -Destination $metadataPath -Force

    # The version shown in Settings > Apps. A wrong number there is no reason to undo the update.
    try { Update-UninstallEntry }
    catch { Write-Warning "Could not update the version in Settings > Apps: $($_.Exception.Message)" }

    # Keep the rollback copy through restart. The next update replaces it only after its
    # Host has exited, so a launch failure cannot discard both versions.
    if (-not $NoStart) {
        Start-InstalledApplication
        Confirm-Started
    }

    # Last of all, so "succeeded" means the new version is in place and running. The new Host
    # looks again a few seconds after it starts. Not being able to say so is no reason to undo it.
    try { Write-Result "succeeded" "" }
    catch { Write-Warning "Could not record the result: $($_.Exception.Message)" }

    try { Stop-Transcript | Out-Null } catch { }
    exit 0
}
catch {
    $message = $_.Exception.Message
    Write-Warning "Update failed: $message"
    # Asked first: a Host started meanwhile clears the mark once the result below is written.
    $hostCannotTell = $HostPid -gt 0 -and -not (Test-HostIsWatching)
    foreach ($newProcess in $newProcesses) {
        try { if (-not $newProcess.HasExited) { $newProcess.Kill(); $newProcess.WaitForExit(10000) | Out-Null } }
        catch { $message = "$message; restart cleanup error: $($_.Exception.Message)" }
    }
    # Each step is tried whatever became of the one before: the more that goes back, the better.
    $appRestored = $true
    $extensionRestored = $true
    if ($appSwapped) {
        # Something still running does not stop the attempt; the moves retry on their own.
        try { Wait-InstallIdle 30 }
        catch { Write-Warning $_.Exception.Message }
        try { Restore-Folder $appDir $backupDir }
        catch {
            $appRestored = $false
            $message = "$message; rollback error: $($_.Exception.Message)"
        }
    }
    if ($extensionSwapped) {
        try { Restore-Folder $extensionDir $extensionBackupDir }
        catch {
            $extensionRestored = $false
            $message = "$message; extension rollback error: $($_.Exception.Message)"
        }
    }
    foreach ($installedScript in @($rootScriptBackups.Keys)) {
        try { [IO.File]::WriteAllBytes($installedScript, $rootScriptBackups[$installedScript]) }
        catch { $message = "$message; script rollback error: $($_.Exception.Message)" }
    }
    if ($null -ne $metadataBackup) {
        try { [System.IO.File]::WriteAllText($metadataPath, $metadataBackup, (New-Object System.Text.UTF8Encoding($false))) }
        catch { $message = "$message; metadata rollback error: $($_.Exception.Message)" }
    }
    try { Restore-UninstallEntry }
    catch { Write-Warning "Could not put back the version in Settings > Apps: $($_.Exception.Message)" }
    # Recorded before the restart, because the restored Host reads it as it starts.
    try { Write-Result "failed" $message } catch { }
    $newProcesses.Clear()
    # A Host too slow to exit is still shutting down. Another started now would find it and step
    # aside, and then nothing would be running. A current Host stays up if this script fails while
    # it is still waiting to hear; an older one always exits.
    if (($script:waitedForHost -or $hostCannotTell) -and $HostPid -gt 0 -and
        (Get-Process -Id $HostPid -ErrorAction SilentlyContinue)) {
        Wait-Process -Id $HostPid -Timeout 300 -ErrorAction SilentlyContinue
    }
    if ($hostCannotTell) {
        try { Set-SkippedVersion }
        catch { Write-Warning "Could not skip this version: $($_.Exception.Message)" }
    }
    $restarted = $false
    if (-not $NoStart -and (Test-Path (Join-Path $appDir "DownloadManager.Host.exe"))) {
        try { Start-InstalledApplication; $restarted = $true }
        catch {
            $message = "$message; restart error: $($_.Exception.Message)"
            try { Write-Result "failed" $message } catch { }
        }
    }

    try { Stop-Transcript | Out-Null } catch { }
    # A restarted Host reports an ordinary failure itself. It cannot tell that the install is damaged,
    # and without a Host, or with one too old to read the result, nothing else would say anything.
    if (-not $NoStart -and (-not $restarted -or -not $appRestored -or -not $extensionRestored -or $hostCannotTell)) {
        $outcome = if (-not $appRestored) { "the previous version could not be put back" }
            elseif (-not $extensionRestored) { "the previous browser extension could not be put back" }
            elseif (-not $restarted) { "the app could not be restarted" }
            else { "the previous version is still in use" }
        $repair = if (-not $appRestored) { "`n`nRun Setup again to repair the installation. Files from the previous version are in:`n$backupDir" }
            elseif (-not $extensionRestored) { "`n`nRun Setup again to repair the installation. Files from the previous extension are in:`n$extensionBackupDir" }
            else { "" }
        Show-Failure ("The update to $Version could not be installed and $outcome.`n`n$message$repair")
    }
    exit 1
}
