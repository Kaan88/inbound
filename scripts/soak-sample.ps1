param(
    [Parameter(Mandatory = $true)][string]$Root,
    [string]$Label = ""
)
# Read-only sampler for scripts\soak.sh. Starts nothing and changes nothing: it finds the processes
# running from $Root (the soak's own copy of the binaries) and prints one CSV line for each.
# Columns: utc,label,name,pid,privateBytes,workingSet,handles,threads,cpuMs
$prefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
$now = [DateTimeOffset]::UtcNow.ToString('O')
foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name LIKE 'DownloadManager.%' OR Name = 'TestServer.exe'" -ErrorAction SilentlyContinue)) {
    if (-not $w.ExecutablePath -or -not $w.ExecutablePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $p = Get-Process -Id $w.ProcessId -ErrorAction SilentlyContinue
    if (-not $p) { continue }
    '{0},{1},{2},{3},{4},{5},{6},{7},{8}' -f $now, $Label, $p.ProcessName, $p.Id, $p.PrivateMemorySize64, $p.WorkingSet64,
        $p.HandleCount, $p.Threads.Count, [int64]$p.TotalProcessorTime.TotalMilliseconds
}
