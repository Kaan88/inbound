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
$cliExe = Join-Path $root "src\DownloadManager.Cli\bin\$Configuration\net10.0\dm.exe"
if (-not (Test-Path $cliExe)) { $cliExe = Join-Path $root "src\DownloadManager.Cli\bin\$Configuration\net10.0\DownloadManager.Cli.exe" }
$hostProc = Start-Process $hostExe -ArgumentList '--background' -PassThru -WindowStyle Hidden
Start-Sleep 4
# Saved password so the "Forget" checkbox is shown; the dialog is cancelled, so it is cleared via CLI at the end.
& $cliExe settings set proxymode manual | Out-Null
& $cliExe settings set proxyaddress 127.0.0.1:3128 | Out-Null
& $cliExe settings set proxyuser uicheck | Out-Null
& $cliExe settings set proxypass ui-check-secret | Out-Null
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

    $forget = Find 'Forget the saved proxy password' ([System.Windows.Automation.ControlType]::CheckBox)
    Check ($null -ne $forget) "'Forget the saved proxy password' checkbox shown when a password is saved"
    if ($forget) {
        $forget.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1
        $pw = Find 'Proxy password' ([System.Windows.Automation.ControlType]::Edit)
        Check ($pw -and -not $pw.Current.IsEnabled) 'checking Forget disables the password box'
        $forget.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    }

    $cancel = Find 'Cancel' ([System.Windows.Automation.ControlType]::Button)
    if ($cancel) { $cancel.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
    Start-Sleep 2

    # Tasks -> Clean up leftover partial files...
    $tasks = Find 'Tasks' ([System.Windows.Automation.ControlType]::MenuItem)
    Check ($null -ne $tasks) 'Tasks menu found'
    if ($tasks) {
        $tasks.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 1
        $cleanItem = $auto::RootElement.FindFirst($scope::Descendants, (New-Object System.Windows.Automation.PropertyCondition(
            $auto::NameProperty, 'Clean up leftover partial files...')))
        Check ($null -ne $cleanItem) "'Clean up leftover partial files...' menu item present"
        if ($cleanItem) {
            $cleanItem.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 2
            foreach ($b in 'Quarantine', 'Delete', 'Cancel') {
                Check ($null -ne (Find $b ([System.Windows.Automation.ControlType]::Button))) "cleanup dialog has '$b'"
            }
            $c = Find 'Cancel' ([System.Windows.Automation.ControlType]::Button)
            if ($c) { $c.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
        }
    }
}
finally {
    if ($cliExe -and (Test-Path $cliExe)) {
        & $cliExe settings set proxypass --clear | Out-Null
        & $cliExe settings set proxymode none | Out-Null
    }
    $dir = Split-Path -Parent $OutFile
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $log | Set-Content -Path $OutFile
    Stop-Process -Id $ui.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $hostProc.Id -Force -ErrorAction SilentlyContinue
    Get-Process DownloadManager.Host -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}
