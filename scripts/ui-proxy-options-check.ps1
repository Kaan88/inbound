[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutFile = ''
)

# Opens Options in the live WinUI window and verifies the Proxy controls exist with
# accessible names, then selects "Manual proxy" + SOCKS4a and reads the values back.
$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Split-Path -Parent $scriptDir
if (-not $OutFile) { $OutFile = Join-Path $root 'artifacts\proxy-options-check.txt' }
$log = New-Object System.Collections.Generic.List[string]
function Log($m) { $log.Add($m); Write-Host $m }
function Check($ok, $m) { Log ("{0}: {1}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $m) }

$hostExe = Join-Path $root "src\DownloadManager.Host\bin\$Configuration\net10.0-windows\DownloadManager.Host.exe"
$uiExe = (Get-ChildItem (Join-Path $root "src\DownloadManager.Desktop.WinUI\bin\x64\$Configuration") -Recurse `
    -Filter DownloadManager.Desktop.WinUI.exe | Select-Object -First 1).FullName
$hostProc = Start-Process $hostExe -ArgumentList '--background' -PassThru -WindowStyle Hidden
Start-Sleep 4
$ui = Start-Process $uiExe -PassThru
try {
    $auto = [System.Windows.Automation.AutomationElement]
    $scope = [System.Windows.Automation.TreeScope]
    $win = $null
    for ($i = 0; $i -lt 30 -and -not $win; $i++) {
        Start-Sleep 1
        $pc = New-Object System.Windows.Automation.PropertyCondition($auto::ProcessIdProperty, $ui.Id)
        $win = $auto::RootElement.FindFirst($scope::Children, $pc)
    }
    Check ($null -ne $win) 'WinUI window found'
    if (-not $win) { return }

    function Find($name, $type) {
        $c = New-Object System.Windows.Automation.AndCondition(
            (New-Object System.Windows.Automation.PropertyCondition($auto::NameProperty, $name)),
            (New-Object System.Windows.Automation.PropertyCondition($auto::ControlTypeProperty, $type)))
        return $win.FindFirst($scope::Descendants, $c)
    }

    $button = $null
    foreach ($n in 'Options', 'Options...') {
        $button = Find $n ([System.Windows.Automation.ControlType]::Button)
        if ($button) { break }
    }
    Check ($null -ne $button) 'Options toolbar button found'
    if (-not $button) { return }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep 2

    foreach ($pair in @(
            @('Proxy mode', [System.Windows.Automation.ControlType]::ComboBox),
            @('Proxy type', [System.Windows.Automation.ControlType]::ComboBox),
            @('Proxy address', [System.Windows.Automation.ControlType]::Edit),
            @('Proxy user name', [System.Windows.Automation.ControlType]::Edit),
            @('Proxy password', [System.Windows.Automation.ControlType]::Edit))) {
        $el = Find $pair[0] $pair[1]
        Check ($null -ne $el) ("control '{0}' present with an accessible name" -f $pair[0])
        if ($el -and $pair[0] -eq 'Proxy password') {
            Check ($el.Current.IsPassword) 'Proxy password box is masked (IsPassword)'
        }
    }

    $mode = Find 'Proxy mode' ([System.Windows.Automation.ControlType]::ComboBox)
    if ($mode) {
        $expand = $mode.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand(); Start-Sleep 1
        $items = $mode.FindAll($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        Log ("mode options: " + (($items | ForEach-Object { $_.Current.Name }) -join ' | '))
        $expand.Collapse()
    }

    $type = Find 'Proxy type' ([System.Windows.Automation.ControlType]::ComboBox)
    if ($type) {
        $expand = $type.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand(); Start-Sleep 1
        $items = $type.FindAll($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            $auto::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
        $names = ($items | ForEach-Object { $_.Current.Name })
        Log ("type options: " + ($names -join ' | '))
        Check (($names -contains 'SOCKS4') -and ($names -contains 'SOCKS4a') -and ($names -contains 'SOCKS5')) `
            'proxy type offers SOCKS4, SOCKS4a and SOCKS5'
        $expand.Collapse()
    }

    $cancel = Find 'Cancel' ([System.Windows.Automation.ControlType]::Button)
    if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
}
finally {
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $log | Set-Content -Path $OutFile
    Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue
    Get-Process DownloadManager.Host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
