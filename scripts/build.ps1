[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipExtension,
    [switch]$SkipWebUi
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $SkipWebUi) {
        # First, so the server's build copies webui\dist into its wwwroot (DownloadManager.Server.csproj).
        Write-Host "Building web UI..."
        Push-Location (Join-Path $root "webui")
        try {
            if (-not (Test-Path "node_modules")) {
                npm ci
                if ($LASTEXITCODE -ne 0) { throw "web UI npm ci failed" }
            }
            npm run check
            if ($LASTEXITCODE -ne 0) { throw "web UI type check failed" }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw "web UI build failed" }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host "Building solution ($Configuration)..."
    dotnet build DownloadManager.slnx -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    if (-not $SkipExtension) {
        Write-Host "Building Chrome extension..."
        Push-Location (Join-Path $root "extensions\chrome")
        try {
            if (-not (Test-Path "node_modules")) {
                npm ci
            }
            npx tsc --noEmit
            if ($LASTEXITCODE -ne 0) { throw "extension typecheck failed" }
            npm run build
            if ($LASTEXITCODE -ne 0) { throw "extension build failed" }
        }
        finally {
            Pop-Location
        }
    }

    Write-Host "Build complete."
}
finally {
    Pop-Location
}
