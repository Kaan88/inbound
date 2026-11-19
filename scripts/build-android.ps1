<#
.SYNOPSIS
  Builds Inbound for Android outside the default solution build.
.DESCRIPTION
  -Mode emulator: Debug/Mono/android-x64; -Mode phone: Release/NativeAOT/android-arm64.
  -TestBuild: API 30 and adb hooks, separate bin/obj; never a distributable release.
  -Rid: optional runtime override (e.g. android-x64 NativeAOT for the emulator).
  -ReleaseSigning: permanent-key phone APK in a fresh artifacts/android-app/signed-builds directory.
    Requires -JavaSdkDirectory and existing key/password files in -SigningDirectory.
    Only apksigner reads the password file after publishing; no secrets go to MSBuild.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("emulator", "phone")]
    [string]$Mode,
    [switch]$TestBuild,
    [ValidateSet("", "android-x64", "android-arm64")]
    [string]$Rid = "",
    [string]$AndroidSdkDirectory = (Join-Path $env:LOCALAPPDATA "Android\Sdk"),
    [string]$JavaSdkDirectory = "",
    [switch]$ReleaseSigning,
    [string]$SigningDirectory = (Join-Path $env:USERPROFILE "Downloads\secrets")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\DownloadManager.Android\DownloadManager.Android.csproj"
if ($Mode -eq "emulator") { $config = "Debug"; $runtime = "android-x64"; $verb = "build" }
else { $config = "Release"; $runtime = "android-arm64"; $verb = "publish" }
if ($Rid) { $runtime = $Rid }
$outConfig = if ($TestBuild) { "$config-test" } else { $config }
$msbuild = @("-c", $config, "-p:RuntimeIdentifier=$runtime", "-p:AndroidSdkDirectory=$AndroidSdkDirectory")
if ($TestBuild) { $msbuild += "-p:InboundTestBuild=true" }
if ($JavaSdkDirectory) { $msbuild += "-p:JavaSdkDirectory=$JavaSdkDirectory" }

if ($ReleaseSigning) {
    if ($Mode -ne "phone" -or $TestBuild -or $runtime -ne "android-arm64") {
        throw "ReleaseSigning requires -Mode phone, arm64, and no -TestBuild."
    }
    if (-not $JavaSdkDirectory -or -not (Test-Path -LiteralPath (Join-Path $JavaSdkDirectory "bin/java.exe") -PathType Leaf)) {
        throw "ReleaseSigning requires -JavaSdkDirectory with java.exe."
    }
    $keystore = Join-Path $SigningDirectory "inbound-android-release.jks"
    $passwordFile = Join-Path $SigningDirectory "inbound-android-release.pass"
    foreach ($file in @($keystore, $passwordFile)) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Release signing input missing: $file" }
    }
    # Never select a stale APK or inherit the test-build flag from MSBuild's environment.
    $signedOutput = Join-Path $root ("artifacts/android-app/signed-builds/" + [Guid]::NewGuid().ToString("N"))
    $publishDir = Join-Path $signedOutput "publish"
    New-Item -ItemType Directory -Path $publishDir | Out-Null
    $msbuild += @("-p:InboundTestBuild=false", "-p:AndroidKeyStore=false", "-p:PublishDir=$publishDir/")
}

Write-Host "dotnet $verb $project $($msbuild -join ' ')"
& dotnet $verb $project @msbuild
if ($LASTEXITCODE -ne 0) { throw "dotnet $verb failed ($LASTEXITCODE)" }

if ($ReleaseSigning) {
    $inputApk = Join-Path $publishDir "app.inbound.android-Signed.apk"
    if (-not (Test-Path -LiteralPath $inputApk -PathType Leaf)) { throw "No new build APK at $inputApk" }
    $apk = Join-Path $signedOutput "Inbound-android-release.apk"
    & python -I (Join-Path $root "tools/AndroidApp/release-apk.py") sign $apk --input $inputApk --keystore $keystore --password-file $passwordFile --java-sdk $JavaSdkDirectory --android-sdk $AndroidSdkDirectory
    if ($LASTEXITCODE -ne 0) { throw "Release APK signing/verification failed." }
}
else {
    $out = Join-Path $root "src/DownloadManager.Android/bin/$outConfig/net10.0-android/$runtime"
    if ($verb -eq "publish") { $out = Join-Path $out "publish" }
    $apk = Join-Path $out "app.inbound.android-Signed.apk"
    if (-not (Test-Path -LiteralPath $apk -PathType Leaf)) { throw "No signed APK at $apk" }
    if ($Mode -eq 'phone') {
        $checkArguments = @('-I', (Join-Path $root 'tools/AndroidApp/check-apk.py'), $apk)
        if (-not $TestBuild) { $checkArguments += '--release' }
        & python @checkArguments
        if ($LASTEXITCODE -ne 0) { throw 'NativeAOT APK verification failed; do not install or distribute this artifact.' }
    }
}
$apkFile = Get-Item -LiteralPath $apk
"{0}  ({1:N0} bytes, {2:N1} MB)" -f $apkFile.FullName, $apkFile.Length, ($apkFile.Length / 1MB)
