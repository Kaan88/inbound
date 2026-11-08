param([Parameter(Mandatory = $true)][string]$Root)
# For scripts\soak.sh: closes the main window of the soak's own window process (found by its path under
# $Root) through UI Automation, the way the title bar's close button does. Starts nothing.
Add-Type -AssemblyName UIAutomationClient
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$A = [System.Windows.Automation.AutomationElement]
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name = 'DownloadManager.Desktop.WinUI.exe'")) {
    if (-not $w.ExecutablePath -or -not $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $window = $A::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$w.ProcessId)))
    if ($window) { $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).Close(); "closed $($w.ProcessId)" }
    else { "no window for $($w.ProcessId)" }
}
