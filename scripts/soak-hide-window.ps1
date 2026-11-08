param([Parameter(Mandatory = $true)][string]$Root)
# For scripts\soak.sh and perf-window.sh: closes the main window of a test instance's own window process (found
# by its path under $Root) the way the title bar's close button does, by posting WM_CLOSE to its top-level window
# (the one UI Automation lists for that process). Starts nothing. UI Automation's WindowPattern.Close returns
# without closing this window, and Process.MainWindowHandle can name another of its windows, so neither is used.
Add-Type -AssemblyName UIAutomationClient
Add-Type -Namespace PerfWindow -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
$A = [System.Windows.Automation.AutomationElement]
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name = 'DownloadManager.Desktop.WinUI.exe'")) {
    if (-not $w.ExecutablePath -or -not $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $window = $A::RootElement.FindFirst('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$w.ProcessId)))
    if ($window -and $window.Current.NativeWindowHandle -ne 0) {
        [void][PerfWindow.Native]::PostMessage([IntPtr]$window.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        "closed $($w.ProcessId)"
    }
    else { "no window on screen for $($w.ProcessId)" }
}
