param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$Out,
    [int]$Minutes = 30,
    [int]$EverySeconds = 10
)
# Read-only idle sampler for scripts\perf-window.sh (PERF.5). Starts nothing, changes nothing. Every
# $EverySeconds it finds the processes running from $Root (one test instance's own copy of the binaries) and
# writes CPU time, memory, handles, threads and context switches per second (a 2 s thread-counter sample,
# summed over each process's threads) as CSV.
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
'utc,name,pid,cpuMs,privateBytes,workingSet,handles,threads,switchesPerSec' | Set-Content -LiteralPath $Out -Encoding ascii
$end = (Get-Date).AddMinutes($Minutes)
while ((Get-Date) -lt $end) {
    $ours = @{}
    foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name LIKE 'DownloadManager.%'" -ErrorAction SilentlyContinue)) {
        if ($w.ExecutablePath -and $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { $ours[[int]$w.ProcessId] = $true }
    }
    $switches = @{}
    if ($ours.Count -gt 0) {
        try {
            $c = Get-Counter '\Thread(DownloadManager*)\Context Switches/sec', '\Thread(DownloadManager*)\ID Process' -SampleInterval 2 -MaxSamples 1 -ErrorAction Stop
            $cs = @{}; $owner = @{}
            foreach ($s in $c.CounterSamples) {
                if ($s.Status -ne 0) { continue }
                $inst = $s.Path.Substring(0, $s.Path.LastIndexOf([char]92)).ToLowerInvariant()
                if ($s.Path -like '*context switches*') { $cs[$inst] = $s.CookedValue } else { $owner[$inst] = [int]$s.CookedValue }
            }
            foreach ($k in $cs.Keys) { $o = $owner[$k]; if ($ours.ContainsKey($o)) { $switches[$o] += $cs[$k] } }
        } catch { }
    }
    $now = [DateTimeOffset]::UtcNow.ToString('O')
    foreach ($id in $ours.Keys) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if (-not $p) { continue }
        '{0},{1},{2},{3},{4},{5},{6},{7},{8:0.0}' -f $now, $p.ProcessName, $id, [int64]$p.TotalProcessorTime.TotalMilliseconds,
            $p.PrivateMemorySize64, $p.WorkingSet64, $p.HandleCount, $p.Threads.Count, [double]$switches[$id] |
            Add-Content -LiteralPath $Out -Encoding ascii
    }
    Start-Sleep -Seconds ([Math]::Max(1, $EverySeconds - 2))
}
'done'
