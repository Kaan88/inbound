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
        "tests\DownloadManager.Integration.Tests\DownloadManager.Integration.Tests.csproj"
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
    }
    finally {
        Pop-Location
    }

    Write-Host "All tests passed."
}
finally {
    Pop-Location
}
