# UI.R6: read-only UIA check that the main list's live counters (Size, Time left, Transfer rate) and their headers share
# a right edge. Attaches to a running UI whose path contains -PathPart; starts nothing. Use a test instance only.
param([string]$PathPart = 'worktrees\stream-a2')
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$proc = Get-CimInstance Win32_Process -Filter "Name='DownloadManager.Desktop.WinUI.exe'" | Where-Object { $_.ExecutablePath -like "*$PathPart*" } | Select-Object -First 1
if (-not $proc) { 'NO TEST UI'; exit 1 }
$AE = [System.Windows.Automation.AutomationElement]
$root = $AE::RootElement
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$proc.ProcessId)))
if (-not $win) { 'NO WINDOW'; exit 1 }
function ById($id) { $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $id))) }
foreach ($id in 'SizeCell', 'EtaCell', 'RateCell') {
    $cells = ById $id
    foreach ($c in $cells) {
        $r = $c.Current.BoundingRectangle
        "{0,-9} '{1}' left={2} right={3}" -f $id, $c.Current.Name, [math]::Round($r.Left), [math]::Round($r.Right)
    }
}
# Headers: the header buttons are named after their columns.
$buttons = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)))
foreach ($b in $buttons) {
    if ($b.Current.Name -in 'Size', 'Time left', 'Transfer rate') {
        $r = $b.Current.BoundingRectangle
        $text = $b.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
        $tr = if ($text) { $text.Current.BoundingRectangle } else { $null }
        "HEADER {0,-14} cell=[{1},{2}] text=[{3},{4}]" -f $b.Current.Name, [math]::Round($r.Left), [math]::Round($r.Right), $(if ($tr) { [math]::Round($tr.Left) } else { '-' }), $(if ($tr) { [math]::Round($tr.Right) } else { '-' })
    }
}
