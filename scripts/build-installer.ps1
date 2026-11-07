[CmdletBinding()]
param(
    [string]$Version = "0.5.2",
    [string]$Iscc
)

# Builds artifacts\installer\DownloadManagerSetup-<Version>.exe: a per-user Inno Setup installer
# with a self-contained app (bundles .NET and the Windows App SDK).
$ErrorActionPreference = "Stop"
$versionPattern = '^\d+(?:\.\d+){2,3}$'
if ($Version -notmatch $versionPattern) {
    throw "Version must contain exactly 3 or 4 numeric components (for example, 1.2.3 or 1.2.3.4)."
}
$root = Split-Path -Parent $PSScriptRoot
$versionPropsPath = Join-Path $root "Directory.Build.props"
if (-not (Test-Path -LiteralPath $versionPropsPath -PathType Leaf)) {
    throw "Project version source was not found: $versionPropsPath"
}
$versionProps = [xml](Get-Content -LiteralPath $versionPropsPath -Raw)
$versionNodes = $versionProps.SelectNodes('/Project/PropertyGroup/Version')
if ($versionNodes.Count -ne 1) {
    throw "Expected exactly one Version in Directory.Build.props; found $($versionNodes.Count)."
}
$sourceVersion = $versionNodes[0].InnerText.Trim()
if ($Version -cne $sourceVersion) {
    throw "Installer version $Version must match the project version $sourceVersion in Directory.Build.props."
}

$appOut = Join-Path $root "artifacts\installer-app"
$extOut = Join-Path $root "artifacts\installer-extension"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts")).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$artifactsPrefix = $artifactsRoot + [IO.Path]::DirectorySeparatorChar
foreach ($target in @($appOut, $extOut)) {
    $canonicalTarget = [IO.Path]::GetFullPath($target).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if (-not $canonicalTarget.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Installer staging paths must be strict descendants of artifacts: $canonicalTarget"
    }
}
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
                throw "Installer staging paths must not contain reparse points: $currentPath"
            }
        }
    }
}
Assert-NoReparsePointInPath $artifactsRoot
Assert-NoReparsePointInPath $appOut
Assert-NoReparsePointInPath $extOut

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path $env:ProgramFiles "Inno Setup 7\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup (ISCC.exe) not found; pass -Iscc <path>." }

& (Join-Path $PSScriptRoot "publish.ps1") -Configuration Release -Output ([IO.Path]::GetFullPath($appOut)) -SelfContained
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# publish.ps1 copies the extension next to the output folder as "extension".
$publishedExtension = Join-Path (Split-Path -Parent $appOut) "extension"
if (Test-Path -LiteralPath $extOut) { Remove-Item -LiteralPath $extOut -Recurse -Force }
Copy-Item -LiteralPath $publishedExtension -Destination $extOut -Recurse -Force

& $Iscc "/DAppVersion=$Version" "/DSourceRoot=$(Join-Path $root 'artifacts')" (Join-Path $root "installer\DownloadManager.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }

$setup = Join-Path $root "artifacts\installer\DownloadManagerSetup-$Version.exe"
# The repository root carries an explicit Low mandatory label (agent sandbox), which files built here
# inherit. Windows starts a Low-labelled exe at Low integrity, and Inno Setup then cannot create its
# %TEMP%\is-*.tmp folder ("Error 5: Access is denied") unless run as admin. Give the setup an explicit
# Medium label so a local build behaves like one downloaded from the release page.
& icacls $setup /setintegritylevel M | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not set Medium integrity on $setup." }
Write-Host ("Installer: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
