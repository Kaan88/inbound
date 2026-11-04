$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $shellCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ClassNameProperty, 'Shell_TrayWnd')
    $shell = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $shellCondition)
    if (-not $shell) { Write-Output 'NO SHELL_TRAY'; exit 1 }
    $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Show Hidden Icons')
    $chevron = $shell.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $nameCondition)
    if (-not $chevron) { Write-Output 'NO OVERFLOW CHEVRON'; exit 0 }
    $pattern = $null
    if ($chevron.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
        $pattern.Invoke()
        Start-Sleep -Milliseconds 900
        Write-Output 'OVERFLOW OPENED'
    } else {
        Write-Output 'OVERFLOW NOT INVOKABLE'
    }
} catch {
    Write-Output "OVERFLOW ERROR: $($_.Exception.Message)"
}
