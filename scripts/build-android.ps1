<#
.SYNOPSIS
  Builds the Inbound phone app (src/DownloadManager.Android). It is not part of DownloadManager.slnx.
.DESCRIPTION
  -Mode emulator : Debug, default (Mono) runtime, android-x64 (dotnet build). For the emulator and day-to-day work.
  -Mode phone    : Release, NativeAOT, android-arm64 (dotnet publish). Slow.
  -TestBuild     : passes -p:InboundTestBuild=true: minimum Android 11 (API 30) and the adb test hooks. Never for a release.
                   Its output goes to bin\<config>-test\, apart from release APKs.
  -Rid           : overrides the runtime (android-x64 for a NativeAOT build on the emulator).
  Prints the APK path and size.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet("emulator", "phone")]
    [string]$Mode,
    [switch]$TestBuild,
    [ValidateSet("", "android-x64", "android-arm64")]
    [string]$Rid = "",
    [string]$AndroidSdkDirectory = (Join-Path $env:LOCALAPPDATA "Android\Sdk")
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\DownloadManager.Android\DownloadManager.Android.csproj"

# PowerShell names are case-insensitive: $Rid (the parameter) and a local $rid would be one variable.
if ($Mode -eq "emulator") { $config = "Debug"; $runtime = "android-x64"; $verb = "build" }
else { $config = "Release"; $runtime = "android-arm64"; $verb = "publish" }
if ($Rid) { $runtime = $Rid }
$outConfig = if ($TestBuild) { "$config-test" } else { $config }

$msbuild = @("-c", $config, "-p:RuntimeIdentifier=$runtime", "-p:AndroidSdkDirectory=$AndroidSdkDirectory")
if ($TestBuild) { $msbuild += "-p:InboundTestBuild=true" }

Write-Host "dotnet $verb $project $($msbuild -join ' ')"
& dotnet $verb $project @msbuild
if ($LASTEXITCODE -ne 0) { throw "dotnet $verb failed ($LASTEXITCODE)" }

# The newest APK of this configuration and runtime (an up-to-date build does not rewrite it). A publish writes it under publish\.
$out = Join-Path $root "src\DownloadManager.Android\bin\$outConfig\net10.0-android\$runtime"
$apk = Get-ChildItem -Path $out -Recurse -Filter "app.inbound.android-Signed.apk" |
    Where-Object { ($verb -eq "publish") -eq ($_.DirectoryName -like "*\publish") } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) { throw "No signed APK under $out" }
"{0}  ({1:N0} bytes, {2:N1} MB)" -f $apk.FullName, $apk.Length, ($apk.Length / 1MB)
