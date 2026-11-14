# PERF.8 baseline on Windows: publishes the Host, CLI and native host (self-contained Release) and runs tools\Bench.
# Read tools\Bench\README.md first.
#
#   powershell -ExecutionPolicy Bypass -File scripts\perf-bench.ps1 -Out artifacts\perf-bench\windows-pc
#   powershell -ExecutionPolicy Bypass -File scripts\perf-bench.ps1 -Out artifacts\perf-bench\smoke -Quick
#
# Everything else goes to the tool: -Extra "--size 8G --repeat 5 --disk-dir D:\bench --ram-dir R:\bench".
# There is no RAM-backed folder by default on Windows: pass --ram-dir (a RAM disk) or the RAM rows are skipped.
# The Host it starts has its own instance name and data folder; the installed app is never touched.
param(
    [Parameter(Mandatory = $true)][string]$Out,
    [switch]$Quick,
    [switch]$NoPublish,
    [string]$BinDir = '',
    [string]$Rid = 'win-x64',
    [string]$Extra = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $BinDir) { $BinDir = Join-Path $root 'artifacts\bench-bin' }
Set-Location $root

dotnet build tools\Bench -c Release -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
if (-not $NoPublish) {
    if (Test-Path $BinDir) { Remove-Item -Recurse -Force $BinDir }
    foreach ($project in 'Host', 'Cli', 'NativeHost') {
        dotnet publish "src\DownloadManager.$project" -c Release -r $Rid --self-contained true -p:PublishSingleFile=false `
            -p:DebugType=none -o (Join-Path $BinDir $project) -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "publish of $project failed" }
    }
}

$toolArgs = @('all', '--out', $Out,
    '--cli', (Join-Path $BinDir 'Cli\DownloadManager.Cli.exe'),
    '--native-host', (Join-Path $BinDir 'NativeHost\DownloadManager.NativeHost.exe'),
    '--host', (Join-Path $BinDir 'Host\DownloadManager.Host.exe'))
if ($Quick) { $toolArgs += '--quick' }
if ($Extra -notmatch '--disk-dir') { $toolArgs += @('--disk-dir', (Join-Path $root 'artifacts\bench-work')) }
if ($Extra) { $toolArgs += ($Extra -split '\s+' | Where-Object { $_ }) }
dotnet tools\Bench\bin\Release\net10.0\Bench.dll @toolArgs
exit $LASTEXITCODE
