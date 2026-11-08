# Small UI Automation driver for walkthroughs of a test UI: each -Do step is "verb:argument", run in order.
# Attaches only to a UI whose executable path contains -PathPart and refuses the installed app. Starts nothing.
#   invoke:<AutomationId or Name>   Invoke/toggle/select the first match in any of the UI's windows
#   expand:<Name>                   expand a menu item or expander;  collapse:<Name>
#   select:<Name>                   select a list item (sidebar view, list row, Options page)
#   click:<Name>                    left-click the middle of an element (or 120 px in); only with the test UI in front
#   right:<Name>                    right-click the middle of an element (context menus); only with the test UI in front
#   key:<ESC|ENTER|TAB|DELETE|SHIFTDELETE|DOWN|UP>  real key input to the foreground test window
#   front                           bring the main window to the front (and nudge its size so it paints)
#   nudge                           resize every normal window of the UI by one pixel and back, so each paints
#   set:<AutomationId or Name>=<text>  set the value of an edit box (or the edit inside a combo box)
#   closewin:<title start>          close a secondary window (never the main one) with WM_CLOSE; the title start is required
#   focus:<AutomationId or Name>     move keyboard focus to an element through UI Automation
#   wait:<ms>                       pause
#   capture:<name>                  PrintWindow every visible window of the UI into -OutDir (see ui-capture.ps1)
#   tree:<depth>                    print the names of the controls of every window of the UI
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PathPart,
    [string]$OutDir = '.',
    [Parameter(Mandatory)][string[]]$Do
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiDriveNative {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hh, bool repaint);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  public static IntPtr WindowFromPoint(int x, int y) { POINT p; p.X = x; p.Y = y; return WindowFromPoint(p); }
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  public static IntPtr Root(IntPtr h) { return h == IntPtr.Zero ? h : GetAncestor(h, 2); }
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<IntPtr> Windows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static void Front(IntPtr h) {
    keybd_event(0x12, 0, 0, UIntPtr.Zero); SetForegroundWindow(h); keybd_event(0x12, 0, 2, UIntPtr.Zero);
  }
  public static void Key(byte vk, bool shift, bool extended) {
    if (shift) keybd_event(0x10, 0x2A, 0, UIntPtr.Zero);
    keybd_event(vk, 0, extended ? 1u : 0u, UIntPtr.Zero);
    System.Threading.Thread.Sleep(50);
    keybd_event(vk, 0, extended ? 3u : 2u, UIntPtr.Zero);
    if (shift) keybd_event(0x10, 0x2A, 2, UIntPtr.Zero);
  }
}
'@

# The main window's process, and any progress-window processes (--job) it started.
$procs = @(Get-CimInstance Win32_Process -Filter "Name='DownloadManager.Desktop.WinUI.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "*$PathPart*" })
if ($procs.Count -eq 0) { Write-Output 'NO TEST UI'; exit 1 }
if ($procs | Where-Object { $_.ExecutablePath -like '*\Programs\Inbound Download Manager\*' }) { Write-Output 'REFUSED: installed app'; exit 1 }
$main = $procs | Where-Object { $_.CommandLine -notmatch '--job' } | Select-Object -First 1
if (-not $main) { $main = $procs[0] }
$procId = [int]$main.ProcessId
$allIds = @($procs | ForEach-Object { [int]$_.ProcessId })
$AE = [System.Windows.Automation.AutomationElement]
$Scope = [System.Windows.Automation.TreeScope]

function TopWindows {
    $list = @()
    foreach ($id in $allIds) {
        $c = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $id)
        $list += @($AE::RootElement.FindAll($Scope::Children, $c))
    }
    return $list
}
function AllHandles { foreach ($id in $allIds) { [UiDriveNative]::Windows([uint32]$id) } }
function MainHwnd {
    # The main window by its title: Process.MainWindowHandle can be any of the process's windows (Statistics, Options).
    foreach ($h in [UiDriveNative]::Windows([uint32]$procId)) {
        if ([UiDriveNative]::Cls($h) -eq 'WinUIDesktopWin32WindowClass' -and [UiDriveNative]::Title($h) -eq 'Inbound Download Manager') { return $h }
    }
    return (Get-Process -Id $procId).MainWindowHandle
}
function Test-TitleBarButton($e) {
    # A window's own Minimize, Maximize and Close: by their automation ids, or any button inside a title bar.
    if ($e.Current.AutomationId -in @('Minimize', 'Maximize', 'Close', 'Restore')) { return $true }
    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
    return $parent -and $parent.Current.ControlType -eq [System.Windows.Automation.ControlType]::TitleBar
}
function Find-Element([string]$key) {
    # "~text" matches a name that starts with text (names with characters that don't survive the command line).
    # The title bar's buttons never match, by id, name or "~" prefix: a Close there would hide or end the window.
    $prefix = $key.StartsWith('~')
    $text = if ($prefix) { $key.Substring(1) } else { $key }
    if (-not $text) { return $null }
    $fallback = $null
    foreach ($w in TopWindows) {
        foreach ($e in $w.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
            $c = $e.Current
            $byId = -not $prefix -and $c.AutomationId -eq $text
            $byName = if ($prefix) { $c.Name -and $c.Name.StartsWith($text) } else { $c.Name -eq $text }
            if (-not ($byId -or $byName)) { continue }
            if (Test-TitleBarButton $e) { continue }
            if (-not $c.IsOffscreen) { return $e }
            if (-not $fallback) { $fallback = $e }
        }
    }
    return $fallback
}
function Test-TestUiInFront {
    $fg = [UiDriveNative]::GetForegroundWindow()
    foreach ($h in AllHandles) { if ($h -eq $fg) { return $true } }
    return $false
}
function Pattern($el, $pattern) {
    $p = $null
    if ($el.TryGetCurrentPattern($pattern, [ref]$p)) { return $p }
    return $null
}

# From Bash a list arrives as one string: steps are separated by '|'.
$Do = @($Do | ForEach-Object { $_ -split '\|' } | Where-Object { $_ })
foreach ($step in $Do) {
    $verb, $arg = $step -split ':', 2
    switch ($verb) {
        'set' {
            $target, $value = $arg -split '=', 2
            $el = Find-Element $target
            $p = if ($el) { Pattern $el ([System.Windows.Automation.ValuePattern]::Pattern) } else { $null }
            if (-not $p -and $el) {
                $edit = $el.FindFirst($Scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
                if ($edit) { $p = Pattern $edit ([System.Windows.Automation.ValuePattern]::Pattern) }
            }
            if ($p) { $p.SetValue($value); Write-Output "SET $target" } else { Write-Output "SET $target MISSING" }
        }
        'closewin' {
            # WM_CLOSE to a secondary window whose title starts with the argument; never the main window.
            # An empty argument would match every title, so it is refused.
            if (-not $arg) { Write-Output 'CLOSEWIN skipped: no title given'; continue }
            $mainHwnd = MainHwnd
            foreach ($h in AllHandles) {
                if ($h -eq $mainHwnd) { continue }
                if ([UiDriveNative]::Title($h).StartsWith($arg)) { [UiDriveNative]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Write-Output "CLOSEWIN $arg" }
            }
        }
        'focus' {
            $el = Find-Element $arg
            if ($el) { $el.SetFocus(); Write-Output "FOCUS $arg" } else { Write-Output "FOCUS $arg MISSING" }
        }
        'wait' { Start-Sleep -Milliseconds ([int]$arg) }
        'front' {
            $h = MainHwnd
            [UiDriveNative]::Front($h)
            Start-Sleep -Milliseconds 300
            # The Alt tap can turn on the menu bar's access-key tips (small popups); Escape turns them off.
            $tips = @(AllHandles | Where-Object {
                $r = New-Object UiDriveNative+RECT; [UiDriveNative]::GetWindowRect($_, [ref]$r) | Out-Null
                [UiDriveNative]::Cls($_) -eq 'Microsoft.UI.Content.PopupWindowSiteBridge' -and ($r.Right - $r.Left) -lt 60 })
            if ($tips.Count -gt 0) { [UiDriveNative]::Key(0x1B, $false, $false); Start-Sleep -Milliseconds 200 }
            Write-Output "FRONT ok=$([UiDriveNative]::GetForegroundWindow() -eq $h)"
        }
        'nudge' {
            foreach ($h in AllHandles) {
                if ([UiDriveNative]::Cls($h) -ne 'WinUIDesktopWin32WindowClass') { continue }
                $r = New-Object UiDriveNative+RECT
                [UiDriveNative]::GetWindowRect($h, [ref]$r) | Out-Null
                $w = $r.Right - $r.Left; $hh = $r.Bottom - $r.Top
                [UiDriveNative]::MoveWindow($h, $r.Left, $r.Top, $w + 1, $hh + 1, $true) | Out-Null
                Start-Sleep -Milliseconds 400
                [UiDriveNative]::MoveWindow($h, $r.Left, $r.Top, $w, $hh, $true) | Out-Null
            }
            Start-Sleep -Milliseconds 600
        }
        'invoke' {
            $el = Find-Element $arg
            if (-not $el) { Write-Output "INVOKE $arg MISSING"; continue }
            $p = Pattern $el ([System.Windows.Automation.InvokePattern]::Pattern)
            if ($p) { $p.Invoke(); Write-Output "INVOKE $arg"; continue }
            $p = Pattern $el ([System.Windows.Automation.TogglePattern]::Pattern)
            if ($p) { $p.Toggle(); Write-Output "TOGGLE $arg"; continue }
            $p = Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)
            if ($p) { $p.Select(); Write-Output "SELECT $arg"; continue }
            $p = Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
            if ($p) { $p.Expand(); Write-Output "EXPAND $arg"; continue }
            Write-Output "INVOKE $arg NO PATTERN"
        }
        'select' {
            $el = Find-Element $arg
            # A list item's name can sit on a text inside it: walk up to the item.
            $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
            while ($el -and -not (Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern))) { $el = $walker.GetParent($el) }
            if (-not $el) { Write-Output "SELECT $arg MISSING"; continue }
            (Pattern $el ([System.Windows.Automation.SelectionItemPattern]::Pattern)).Select()
            Write-Output "SELECT $arg"
        }
        'expand' {
            $el = Find-Element $arg
            $p = if ($el) { Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } else { $null }
            if ($p) { $p.Expand(); Write-Output "EXPAND $arg" } else { Write-Output "EXPAND $arg MISSING" }
        }
        'collapse' {
            $el = Find-Element $arg
            $p = if ($el) { Pattern $el ([System.Windows.Automation.ExpandCollapsePattern]::Pattern) } else { $null }
            if ($p) { $p.Collapse(); Write-Output "COLLAPSE $arg" } else { Write-Output "COLLAPSE $arg MISSING" }
        }
        { $_ -in 'right', 'click' } {
            # Real mouse input lands on whatever window is under the cursor, so only with the test UI in front, and
            # only on an element that is on screen and has a size (Find-Element can fall back to an offscreen one).
            if (-not (Test-TestUiInFront)) { Write-Output "$($verb.ToUpper()) $arg skipped: the test UI is not in front"; continue }
            $el = Find-Element $arg
            if (-not $el) { Write-Output "$($verb.ToUpper()) $arg MISSING"; continue }
            $r = $el.Current.BoundingRectangle
            if ($el.Current.IsOffscreen -or $r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) {
                Write-Output "$($verb.ToUpper()) $arg skipped: offscreen or no size"; continue
            }
            $x = [int]($r.Left + [Math]::Min(120, $r.Width / 2)); $y = [int]($r.Top + $r.Height / 2)
            # The point itself must belong to the test UI: another window can overlap the element.
            $atPoint = [UiDriveNative]::Root([UiDriveNative]::WindowFromPoint($x, $y))
            if (-not (@(AllHandles) -contains $atPoint)) { Write-Output "$($verb.ToUpper()) $arg skipped: another window is at that point"; continue }
            [UiDriveNative]::SetCursorPos($x, $y) | Out-Null
            Start-Sleep -Milliseconds 100
            $down, $up = if ($verb -eq 'right') { 0x0008, 0x0010 } else { 0x0002, 0x0004 }
            [UiDriveNative]::mouse_event($down, 0, 0, 0, [UIntPtr]::Zero)
            [UiDriveNative]::mouse_event($up, 0, 0, 0, [UIntPtr]::Zero)
            Write-Output "$($verb.ToUpper()) $arg"
        }
        'key' {
            if (-not (Test-TestUiInFront)) { Write-Output "KEY $arg skipped: the test UI is not in front"; continue }
            switch ($arg) {
                'ESC' { [UiDriveNative]::Key(0x1B, $false, $false) }
                'ENTER' { [UiDriveNative]::Key(0x0D, $false, $false) }
                'TAB' { [UiDriveNative]::Key(0x09, $false, $false) }
                'DOWN' { [UiDriveNative]::Key(0x28, $false, $true) }
                'UP' { [UiDriveNative]::Key(0x26, $false, $true) }
                'RIGHT' { [UiDriveNative]::Key(0x27, $false, $true) }
                'DELETE' { [UiDriveNative]::Key(0x2E, $false, $true) }
                'SHIFTDELETE' { [UiDriveNative]::Key(0x2E, $true, $true) }
            }
            Write-Output "KEY $arg"
        }
        'capture' {
            & (Join-Path $PSScriptRoot 'ui-capture.ps1') -PathPart $PathPart -OutDir $OutDir -Name $arg
        }
        'tree' {
            $depth = if ($arg) { [int]$arg } else { 0 }
            foreach ($w in TopWindows) {
                Write-Output "WINDOW '$($w.Current.Name)' class=$($w.Current.ClassName)"
                foreach ($e in $w.FindAll($Scope::Descendants, [System.Windows.Automation.Condition]::TrueCondition)) {
                    if ($e.Current.IsOffscreen) { continue }
                    $n = $e.Current.Name; $id = $e.Current.AutomationId
                    if (-not $n -and -not $id) { continue }
                    Write-Output ("  {0} id={1} name={2}" -f $e.Current.ControlType.ProgrammaticName.Replace('ControlType.', ''), $id, $n)
                }
            }
        }
        default { Write-Output "UNKNOWN STEP $step" }
    }
}
