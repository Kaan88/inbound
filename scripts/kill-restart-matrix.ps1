[CmdletBinding()]
param(
    [int]$SizeMiB = 24,
    [int]$BytesPerSecond = 262144,
    [int[]]$KillAtPercent = @(20, 50, 80),
    [int[]]$Connections = @(1, 8),
    [string]$AppDir,
    # A test instance name: with it, the matrix's Host gets its own pipe and runs beside an installed copy.
    [string]$Instance = ("killmatrix" + (Get-Random -Minimum 1000 -Maximum 9999))
)

# Kill/restart matrix (ROADMAP P2.7 / P5.6 / X.5): for each connection count, downloads a
# throttled file through the real Host, force-kills the Host (simulated crash) at each listed
# progress point, restarts it, and finally checks the SHA-256 against an independent copy.
# Uses an isolated state directory (DOWNLOADMANAGER_DATA) so the user's own data is untouched.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (-not $AppDir) {
    $AppDir = Join-Path $root "src\DownloadManager.Host\bin\Release\net10.0-windows"
}
$hostExe = Join-Path $AppDir "DownloadManager.Host.exe"
$cliExe = Join-Path $root "src\DownloadManager.Cli\bin\Release\net10.0\DownloadManager.Cli.exe"
$serverExe = Join-Path $root "tools\TestServer\bin\Release\net10.0\TestServer.exe"
foreach ($exe in $hostExe, $cliExe, $serverExe) {
    if (-not (Test-Path $exe)) { throw "Missing $exe (build Release first)." }
}
# Only this matrix's own Host counts: with DOWNLOADMANAGER_INSTANCE set, other copies (the installed app,
# other test instances) use other pipes and are left alone.

$work = Join-Path $root ("artifacts\kill-restart\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$data = Join-Path $work "data"
$downloads = Join-Path $work "downloads"
New-Item -ItemType Directory -Path $data, $downloads -Force | Out-Null
$previousData = $env:DOWNLOADMANAGER_DATA
$previousInstance = $env:DOWNLOADMANAGER_INSTANCE
$env:DOWNLOADMANAGER_DATA = $data
$env:DOWNLOADMANAGER_INSTANCE = $Instance
$port = Get-Random -Minimum 20000 -Maximum 40000
$base = "http://127.0.0.1:$port"
$size = $SizeMiB * 1MB
$results = New-Object System.Collections.Generic.List[object]
$failures = 0

function Invoke-MatrixCli([string[]]$cliArgs) {
    $out = & $cliExe @cliArgs 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "CLI failed ($LASTEXITCODE): $out" }
    return $out
}
function JobStatus([string]$id) {
    $text = Invoke-MatrixCli -cliArgs @('status', $id, '--json')
    try { return ($text | ConvertFrom-Json) } catch { return $null }
}
function StartHost {
    $p = Start-Process -FilePath $hostExe -ArgumentList '--background' -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work "host-out.txt") -RedirectStandardError (Join-Path $work "host-err.txt")
    $script:hostProcess = $p
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($p.HasExited) { throw "Host exited with code $($p.ExitCode)." }
        try { if ((Invoke-MatrixCli -cliArgs @('host', 'status')) -match 'protocol') { return $p } } catch { }
    }
    throw "Host did not become reachable."
}
function Fail([string]$message) {
    $script:failures++
    Write-Host "FAIL: $message"
}

$server = $null
$hostProcess = $null
try {
    $server = Start-Process -FilePath $serverExe -ArgumentList '--port', $port -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $work "server-out.txt") -RedirectStandardError (Join-Path $work "server-err.txt")
    $serverReady = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        if ($server.HasExited) { throw "TestServer exited with code $($server.ExitCode)." }
        try { Invoke-WebRequest "$base/file/ping.bin?size=16" -UseBasicParsing -TimeoutSec 2 | Out-Null; $serverReady = $true; break } catch { }
    }
    if (-not $serverReady) { throw "TestServer did not become reachable at $base." }

    $hostProcess = StartHost
    foreach ($connectionCount in $Connections) {
        $name = "matrix-c$connectionCount.bin"
        $seed = 700 + $connectionCount
        $url = "$base/file/$name`?size=$size&seed=$seed&bps=$BytesPerSecond"

        # Independent reference copy, fetched unthrottled outside the app.
        $reference = Join-Path $work "reference-$name"
        Invoke-WebRequest "$base/file/$name`?size=$size&seed=$seed" -OutFile $reference -UseBasicParsing
        $expected = (Get-FileHash $reference -Algorithm SHA256).Hash.ToLowerInvariant()

        $added = Invoke-MatrixCli -cliArgs @('add', $url, '--dest', $downloads, '--connections', "$connectionCount", '--json') | ConvertFrom-Json
        if (-not $added.accepted) { throw "add failed: $($added.error)" }
        $id = $added.job.id
        Write-Host "connections=$connectionCount job=$id size=$size"

        foreach ($percent in $KillAtPercent) {
            $target = [long]($size * $percent / 100)
            $deadline = (Get-Date).AddSeconds(120)
            do {
                Start-Sleep -Milliseconds 200
                $job = JobStatus $id
            } while ($job -and $job.receivedBytes -lt $target -and $job.state -ne 'completed' -and (Get-Date) -lt $deadline)
            if (-not $job -or $job.receivedBytes -lt $target) { throw "c=$connectionCount did not reach $percent% before timeout." }
            if ($job.state -eq 'completed') { Fail "c=$connectionCount completed before the $percent% kill point"; break }

            Stop-Process -Id $hostProcess.Id -Force
            $hostProcess.WaitForExit(10000) | Out-Null
            $before = $job.receivedBytes
            $partBytes = if (Test-Path $job.partFilePath) { (Get-Item $job.partFilePath).Length } else { -1 }

            $hostProcess = StartHost
            $after = JobStatus $id
            if (-not $after) { throw "Job $id disappeared after restart." }
            $kept = $after.receivedBytes
            if ($after.state -notin @('downloading', 'queued', 'connecting')) {
                Invoke-MatrixCli -cliArgs @('resume', $id) | Out-Null
            }
            $row = [pscustomobject]@{
                connections = $connectionCount; killAt = "$percent%"; bytesAtKill = $before
                partFileBytes = $partBytes; keptAfterRestart = $kept; stateAfterRestart = $after.state
            }
            $results.Add($row)
            Write-Host ("  killed at {0}%: reported {1:N0}, part {2:N0}, kept after restart {3:N0} ({4})" -f `
                $percent, $before, $partBytes, $kept, $after.state)
            if ($kept -le 0) { Fail "c=$connectionCount kill $percent%: progress was not preserved" }
            if ($kept -gt $size) { Fail "c=$connectionCount kill $percent%: kept bytes exceed the file size" }
        }

        $deadline = (Get-Date).AddSeconds(240)
        do {
            Start-Sleep -Milliseconds 500
            $job = JobStatus $id
            if (-not $job) { throw "Could not read status for job $id during final completion wait." }
            if ($job.state -in @('paused', 'failed')) { Invoke-MatrixCli -cliArgs @('resume', $id) | Out-Null }
        } while ($job.state -ne 'completed' -and (Get-Date) -lt $deadline)
        if ($job.state -ne 'completed') { Fail "c=$connectionCount did not complete (state $($job.state))"; continue }

        $actual = (Get-FileHash $job.finalFilePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $ok = $actual -eq $expected
        Write-Host ("  completed {0:N0} bytes; sha256 {1} -> {2}" -f (Get-Item $job.finalFilePath).Length, $actual, $(if ($ok) { 'MATCH' } else { 'MISMATCH' }))
        if (-not $ok) { Fail "c=$connectionCount hash mismatch (expected $expected)" }
        $leftover = Get-ChildItem $downloads -Filter '*.part' -ErrorAction SilentlyContinue
        if ($leftover) { Fail "c=$connectionCount left .part files: $($leftover.Name -join ', ')" }
    }
}
finally {
    try {
        if ($hostProcess -and -not $hostProcess.HasExited) {
            try { Invoke-MatrixCli -cliArgs @('host', 'shutdown') | Out-Null } catch { Write-Warning $_ }
            Start-Sleep -Seconds 2
            if (-not $hostProcess.HasExited) {
                Stop-Process -Id $hostProcess.Id -Force
                $hostProcess.WaitForExit(10000) | Out-Null
            }
        }
        if ($server -and -not $server.HasExited) {
            Stop-Process -Id $server.Id -Force
            $server.WaitForExit(10000) | Out-Null
        }
    }
    finally {
        $env:DOWNLOADMANAGER_DATA = $previousData
        $env:DOWNLOADMANAGER_INSTANCE = $previousInstance
    }
}

$results | Format-Table -AutoSize | Out-String | Write-Host
$results | ConvertTo-Json | Set-Content (Join-Path $work "results.json") -Encoding UTF8
if ($failures -gt 0) { Write-Host "RESULT: $failures failure(s). Work dir: $work"; exit 1 }
Write-Host "RESULT: all kill/restart cases passed. Work dir: $work"
