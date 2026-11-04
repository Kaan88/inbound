[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class U7Native {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
  [DllImport("shcore.dll")] public static extern int GetScaleFactorForMonitor(IntPtr hmon, out int scale);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static IntPtr InputSite(IntPtr parent) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h, l) => {
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() == "InputSiteWindowClass") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
}
'@

$failures = 0
function Check([bool]$condition, [string]$message) {
    if ($condition) { Write-Output "PASS $message"; return }
    $script:failures++
    Write-Output "FAIL $message"
}

$proc = Get-Process DownloadManager.Desktop.WinUI -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pc)
if (-not $win) { Write-Output 'NO WINDOW'; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle

function ById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# 1. Interactive control names.
$types = @(
    [System.Windows.Automation.ControlType]::Button,
    [System.Windows.Automation.ControlType]::Edit,
    [System.Windows.Automation.ControlType]::ComboBox,
    [System.Windows.Automation.ControlType]::List,
    [System.Windows.Automation.ControlType]::CheckBox,
    [System.Windows.Automation.ControlType]::Slider
)
$unnamed = @()
foreach ($type in $types) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    foreach ($element in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.IsOffscreen) { continue }
        if ([string]::IsNullOrWhiteSpace($element.Current.Name)) {
            $unnamed += "$($type.ProgrammaticName)/$($element.Current.AutomationId)"
        }
    }
}
Write-Output "UIA unnamed controls: $($unnamed -join ', ')"
Check ($unnamed.Count -eq 0) 'all visible interactive controls have names'

$status = ById 'StatusText'
$connection = ById 'ConnectionText'
$jobs = ById 'JobsList'
$categories = ById 'CategoryList'
Write-Output "UIA names JobsList='$($jobs.Current.Name)' CategoryList='$($categories.Current.Name)' Status='$($status.Current.Name)' Connection='$($connection.Current.Name)'"
Check (-not [string]::IsNullOrWhiteSpace($jobs.Current.Name)) 'JobsList has an accessible name'
Check (-not [string]::IsNullOrWhiteSpace($categories.Current.Name)) 'CategoryList has an accessible name'

# 2. Tab order via the WinUI input site.
$add = ById 'AddButton'
$add.SetFocus()
Start-Sleep -Milliseconds 300
$input = [U7Native]::InputSite($hwnd)
Write-Output "UIA inputSite=$input addFocus=$($add.Current.HasKeyboardFocus)"
$order = @()
for ($i = 0; $i -lt 8; $i++) {
    [U7Native]::PostMessage($input, 0x0100, [IntPtr]9, [IntPtr]1) | Out-Null
    [U7Native]::PostMessage($input, 0x0101, [IntPtr]9, [IntPtr]0xC0000001) | Out-Null
    Start-Sleep -Milliseconds 250
    $focusCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::HasKeyboardFocusProperty, $true)
    $focused = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $focusCondition)
    if ($focused.Count -gt 0) {
        foreach ($entry in $focused) {
            $label = $entry.Current.Name
            if (-not [string]::IsNullOrWhiteSpace($label)) {
                $order += $label
            }
        }
    }
}
$order = @($order | Select-Object -Unique)
Write-Output "TAB ORDER: $($order -join ' -> ')"
Check ($order.Count -ge 3) 'Tab moves focus through multiple named controls'

# 2b. Add URL dialog names.
$add.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Milliseconds 800
$unnamedDialog = @()
foreach ($type in @([System.Windows.Automation.ControlType]::Edit,
                   [System.Windows.Automation.ControlType]::ComboBox,
                   [System.Windows.Automation.ControlType]::Button)) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type)
    foreach ($element in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        if ($element.Current.IsOffscreen) { continue }
        if ([string]::IsNullOrWhiteSpace($element.Current.Name)) {
            $unnamedDialog += "$($type.ProgrammaticName)/$($element.Current.AutomationId)"
        }
    }
}
Write-Output "UIA unnamed Add dialog controls: $($unnamedDialog -join ', ')"
Check ($unnamedDialog.Count -eq 0) 'Add dialog controls have accessible names'
$cancelCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, 'Cancel')
$cancel = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cancelCondition)
if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500 }

# 3. Contrast of the explicit dark palette.
function Luminance([string]$hex) {
    $r = [Convert]::ToInt32($hex.Substring(1, 2), 16) / 255
    $g = [Convert]::ToInt32($hex.Substring(3, 2), 16) / 255
    $b = [Convert]::ToInt32($hex.Substring(5, 2), 16) / 255
    $convert = {
        param($c)
        if ($c -le 0.03928) { return $c / 12.92 }
        return [Math]::Pow(($c + 0.055) / 1.055, 2.4)
    }
    return 0.2126 * (& $convert $r) + 0.7152 * (& $convert $g) + 0.0722 * (& $convert $b)
}
function Contrast([string]$fg, [string]$bg) {
    $l1 = Luminance $fg
    $l2 = Luminance $bg
    if ($l1 -lt $l2) { $tmp = $l1; $l1 = $l2; $l2 = $tmp }
    return ($l1 + 0.05) / ($l2 + 0.05)
}
$pairs = @(
    @('#D7D7D7', '#1E1E1E', 'primary text'),
    @('#E6E6E6', '#232323', 'toolbar label'),
    @('#A0A0A0', '#1E1E1E', 'secondary text'),
    @('#B8B8B8', '#1E1E1E', 'rate/ETA text'),
    @('#8C8C8C', '#1E1E1E', 'empty text'),
    @('#8C8C8C', '#252526', 'muted sidebar'),
    @('#6FA8DC', '#1E1E1E', 'blue icon'),
    @('#8CCF8C', '#1E1E1E', 'green icon'),
    @('#E8B36A', '#1E1E1E', 'orange icon'),
    @('#D98C8C', '#1E1E1E', 'red icon')
)
$low = @()
foreach ($pair in $pairs) {
    $ratio = Contrast $pair[0] $pair[1]
    Write-Output ("CONTRAST {0} on {1}: {2:N2}:1 ({3})" -f $pair[0], $pair[1], $ratio, $pair[2])
    if ($ratio -lt 4.5) { $low += $pair[2] }
}
Check ($low.Count -eq 0) 'all normal-text palette pairs meet 4.5:1'

# 4. DPI and simulated effective-area matrix.
$systemDpi = [U7Native]::GetDpiForSystem()
$windowDpi = [U7Native]::GetDpiForWindow($hwnd)
$monitor = [U7Native]::MonitorFromWindow($hwnd, 2)
$scale = 0
[U7Native]::GetScaleFactorForMonitor($monitor, [ref]$scale) | Out-Null
Write-Output "DPI system=$systemDpi window=$windowDpi monitorScale=$scale%"
$working = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$original = New-Object U7Native+RECT
[U7Native]::GetWindowRect($hwnd, [ref]$original) | Out-Null
$matrix = @(100, 125, 150, 200)
foreach ($percent in $matrix) {
    $factor = $percent / 100.0
    $width = [Math]::Max(900, [int]($working.Width / $factor))
    $height = [Math]::Max(560, [int]($working.Height / $factor))
    [U7Native]::MoveWindow($hwnd, 0, 0, [Math]::Max(900, $width - 16), [Math]::Max(560, $height - 16), $true) | Out-Null
    Start-Sleep -Milliseconds 700
    $rect = New-Object U7Native+RECT
    [U7Native]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $addRect = $add.Current.BoundingRectangle
    $description = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Description')))
    $descriptionRect = if ($description) { $description.Current.BoundingRectangle } else { [System.Windows.Rect]::Empty }
    $inside = $addRect.Right -le $rect.Right -and $addRect.Bottom -le $rect.Bottom -and
              $descriptionRect.Right -le $rect.Right
    Write-Output ("DPI-SIM {0}% effective={1}x{2} window={3},{4},{5},{6} addRight={7} descriptionRight={8} fits={9}" -f
        $percent, $width, $height, $rect.Left, $rect.Top, $rect.Right, $rect.Bottom,
        [int]$addRect.Right, [int]$descriptionRect.Right, $inside)
    Check $inside "simulated $percent% layout keeps toolbar and columns inside the window"
}
[U7Native]::MoveWindow($hwnd, $original.Left, $original.Top,
    $original.Right - $original.Left, $original.Bottom - $original.Top, $true) | Out-Null
Write-Output "DPI-ACTUAL only 100% was exercised; 125/150/200 are simulated effective areas because changing Windows scaling requires sign-out."

# 5. Localization audit.
$resources = @(Get-ChildItem (Join-Path $PSScriptRoot '..\src') -Recurse -Include *.resw, *.resx -ErrorAction SilentlyContinue)
$resourceCount = $resources.Count
Write-Output "LOCALIZATION resourceFiles=$resourceCount xamlStrings=hardcoded-English"
Check ($resourceCount -eq 0) 'localization audit recorded (no resource files; English-only)'

Write-Output $(if ($failures -eq 0) { 'U7 CHECKS PASSED' } else { "U7 $failures CHECK(S) FAILED" })
exit $(if ($failures -eq 0) { 0 } else { 1 })
