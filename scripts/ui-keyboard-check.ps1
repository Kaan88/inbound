[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class UiKeyboardCheckNative {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc callback, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
  public static IntPtr InputSite(IntPtr parent) {
    IntPtr found = IntPtr.Zero;
    EnumChildWindows(parent, (h, l) => {
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      if (cls.ToString() == "InputSiteWindowClass") { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  public static void Enter(IntPtr hwnd) {
    PostMessage(hwnd, 0x0100, (IntPtr)13, (IntPtr)1);
    System.Threading.Thread.Sleep(80);
    PostMessage(hwnd, 0x0101, (IntPtr)13, (IntPtr)0xC0000001);
  }
}
'@

$proc = Get-Process DownloadManager.Desktop.WinUI -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCondition)
if (-not $win) { Write-Output 'NO WINDOW'; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle

function Find-ById([string]$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-ByName([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Get-Button([string]$name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
    foreach ($b in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)) {
        if ($b.Current.Name -eq $name) { return $b }
    }
    return $null
}
function Invoke-Element($el) {
    if (-not $el) { return $false }
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke(); return $true }
    return $false
}
function Modal-Info([string]$title) {
    $el = Find-ByName $title
    if (-not $el) { return 'missing' }
    $p = $null
    if ($el.TryGetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern, [ref]$p)) {
        return "modal=$($p.Current.IsModal)"
    }
    return 'no-window-pattern'
}

# Accelerator metadata.
foreach ($menu in @('Tasks', 'Downloads', 'View', 'File')) {
    $item = Find-ByName $menu
    if (-not $item) { continue }
    $expand = $null
    if (-not $item.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$expand)) { continue }
    $expand.Expand()
    Start-Sleep -Milliseconds 450
    if ($menu -eq 'View') {
        $arrange = Find-ByName 'Arrange files'
        $sub = $null
        if ($arrange -and $arrange.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$sub)) {
            $sub.Expand()
            Start-Sleep -Milliseconds 350
        }
    }
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
    foreach ($entry in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)) {
        $name = $entry.Current.Name
        if ($name -and $name -notin @('Tasks','File','Downloads','View','Help') -and $entry.Current.AcceleratorKey) {
            Write-Output "ACCEL '$name'='$($entry.Current.AcceleratorKey)'"
        }
    }
    $expand.Collapse()
    Start-Sleep -Milliseconds 200
}

# UIA focus route.
$add = Find-ById 'AddButton'
if ($add) {
    $add.SetFocus()
    Start-Sleep -Milliseconds 300
    Write-Output "FOCUS AddButton hasFocus=$($add.Current.HasKeyboardFocus) focusable=$($add.Current.IsKeyboardFocusable)"
}

$jobs = Find-ById 'JobsList'
$itemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
$rows = if ($jobs) { $jobs.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCondition) } else { @() }
if ($rows.Count -gt 0) {
    $rows[0].GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 400
}

# Delete dialog uses DefaultButton=Close; Enter must cancel, not delete.
$delete = Find-ById 'DeleteButton'
if ($delete -and (Invoke-Element $delete)) {
    Start-Sleep -Milliseconds 700
    $input = [UiKeyboardCheckNative]::InputSite($hwnd)
    Write-Output "DELETE open=$([bool](Find-ByName 'Delete download?')) $(Modal-Info 'Delete download?') inputSite=$input"
    [UiKeyboardCheckNative]::Enter($input)
    Start-Sleep -Milliseconds 900
    $rowsAfter = $jobs.FindAll([System.Windows.Automation.TreeScope]::Children, $itemCondition).Count
    Write-Output "DELETE-ENTER closed=$(-not [bool](Find-ByName 'Delete download?')) rowsAfter=$rowsAfter"
    if (Find-ByName 'Delete download?') { Invoke-Element (Get-Button 'Cancel') | Out-Null; Start-Sleep -Milliseconds 500 }
}

# Move/Rename uses DefaultButton=Primary; the current path is a safe no-op.
$file = Find-ByName 'File'
if ($file) {
    $file.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 450
    $move = Find-ByName 'Move/Rename...'
    if (Invoke-Element $move) {
        Start-Sleep -Milliseconds 700
        $input = [UiKeyboardCheckNative]::InputSite($hwnd)
        Write-Output "MOVE open=$([bool](Find-ByName 'Move/Rename')) $(Modal-Info 'Move/Rename') inputSite=$input"
        [UiKeyboardCheckNative]::Enter($input)
        Start-Sleep -Milliseconds 900
        Write-Output "MOVE-ENTER closed=$(-not [bool](Find-ByName 'Move/Rename'))"
        if (Find-ByName 'Move/Rename') { Invoke-Element (Get-Button 'Cancel') | Out-Null; Start-Sleep -Milliseconds 500 }
    }
}

# Add dialog is modal.
if ($add -and (Invoke-Element $add)) {
    Start-Sleep -Milliseconds 700
    Write-Output "ADD open=$([bool](Find-ByName 'Add download')) $(Modal-Info 'Add download')"
    Invoke-Element (Get-Button 'Cancel') | Out-Null
}
Write-Output 'UI-KEYBOARD-CHECK-DONE'
