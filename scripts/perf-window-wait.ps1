param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Ready,
    [int]$TimeoutSeconds = 60,
    [switch]$Gone,
    [switch]$Hidden
)
# Read-only helper for scripts\perf-window.sh (PERF.5). Starts nothing. Writes $Ready when it is polling, then
# waits until a window process from $Root has a top-level window on screen (with -Gone, until none of its
# window processes is left; with -Hidden, until none has a window on screen) and prints the Unix time in
# milliseconds when it saw that.
Add-Type -AssemblyName UIAutomationClient
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$A = [System.Windows.Automation.AutomationElement]
function Windows {
    @(Get-CimInstance Win32_Process -Filter "Name = 'DownloadManager.Desktop.WinUI.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
}
Set-Content -LiteralPath $Ready -Value 'ready'
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    $processes = Windows
    if ($Gone) {
        if ($processes.Count -eq 0) { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); exit 0 }
    }
    elseif ($Hidden) {
        $shown = @($processes | Where-Object { $A::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$_.ProcessId))) })
        if ($shown.Count -eq 0) { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); exit 0 }
    }
    else {
        foreach ($w in $processes) {
            $window = $A::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$w.ProcessId)))
            if ($window -and -not $window.Current.IsOffscreen) { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); exit 0 }
        }
    }
    Start-Sleep -Milliseconds 25
}
'timeout'
exit 1
