<#
  Closes this installation's running processes before Setup replaces or removes
  its files. Restart Manager cannot close the tray-only Host, so Setup runs this
  first. The Host is asked to exit through its own IPC shutdown (the same path
  as tray Exit, so checkpoints are flushed) and is never force-killed. The
  window-only frontends and the stateless browser bridge are closed directly,
  as the updater does. Exit code 0 means nothing from this install is running.
#>
param([Parameter(Mandatory)][string]$AppDir, [int]$TimeoutSeconds = 30)
$ErrorActionPreference = 'Stop'

$prefix = [IO.Path]::GetFullPath($AppDir).TrimEnd('\') + '\'
function Get-Installed([string]$name) {
    @(Get-CimInstance Win32_Process -Filter "Name = '$name'" | Where-Object {
        $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    })
}
function Stop-Installed([string]$name) {
    foreach ($process in Get-Installed $name) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
}

try {
    # Older frontends restart the Host when it disconnects; close them first.
    Stop-Installed 'DownloadManager.Desktop.WinUI.exe'

    if (@(Get-Installed 'DownloadManager.Host.exe').Count -gt 0) {
        $cli = Join-Path $AppDir 'DownloadManager.Cli.exe'
        if (Test-Path $cli) {
            # Setup's environment selects the default (installed) instance.
            Remove-Item Env:DOWNLOADMANAGER_INSTANCE -ErrorAction SilentlyContinue
            Remove-Item Env:DOWNLOADMANAGER_DATA -ErrorAction SilentlyContinue
            # Windows PowerShell 5.1 turns native stderr into terminating errors under 'Stop'.
            $ErrorActionPreference = 'Continue'
            & $cli host shutdown --no-start *> $null
            $ErrorActionPreference = 'Stop'
        }
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while (@(Get-Installed 'DownloadManager.Host.exe').Count -gt 0 -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 300
        }
    }

    # The browser bridge and the web UI bridge can briefly outlive the Host and would keep their files locked.
    Stop-Installed 'DownloadManager.NativeHost.exe'
    Stop-Installed 'DownloadManager.Server.exe'
    Stop-Installed 'DownloadManager.Desktop.WinUI.exe'
    Start-Sleep -Milliseconds 300

    $left = @('DownloadManager.Host.exe', 'DownloadManager.Desktop.WinUI.exe', 'DownloadManager.NativeHost.exe', 'DownloadManager.Server.exe' |
        Where-Object { @(Get-Installed $_).Count -gt 0 })
    if ($left.Count -gt 0) {
        [Console]::Error.WriteLine("Still running: $($left -join ', ')")
        exit 1
    }
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
