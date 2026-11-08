# Accessibility checks of a running test UI: control names, tab order, Add URL names, the contrast of every Inbound
# colour token in both the Dark and the Light theme (read from App.xaml), a simulated DPI matrix and a localization note.
# Attaches only to a UI whose executable path contains -PathPart and refuses the installed app; it moves and resizes that
# window for the DPI matrix, so run it against a test instance only. Starts nothing.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$PathPart)

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

$proc = Get-CimInstance Win32_Process -Filter "Name='DownloadManager.Desktop.WinUI.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "*$PathPart*" -and $_.CommandLine -notmatch '--job|update-progress' } |
    Select-Object -First 1
if (-not $proc) { Write-Output 'NO TEST UI'; exit 1 }
if ($proc.ExecutablePath -like '*\Programs\Inbound Download Manager\*') { Write-Output 'REFUSED: that is the installed app, not a test instance'; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.ProcessId)
# The main window by its title: Options or Statistics can be the process's first window.
$win = $null
foreach ($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pc)) {
    if ($candidate.Current.Name -eq 'Inbound Download Manager') { $win = $candidate; break }
}
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

# 3. Contrast of every Inbound colour token, Dark and Light, read from App.xaml so the check follows the palette.
# Text tokens (secondary, error, status) need 4.5:1 on the window surface and on the chrome; glyph and progress tokens
# are non-text UI and need 3:1 (WCAG 1.4.11). Pairs on the highlight banner are printed for information only.
# WinUI's own text beside the tokens (primary text, text on the accent button) is checked with WinUI's theme values.
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
$xamlNs = 'http://schemas.microsoft.com/winfx/2006/xaml'
[xml]$appXaml = Get-Content -Raw (Join-Path $PSScriptRoot '..\src\DownloadManager.Desktop.WinUI\App.xaml')
$ns = New-Object System.Xml.XmlNamespaceManager($appXaml.NameTable)
$ns.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$ns.AddNamespace('x', $xamlNs)
$accent = @{}
foreach ($c in $appXaml.SelectNodes('//p:Color', $ns)) { $accent[$c.GetAttribute('Key', $xamlNs)] = $c.InnerText.Trim() }
$low = @()
$checked = 0
foreach ($theme in @('Dark', 'Light')) {
    $dict = $appXaml.SelectSingleNode("//p:ResourceDictionary.ThemeDictionaries/p:ResourceDictionary[@x:Key='$theme']", $ns)
    if (-not $dict) { $low += "$theme dictionary missing"; continue }
    $tokens = @{}
    foreach ($brush in $dict.SelectNodes('p:SolidColorBrush', $ns)) { $tokens[$brush.GetAttribute('Key', $xamlNs)] = $brush.GetAttribute('Color') }
    foreach ($key in ($tokens.Keys | Sort-Object)) {
        $isText = $key -match 'Text|Status'
        $isGlyph = $key -match 'Icon|ProgressFill'
        if (-not ($isText -or $isGlyph)) { continue }
        $need = if ($isText) { 4.5 } else { 3.0 }
        foreach ($bg in @('InboundSurfaceBrush', 'InboundChromeBrush', 'InboundHighlightBrush')) {
            $ratio = Contrast $tokens[$key] $tokens[$bg]
            $info = $bg -eq 'InboundHighlightBrush'
            $verdict = if ($info) { 'info' } elseif ($ratio -ge $need) { 'ok' } else { 'LOW' }
            Write-Output ("CONTRAST {0,-5} {1,-27} {2} on {3,-21} {4}: {5:N2}:1 (needs {6}) {7}" -f $theme, $key, $tokens[$key], $bg, $tokens[$bg], $ratio, $need, $verdict)
            if (-not $info) {
                $checked++
                if ($ratio -lt $need) { $low += "$theme $key on $bg" }
            }
        }
    }
    # TextFillColorPrimary is #FFFFFF in Dark and #E4000000 in Light (about #1B1B1B on these surfaces); the accent button
    # fills with SystemAccentColorLight2 (Dark) or SystemAccentColorDark1 (Light) and writes black or white text on it.
    $primary = if ($theme -eq 'Dark') { '#FFFFFF' } else { '#1B1B1B' }
    $accentFill = if ($theme -eq 'Dark') { $accent['SystemAccentColorLight2'] } else { $accent['SystemAccentColorDark1'] }
    $onAccent = if ($theme -eq 'Dark') { '#000000' } else { '#FFFFFF' }
    foreach ($pair in @(@($primary, $tokens['InboundSurfaceBrush'], 'primary text on surface'),
                        @($primary, $tokens['InboundChromeBrush'], 'primary text on chrome'),
                        @($onAccent, $accentFill, 'text on the accent button'))) {
        $ratio = Contrast $pair[0] $pair[1]
        $verdict = if ($ratio -ge 4.5) { 'ok' } else { 'LOW' }
        Write-Output ("CONTRAST {0,-5} {1} on {2}: {3:N2}:1 (needs 4.5, {4}) {5}" -f $theme, $pair[0], $pair[1], $ratio, $pair[2], $verdict)
        $checked++
        if ($ratio -lt 4.5) { $low += "$theme $($pair[2])" }
    }
}
Write-Output "CONTRAST pairs checked: $checked; low: $($low -join ', ')"
Check ($low.Count -eq 0 -and $checked -gt 40) 'every Dark and Light colour token meets its contrast minimum'

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
