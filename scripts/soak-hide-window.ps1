param([Parameter(Mandatory = $true)][string]$Root)
# For scripts\soak.sh and perf-window.sh: closes the main window of a test instance's own window process (found
# by its path under $Root) the way the title bar's close button does, by posting WM_CLOSE. Starts nothing.
# `dm host open settings` (and `dm host open` before audit 4 L19, which now opens the main window) asks the Host for
# the Options page, so the process can also have an Options window, and UI Automation lists it first. TEST.4 (2026-11-08): a single WM_CLOSE sent to the main window itself reaches AppWindow.Closing
# the first time; the "ignored" first close of earlier runs went to the Options window (WindowPattern.Close was
# checked only with Options closed). So every secondary window is closed first, whether or not the main window
# is on screen (Options can open after it, and the per-download, package and update windows have no main window),
# and the main window, picked by its exact title, last.
# Process.MainWindowHandle can name another of its windows, so it is not used.
Add-Type -AssemblyName UIAutomationClient
Add-Type -Namespace PerfWindow -Name Native -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
$A = [System.Windows.Automation.AutomationElement]
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name = 'DownloadManager.Desktop.WinUI.exe'")) {
    if (-not $w.ExecutablePath -or -not $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $windows = @($A::RootElement.FindAll('Children', (New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, [int]$w.ProcessId))) |
        Where-Object { $_.Current.NativeWindowHandle -ne 0 })
    $main = @($windows | Where-Object { $_.Current.Name -eq 'Inbound Download Manager' })
    if ($windows.Count -eq 0) { "no window on screen for $($w.ProcessId)"; continue }
    foreach ($other in @($windows | Where-Object { $_.Current.Name -ne 'Inbound Download Manager' })) {
        [void][PerfWindow.Native]::PostMessage([IntPtr]$other.Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
        "closed secondary window 0x{0:X} of {1}" -f [int64]$other.Current.NativeWindowHandle, $w.ProcessId
    }
    if ($main.Count -eq 0) { "no main window on screen for $($w.ProcessId)"; continue }
    [void][PerfWindow.Native]::PostMessage([IntPtr]$main[0].Current.NativeWindowHandle, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    "closed $($w.ProcessId)"
}
