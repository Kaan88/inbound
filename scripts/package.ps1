[CmdletBinding()]
param(
    [string]$Version = "0.7.4",
    # The native torrent worker, passed on to publish.ps1 (default: artifacts\torrent-worker\win-x64\inbound-torrent.exe).
    [string]$TorrentWorker
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $root "artifacts\package\download-manager-$Version"
$packageFile = Join-Path $root "artifacts\package\download-manager-$Version.zip"

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release
# Self-contained like Setup: an update replaces the whole app folder, so it must not start
# depending on a .NET or Windows App SDK runtime that Setup never asked the PC to have.
$workerArgs = @{}
if ($TorrentWorker) { $workerArgs.TorrentWorker = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($TorrentWorker) }
& (Join-Path $PSScriptRoot "publish.ps1") -Configuration Release -SelfContained @workerArgs

if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item (Join-Path $root "artifacts\app") (Join-Path $packageRoot "app") -Recurse -Force
$installText = Get-Content (Join-Path $PSScriptRoot "install.ps1") -Raw
$installText = $installText.Replace('[string]$Version = "0.7.4"', "[string]`$Version = `"$Version`"")
Set-Content -Path (Join-Path $packageRoot "install.ps1") -Value $installText -Encoding UTF8
Copy-Item (Join-Path $PSScriptRoot "repair.ps1") $packageRoot
Copy-Item (Join-Path $PSScriptRoot "apply-update.ps1") $packageRoot
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $packageRoot
Copy-Item (Join-Path $PSScriptRoot "register-native-host.ps1") $packageRoot
# repair.ps1 and uninstall.ps1 close the app through it, as Setup does.
Copy-Item (Join-Path $PSScriptRoot "stop-for-setup.ps1") $packageRoot
Copy-Item (Join-Path $root "README.md") $packageRoot
# Only the documents meant for users, by name: docs\ also holds plans, mockups, store screenshots and whatever local
# file was put there, none of which belongs in a release (audit 4 L23).
$packagedDocs = @(
    "architecture.md",
    "browser-integration.md",
    "chrome-extension-privacy.md",
    "docker.md",
    "extraction.md",
    "ipc-protocol.md"
)
New-Item -ItemType Directory -Path (Join-Path $packageRoot "docs") -Force | Out-Null
foreach ($doc in $packagedDocs) {
    $docPath = Join-Path $root "docs\$doc"
    if (-not (Test-Path -LiteralPath $docPath -PathType Leaf)) { throw "A packaged document is missing: docs\$doc" }
    Copy-Item -LiteralPath $docPath (Join-Path $packageRoot "docs")
}

if (Test-Path $packageFile) { Remove-Item $packageFile -Force }
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $packageFile -CompressionLevel Optimal
Write-Host "Package: $packageFile"
