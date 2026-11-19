param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][DateTimeOffset]$Due,
    [Parameter(Mandatory)][string]$FixtureLog,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe",
    [ValidateRange(1, 120)][int]$MinutesAfterDue = 30
)

$ErrorActionPreference = 'Stop'
if ($Serial -notmatch ':\d+$') { throw 'Use the explicit Wi-Fi ADB address and port.' }
if ($Due -le [DateTimeOffset]::Now) { throw 'Start observing before the scheduled minute.' }
$FixtureLog = (Resolve-Path -LiteralPath $FixtureLog).Path
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$out = Join-Path $OutputDirectory 'samples.jsonl'
$deadline = $Due.AddMinutes($MinutesAfterDue)

function Read-Device([string]$Command) {
    $result = & $Adb -s $Serial shell $Command
    if ($LASTEXITCODE -ne 0) { throw "ADB cannot observe: $Command" }
    $result -join "`n"
}

function Read-PayloadTrace {
    @(Select-String -LiteralPath $FixtureLog -Pattern '^TRACE (begin|end) ' |
        ForEach-Object { $_.Line })
}

if (@(Read-PayloadTrace).Count) { throw 'Use a dedicated fixture with no earlier payload requests.' }
do {
    $now = [DateTimeOffset]::Now
    $idle = Read-Device 'dumpsys deviceidle'
    $battery = Read-Device 'dumpsys battery'
    if ($idle -notmatch '(?m)^\s*mState=(\w+)\s+mLightState=(\w+)') {
        throw 'Cannot parse deep and light idle state.'
    }
    $deep, $light = $Matches[1], $Matches[2]
    $trace = @(Read-PayloadTrace)
    $sample = [ordered]@{
        at = $now.ToString('o')
        due = $Due.ToString('o')
        deep = $deep
        light = $light
        forcedIdleOff = $idle -match '(?m)^\s*mForceIdle=false\s*$'
        locked = $idle -match '(?m)^\s*mScreenLocked=true\s*$'
        screenOff = $idle -match '(?m)^\s*mScreenOn=false\s*$'
        unplugged = $idle -match '(?m)^\s*mCharging=false\s*$' -and
                    $battery -match '(?m)^\s*USB powered: false\s*$' -and
                    $battery -notmatch '(?m)^\s*\w+ powered: true\s*$'
        queueJob = Read-Device 'cmd jobscheduler get-job-state app.inbound.android 4100 2>&1 || true'
        sessionJob = Read-Device 'cmd jobscheduler get-job-state app.inbound.android 1 2>&1 || true'
        payloadBegins = @($trace -match '^TRACE begin ').Count
        payloadEnds = @($trace -match '^TRACE end ').Count
    }
    $sample | ConvertTo-Json -Compress | Tee-Object -FilePath $out -Append
    if (!$sample.forcedIdleOff -or !$sample.locked -or !$sample.screenOff -or !$sample.unplugged) {
        throw 'Natural locked, screen-off and unplugged preconditions were not observed; see last sample.'
    }
    if ($sample.payloadEnds -gt 0 -or $now -ge $deadline) { break }
    $interval = if ($now -ge $Due.AddMinutes(-2)) { 10 } else { 60 }
    Start-Sleep -Seconds $interval
} while ($true)

Read-PayloadTrace | Set-Content -LiteralPath (Join-Path $OutputDirectory 'payload-trace.txt')
Write-Output 'Observer finished. Compare the due-time samples and trace, then verify the final file hash separately.'
