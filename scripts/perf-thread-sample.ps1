param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Seconds = 60,
    [string]$NameLike = 'DownloadManager.%'
)
# Read-only per-thread CPU sampler for scripts/perf-window-active.sh (PERF.15, WinUI). Starts nothing, changes nothing.
# Finds the processes running from $Root (one test instance's own copy of the binaries), reads each thread's CPU time at
# the start and the end of $Seconds, and writes one CSV row per process: CPU over the window, the five busiest threads
# (id, ms, whether it is the process's first thread), and private bytes and working set at both ends. In a WinUI 3
# desktop app XAML runs on the main thread, the process's first thread, so `mainMs` is the UI thread's CPU.
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'

function Snapshot {
    $result = @{}
    foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name LIKE '$NameLike'" -ErrorAction SilentlyContinue)) {
        if (-not ($w.ExecutablePath -and $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))) { continue }
        $p = Get-Process -Id ([int]$w.ProcessId) -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        $threads = @{}
        $first = $null; $firstStart = [DateTime]::MaxValue
        foreach ($t in $p.Threads) {
            try {
                $threads[$t.Id] = $t.TotalProcessorTime.TotalMilliseconds
                if ($t.StartTime -lt $firstStart) { $firstStart = $t.StartTime; $first = $t.Id }
            } catch { }
        }
        $result[$p.Id] = [pscustomobject]@{
            Name = $p.ProcessName; Cpu = $p.TotalProcessorTime.TotalMilliseconds; Threads = $threads; First = $first
            Private = $p.PrivateMemorySize64; WorkingSet = $p.WorkingSet64
        }
    }
    return $result
}

$before = Snapshot
$clock = [Diagnostics.Stopwatch]::StartNew()
Start-Sleep -Seconds $Seconds
$after = Snapshot
$elapsed = $clock.Elapsed.TotalSeconds

'name,pid,seconds,cpuMs,cpuPct,mainThreadMs,mainThreadPct,top5,privateMBStart,privateMBEnd,workingSetMBEnd' |
    Set-Content -LiteralPath $Out -Encoding ascii
foreach ($id in $after.Keys) {
    if (-not $before.ContainsKey($id)) { continue }
    $a = $before[$id]; $b = $after[$id]
    $deltas = foreach ($tid in $b.Threads.Keys) {
        $start = if ($a.Threads.ContainsKey($tid)) { $a.Threads[$tid] } else { 0 }
        [pscustomobject]@{ Id = $tid; Ms = $b.Threads[$tid] - $start }
    }
    $main = ($deltas | Where-Object { $_.Id -eq $a.First } | Select-Object -First 1).Ms
    if ($null -eq $main) { $main = 0 }
    $top = ($deltas | Sort-Object Ms -Descending | Select-Object -First 5 |
        ForEach-Object { '{0}{1}:{2:0}' -f $_.Id, $(if ($_.Id -eq $a.First) { '*' } else { '' }), $_.Ms }) -join ' '
    $cpu = $b.Cpu - $a.Cpu
    '{0},{1},{2:0.0},{3:0},{4:0.00},{5:0},{6:0.00},{7},{8:0.0},{9:0.0},{10:0.0}' -f $b.Name, $id, $elapsed, $cpu,
        ($cpu / 10 / $elapsed), $main, ($main / 10 / $elapsed), $top, ($a.Private / 1MB), ($b.Private / 1MB), ($b.WorkingSet / 1MB) |
        Add-Content -LiteralPath $Out -Encoding ascii
}
