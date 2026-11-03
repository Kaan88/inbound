[CmdletBinding()]
param(
    [string]$Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $root "artifacts\package\download-manager-$Version"
$packageFile = Join-Path $root "artifacts\package\download-manager-$Version.zip"

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

& (Join-Path $PSScriptRoot "publish.ps1") -Configuration Release
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if (Test-Path $packageRoot) { Remove-Item $packageRoot -Recurse -Force }
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
Copy-Item (Join-Path $root "artifacts\app") (Join-Path $packageRoot "app") -Recurse -Force
Copy-Item (Join-Path $root "artifacts\extension") (Join-Path $packageRoot "extension") -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "install.ps1") $packageRoot
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $packageRoot
Copy-Item (Join-Path $PSScriptRoot "register-native-host.ps1") $packageRoot
Copy-Item (Join-Path $root "README.md") $packageRoot
New-Item -ItemType Directory -Path (Join-Path $packageRoot "docs") -Force | Out-Null
Copy-Item (Join-Path $root "docs\*") (Join-Path $packageRoot "docs") -Recurse -Force

if (Test-Path $packageFile) { Remove-Item $packageFile -Force }
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $packageFile -CompressionLevel Optimal
Write-Host "Package: $packageFile"
