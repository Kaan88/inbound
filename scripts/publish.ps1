[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Output,
    # Bundle the .NET runtime and the Windows App SDK so the app runs on a PC without them.
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $root "artifacts\app"
}
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts")).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$artifactsPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar
$resolvedOutputPath = [IO.Path]::GetFullPath($Output).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $resolvedOutputPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish output must be a strict descendant of the repository's artifacts directory: $artifactsRoot"
}
$Output = $resolvedOutputPath
$extension = [IO.Path]::GetFullPath((Join-Path $root "extensions\chrome\dist"))
$extensionDestination = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $Output) "extension"))

function Assert-NoReparsePointInPath([string]$path) {
    $fullPath = [IO.Path]::GetFullPath($path)
    $currentPath = [IO.Path]::GetPathRoot($fullPath)
    $parts = $fullPath.Substring($currentPath.Length) -split '[\\/]'
    foreach ($part in $parts) {
        if ([string]::IsNullOrEmpty($part)) { continue }
        $currentPath = Join-Path $currentPath $part
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Publish paths must not contain reparse points: $currentPath"
            }
        }
    }
}

function Test-PathOverlap([string]$left, [string]$right) {
    $left = [IO.Path]::GetFullPath($left).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $right = [IO.Path]::GetFullPath($right).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    return $left.Equals($right, [StringComparison]::OrdinalIgnoreCase) -or
        $left.StartsWith($right + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        $right.StartsWith($left + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}

if (-not $extensionDestination.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Extension staging destination must be under the repository's artifacts directory: $extensionDestination"
}
if (Test-PathOverlap $extensionDestination $extension) {
    throw "Extension staging destination overlaps the extension source: $extensionDestination"
}
if (Test-PathOverlap $extensionDestination $Output) {
    throw "Extension staging destination overlaps the publish output: $extensionDestination"
}

# Validate every existing ancestor, including the workspace and artifacts roots,
# before any build or recursive deletion can follow a junction/symlink elsewhere.
Assert-NoReparsePointInPath $artifactsRoot
Assert-NoReparsePointInPath $Output
Assert-NoReparsePointInPath $extensionDestination
Assert-NoReparsePointInPath $extension

$resolvedOutput = $Output + [IO.Path]::DirectorySeparatorChar
$outputProcesses = Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
    $_.ExecutablePath -and $_.ExecutablePath.StartsWith($resolvedOutput, [StringComparison]::OrdinalIgnoreCase)
}
if ($outputProcesses) {
    $names = ($outputProcesses | ForEach-Object { "$($_.Name) (PID $($_.ProcessId))" }) -join ", "
    throw "Cannot publish: executable(s) under output directory are running: $names. Close them and retry."
}

if (Test-Path -LiteralPath $Output) {
    Remove-Item -LiteralPath $Output -Recurse -Force
}
New-Item -ItemType Directory -Path $Output -Force | Out-Null

function Publish-Project([string]$project, [string[]]$extraArgs = @()) {
    Write-Host "Publishing $project..."
    $runtimeArgs = if ($SelfContained) { @("-r", "win-x64", "--self-contained", "-p:WindowsAppSDKSelfContained=true") } else { @("--no-self-contained") }
    $argumentList = @("publish", $project, "-c", $Configuration, "-o", $Output) + $runtimeArgs + $extraArgs
    & dotnet @argumentList
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $project" }
}

Publish-Project (Join-Path $root "src\DownloadManager.Host\DownloadManager.Host.csproj")
Publish-Project (Join-Path $root "src\DownloadManager.NativeHost\DownloadManager.NativeHost.csproj")
Publish-Project (Join-Path $root "src\DownloadManager.Cli\DownloadManager.Cli.csproj")

# WinUI's incremental XAML resource generation can keep a stale .xbf after a
# markup change, which makes the published app fail at startup with
# "XAML parsing failed". Clean this project before publishing it.
$winuiProject = Join-Path $root "src\DownloadManager.Desktop.WinUI\DownloadManager.Desktop.WinUI.csproj"
Write-Host "Cleaning WinUI intermediates..."
$cleanArgs = @($winuiProject, "-c", $Configuration, "-p:Platform=x64")
# A self-contained build keeps its intermediates in a folder of its own.
if ($SelfContained) { $cleanArgs += @("-r", "win-x64") }
& dotnet clean @cleanArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "WinUI clean failed." }
Publish-Project $winuiProject @("-p:Platform=x64")

# dotnet publish for this unpackaged WinUI project does not copy the generated
# resource index, but the app cannot load MainWindow.xaml without it. Copy the
# exact .pri produced by the matching build next to the published executable.
$winuiBuildRoot = Join-Path $root "src\DownloadManager.Desktop.WinUI\bin\x64\$Configuration\net10.0-windows10.0.19041.0"
$winuiPriDir = if ($SelfContained) { Join-Path $winuiBuildRoot "win-x64" } else { $winuiBuildRoot }
$winuiPri = Join-Path $winuiPriDir "DownloadManager.Desktop.WinUI.pri"
if (-not (Test-Path $winuiPri -PathType Leaf)) {
    throw "WinUI resource index was not produced at expected path: $winuiPri"
}
Copy-Item $winuiPri (Join-Path $Output "DownloadManager.Desktop.WinUI.pri") -Force

Push-Location (Join-Path $root "extensions\chrome")
try {
    if (-not (Test-Path "node_modules")) {
        npm ci
        if ($LASTEXITCODE -ne 0) { throw "Extension dependency installation failed (npm ci)." }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw "Extension build failed." }
}
finally {
    Pop-Location
}

$resolvedExtension = [IO.Path]::GetFullPath($extension)
$resolvedOutputFull = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $extensionDestination) {
    Remove-Item -LiteralPath $extensionDestination -Recurse -Force
}
New-Item -ItemType Directory -Path $extensionDestination -Force | Out-Null
Copy-Item (Join-Path $extension "*") $extensionDestination -Recurse -Force

$sourceFiles = Get-ChildItem $extension -File -Recurse
$destinationFiles = Get-ChildItem $extensionDestination -File -Recurse
$sourceRelativePaths = @($sourceFiles | ForEach-Object { $_.FullName.Substring($resolvedExtension.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1) } | Sort-Object)
$destinationRelativePaths = @($destinationFiles | ForEach-Object { $_.FullName.Substring($extensionDestination.TrimEnd([IO.Path]::DirectorySeparatorChar).Length + 1) } | Sort-Object)
if (($sourceRelativePaths -join "`n") -cne ($destinationRelativePaths -join "`n")) {
    throw "Extension staging file layout does not match source at $extensionDestination"
}
foreach ($relativePath in $sourceRelativePaths) {
    $sourceHash = (Get-FileHash (Join-Path $extension $relativePath) -Algorithm SHA256).Hash
    $destinationHash = (Get-FileHash (Join-Path $extensionDestination $relativePath) -Algorithm SHA256).Hash
    if ($sourceHash -ne $destinationHash) {
        throw "Extension staging hash mismatch: $relativePath"
    }
}
if (Test-Path (Join-Path $extensionDestination "dist")) {
    throw "Extension staging unexpectedly contains a nested dist directory."
}

$required = @(
    "DownloadManager.Host.exe",
    "DownloadManager.Desktop.WinUI.exe",
    "DownloadManager.Desktop.WinUI.pri",
    "app.ico",
    "DownloadManager.NativeHost.exe",
    "DownloadManager.Cli.exe"
)
foreach ($file in $required) {
    if (-not (Test-Path (Join-Path $Output $file))) {
        throw "Published output is missing $file"
    }
}

Write-Host "Published to $Output"
