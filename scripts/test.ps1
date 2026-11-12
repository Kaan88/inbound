[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $projects = @(
        "tests\DownloadManager.Core.Tests\DownloadManager.Core.Tests.csproj",
        "tests\DownloadManager.Engine.Tests\DownloadManager.Engine.Tests.csproj",
        "tests\DownloadManager.Integration.Tests\DownloadManager.Integration.Tests.csproj",
        "tests\DownloadManager.TorBox.Tests\DownloadManager.TorBox.Tests.csproj",
        "tests\DownloadManager.Archives.Tests\DownloadManager.Archives.Tests.csproj",
        "tests\DownloadManager.Containers.Tests\DownloadManager.Containers.Tests.csproj",
        "tests\DownloadManager.Torrents.Tests\DownloadManager.Torrents.Tests.csproj",
        "tests\DownloadManager.Server.Tests\DownloadManager.Server.Tests.csproj",
        "tests\DownloadManager.Tray.Tests\DownloadManager.Tray.Tests.csproj"
    )

    foreach ($project in $projects) {
        Write-Host "Testing $project..."
        dotnet test $project -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "Tests failed: $project" }
    }

    Push-Location (Join-Path $root "extensions\chrome")
    try {
        npx tsc --noEmit
        if ($LASTEXITCODE -ne 0) { throw "Extension typecheck failed." }
        # The extension's own unit tests (node:test with a mocked chrome API).
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Extension tests failed." }
    }
    finally {
        Pop-Location
    }

    # The web UI's own tests (vitest) and its type check (svelte-check).
    Push-Location (Join-Path $root "webui")
    try {
        if (-not (Test-Path "node_modules")) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "Web UI dependency installation failed (npm ci)." }
        }
        npm test
        if ($LASTEXITCODE -ne 0) { throw "Web UI tests failed." }
        npx svelte-check
        if ($LASTEXITCODE -ne 0) { throw "Web UI type check failed." }
    }
    finally {
        Pop-Location
    }

    Write-Host "All tests passed."
}
finally {
    Pop-Location
}
