# Lists the menus, toolbar, categories, columns and row context menu of a running test UI and saves a screenshot.
# Attaches only to a UI whose executable path contains -PathPart and refuses the installed app. Starts nothing, but it
# opens menus and right-clicks a row, so use a test instance only.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PathPart,
    [string]$Screenshot = (Join-Path $env:TEMP 'downloadmanager-ui-inventory.png')
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class UiWin2 {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@

$proc = Get-CimInstance Win32_Process -Filter "Name='DownloadManager.Desktop.WinUI.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "*$PathPart*" -and $_.CommandLine -notmatch '--job|update-progress' } |
    Select-Object -First 1
if (-not $proc) { Write-Output 'NO TEST UI'; exit 1 }
if ($proc.ExecutablePath -like '*\Programs\Inbound Download Manager\*') { Write-Output 'REFUSED: that is the installed app, not a test instance'; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::RootElement
$pidCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.ProcessId)
$win = $null
foreach ($candidate in $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCondition)) {
    if ($candidate.Current.Name -eq 'Inbound Download Manager') { $win = $candidate; break }
}
if (-not $win) { Write-Output 'NO WINDOW'; exit 1 }
$hwnd = [IntPtr]$win.Current.NativeWindowHandle
[UiWin2]::ShowWindow($hwnd, 3) | Out-Null
[UiWin2]::SetForegroundWindow($hwnd) | Out-Null
Start-Sleep -Milliseconds 600

$topNames = @('Tasks', 'File', 'Downloads', 'View', 'Help')
$menuItemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)
$menuCondition = New-Object System.Windows.Automation.AndCondition($pidCondition, $menuItemCondition)

Write-Output "WINDOW title='$($win.Current.Name)' rect=$($win.Current.BoundingRectangle)"
$topItems = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuItemCondition)

foreach ($name in $topNames) {
    $item = $null
    foreach ($candidate in $topItems) {
        if ($candidate.Current.Name -eq $name) { $item = $candidate; break }
    }
    if (-not $item) { Write-Output "MENU $name = MISSING"; continue }
    $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    Start-Sleep -Milliseconds 500
    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
    $names = @()
    foreach ($entry in $all) {
        $current = $entry.Current.Name
        if ($current -and $current -notin $topNames -and $current -notin $names) { $names += $current }
    }
    Write-Output "MENU ${name}: $($names -join ' | ')"
    $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse()
    Start-Sleep -Milliseconds 250
}

$buttonCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $buttonCondition)
$buttonNames = @()
foreach ($button in $buttons) { if ($button.Current.Name) { $buttonNames += $button.Current.Name } }
Write-Output "TOOLBAR: $($buttonNames -join ' | ')"

$categoryCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'CategoryList')
$categoryList = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $categoryCondition)
if ($categoryList) {
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $categories = $categoryList.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    $categoryNames = @()
    foreach ($category in $categories) {
        $textCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
        $texts = $category.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        foreach ($text in $texts) { if ($text.Current.Name) { $categoryNames += $text.Current.Name } }
    }
    Write-Output "CATEGORIES: $($categoryNames -join ' | ')"
}

foreach ($header in @('File Name','Q','Size','Status','Time left','Transfer rate','Last Try Date','Description')) {
    $headerCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $header)
    $found = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $headerCondition)
    Write-Output "COLUMN '$header' present=$([bool]$found)"
}

$jobsCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, 'JobsList')
$jobs = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $jobsCondition)
if ($jobs) {
    $rowCondition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)
    $rows = $jobs.FindAll([System.Windows.Automation.TreeScope]::Children, $rowCondition)
    if ($rows.Count -gt 0) {
        $rect = $rows[0].Current.BoundingRectangle
        [UiWin2]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 300
        [UiWin2]::SetCursorPos([int]($rect.X + 200), [int]($rect.Y + $rect.Height / 2)) | Out-Null
        [UiWin2]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)
        [UiWin2]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 900
        $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $menuCondition)
        $contextNames = @()
        foreach ($entry in $all) {
            $current = $entry.Current.Name
            if ($current -and $current -notin $topNames -and $current -notin $contextNames) { $contextNames += $current }
        }
        Write-Output "CONTEXT: $($contextNames -join ' | ')"
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 400
    }
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
$graphics = [System.Drawing.Graphics]::FromImage($bmp)
$graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$path = $Screenshot
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bmp.Dispose()
Write-Output "SCREENSHOT=$path"
