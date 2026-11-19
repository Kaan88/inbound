<#
.SYNOPSIS
  Prepares a verified APK and signed Android manifest locally; never publishes.
.DESCRIPTION
  Versioned APK/manifest belong on android-v<version>; the same signed manifest
  also goes on android-latest. Both releases use latest=false. No secret values
  go to builds, arguments, logs or output. A complete staged directory is renamed
  atomically; an existing final directory is never overwritten.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ApkPath,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][long]$VersionCode,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+(?:\.[0-9]+){1,3}$')][string]$Version,
    [Parameter(Mandatory)][string]$JavaSdkDirectory,
    [string]$AndroidSdkDirectory = (Join-Path $env:LOCALAPPDATA 'Android/Sdk'),
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE 'Downloads/secrets/update-signing.pem'),
    [string]$OutputDirectory,
    [string]$ReleaseNotesPath
)
$ErrorActionPreference = 'Stop'
function Get-Sha256File([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}
$root = Split-Path -Parent $PSScriptRoot
$apk = (Resolve-Path -LiteralPath $ApkPath).Path
if (-not (Test-Path -LiteralPath $PrivateKeyPath -PathType Leaf)) { throw 'Update signing key is missing.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts/android-app/release-$Version" }
$absoluteOutput = [IO.Path]::GetFullPath($OutputDirectory)
$final = $absoluteOutput.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$volumeRoot = [IO.Path]::GetPathRoot($absoluteOutput).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if ($final.Equals($volumeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output cannot be a filesystem root.' }
if (Test-Path -LiteralPath $final) { throw 'Output directory already exists; refusing to overwrite.' }
$parent = [IO.Path]::GetDirectoryName($final)
if (-not $parent) { throw 'Output must have a parent directory.' }
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$stage = Join-Path $parent ('.android-release-stage-' + [Guid]::NewGuid().ToString('N'))
$stage = [IO.Path]::GetFullPath($stage)
# Validate both recursive move/delete targets before creating anything in the stage.
if ([IO.Path]::GetDirectoryName($stage) -cne $parent -or [IO.Path]::GetDirectoryName($final) -cne $parent -or
    [IO.Path]::GetFileName($stage) -notmatch '^\.android-release-stage-[a-f0-9]{32}$') { throw 'Release staging containment check failed.' }
$signerProject = Join-Path $root 'tools/AndroidUpdateSigner/AndroidUpdateSigner.csproj'
& dotnet build $signerProject -c Release --disable-build-servers
if ($LASTEXITCODE -ne 0) { throw 'Android update signer build failed.' }
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    $name = "Inbound-android-$Version-arm64.apk"
    $releaseApk = Join-Path $stage $name
    Copy-Item -LiteralPath $apk -Destination $releaseApk
    # Strict verification (including the pinned rclone and 16 KiB alignment)
    # must succeed on the staged copy before update metadata is signed. Never
    # pass the historical-only verification option here.
    & python -I (Join-Path $root 'tools/AndroidApp/release-apk.py') verify $releaseApk --java-sdk $JavaSdkDirectory --android-sdk $AndroidSdkDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Staged APK verification failed.' }
    $tools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Directory |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'aapt2.exe')) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $tools) { throw 'Android aapt2 was not found.' }
    $badging = (& (Join-Path $tools.FullName 'aapt2.exe') dump badging $releaseApk) -join [Environment]::NewLine
    if ($LASTEXITCODE -ne 0) { throw 'APK version could not be read.' }
    $actualCode = [regex]::Match($badging, "versionCode='([0-9]+)'")
    $actualName = [regex]::Match($badging, "versionName='([^']+)'")
    if (-not $actualCode.Success -or -not $actualName.Success -or [long]$actualCode.Groups[1].Value -ne $VersionCode -or $actualName.Groups[1].Value -cne $Version) {
        throw 'Staged APK version does not match the requested release.'
    }
    $manifest = Join-Path $stage 'manifest-android-arm64.json'
    $signer = Join-Path $root 'tools/AndroidUpdateSigner/bin/Release/net10.0/AndroidUpdateSigner.dll'
    $url = "https://github.com/Kaan88/inbound/releases/download/android-v$Version/$name"
    $signerArguments = @($releaseApk, $VersionCode, $Version, $url, $PrivateKeyPath, $manifest)
    if ($ReleaseNotesPath) { $signerArguments += (Resolve-Path -LiteralPath $ReleaseNotesPath).Path }
    & dotnet $signer @signerArguments
    if ($LASTEXITCODE -ne 0) { throw 'Android release metadata signing failed.' }
    $hashes = @($releaseApk, $manifest) | ForEach-Object {
        $hash = Get-Sha256File $_
        "$hash  $([IO.Path]::GetFileName($_))"
    }
    [IO.File]::WriteAllLines((Join-Path $stage 'SHA256SUMS'), $hashes, [Text.UTF8Encoding]::new($false))
    [IO.Directory]::Move($stage, $final)
}
finally {
    if (Test-Path -LiteralPath $stage) {
        $resolvedStage = (Resolve-Path -LiteralPath $stage).Path
        if ($resolvedStage -cne $stage -or [IO.Path]::GetDirectoryName($resolvedStage) -cne $parent -or
            [IO.Path]::GetFileName($resolvedStage) -notmatch '^\.android-release-stage-[a-f0-9]{32}$') {
            throw 'Release staging cleanup containment check failed.'
        }
        Remove-Item -LiteralPath $resolvedStage -Recurse -Force
    }
}
Write-Host "Prepared $final (not published)."
Write-Host "Publish $name and the manifest on android-v$Version; publish the same manifest on android-latest. Use latest=false for both."
