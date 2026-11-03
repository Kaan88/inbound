[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Output
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "artifacts\app"
}
$extension = Join-Path $root "extensions\chrome\dist"

Get-Process DownloadManager.Host, DownloadManager.Desktop.WinUI, TestServer -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

if (Test-Path $Output) {
    Remove-Item $Output -Recurse -Force
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null

function Publish-Project([string]$project, [string[]]$extraArgs = @()) {
    Write-Host "Publishing $project..."
    $argumentList = @("publish", $project, "-c", $Configuration, "-o", $Output, "--no-self-contained") + $extraArgs
    & dotnet @argumentList
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

Publish-Project (Join-Path $root "src\DownloadManager.Host\DownloadManager.Host.csproj")
Publish-Project (Join-Path $root "src\DownloadManager.NativeHost\DownloadManager.NativeHost.csproj")
Publish-Project (Join-Path $root "src\DownloadManager.Cli\DownloadManager.Cli.csproj")
Publish-Project (Join-Path $root "src\DownloadManager.Desktop.WinUI\DownloadManager.Desktop.WinUI.csproj") @("-p:Platform=x64")

if (-not (Test-Path (Join-Path $extension "manifest.json"))) {
    Push-Location (Join-Path $root "extensions\chrome")
    try {
        if (-not (Test-Path "node_modules")) { npm ci }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw "Extension build failed." }
    }
    finally {
        Pop-Location
    }
}

Copy-Item $extension (Join-Path (Split-Path -Parent $Output) "extension") -Recurse -Force

$required = @(
    "DownloadManager.Host.exe",
    "DownloadManager.Desktop.WinUI.exe",
    "DownloadManager.NativeHost.exe",
    "DownloadManager.Cli.exe"
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $Output $file))) {
        throw "Published output is missing $file"
    }
}

Write-Host "Published to $Output"
