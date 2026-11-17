[CmdletBinding()]
param(
    [switch]$NoBuild
)

# Builds the zip to upload to addons.mozilla.org (AMO) for signing (EXT.E1). Firefox runs only signed
# add-ons in its release builds, so this zip is not installable as it is: upload it as an unlisted
# version from the owner's AMO account and install the signed .xpi AMO returns. The manifest comes from
# build.mjs --target firefox (gecko ID, event page, no Chrome key); this checks it before zipping.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$extension = Join-Path $root "extensions\chrome"
$dist = Join-Path $extension "dist-firefox"
$outDir = Join-Path $root "artifacts\firefox-addon"
$geckoId = "inbound-bridge@kaan88"

if (-not $NoBuild) {
    Push-Location $extension
    try {
        & npm run build:firefox
        if ($LASTEXITCODE -ne 0) { throw "The Firefox build failed." }
    }
    finally { Pop-Location }
}

$manifest = [IO.File]::ReadAllText((Join-Path $dist "manifest.json")) | ConvertFrom-Json
$package = [IO.File]::ReadAllText((Join-Path $extension "package.json")) | ConvertFrom-Json

$problems = [Collections.Generic.List[string]]::new()
if ($manifest.manifest_version -ne 3) { $problems.Add("manifest_version is not 3.") }
if ($manifest.version -ne $package.version) { $problems.Add("manifest version $($manifest.version) differs from package.json $($package.version).") }
if ($manifest.browser_specific_settings.gecko.id -ne $geckoId) { $problems.Add("The gecko ID is not $geckoId.") }
if ($manifest.PSObject.Properties.Name -contains 'key') { $problems.Add("The manifest still has Chrome's key.") }
if ($manifest.background.PSObject.Properties.Name -contains 'service_worker') { $problems.Add("The background is a service worker; Firefox needs background.scripts.") }
foreach ($file in @($manifest.background.scripts) + @($manifest.action.default_popup, $manifest.options_ui.page) +
        @($manifest.icons.PSObject.Properties.Value) + @($manifest.content_scripts | ForEach-Object { $_.js })) {
    if (-not (Test-Path -LiteralPath (Join-Path $dist $file))) { $problems.Add("The manifest names '$file', which is not in the build.") }
}

$files = @(Get-ChildItem -LiteralPath $dist -Recurse -File)
$distPrefix = $dist.TrimEnd('\') + '\'
foreach ($file in $files) {
    if ($file.Extension -in '.map', '.pem', '.ts', '.crx', '.xpi' -or $file.Name -like '.*') { $problems.Add("'$($file.Name)' must not be in the package.") }
}
if ($problems.Count -gt 0) { throw ("The package is not ready:`n  " + ($problems -join "`n  ")) }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir "inbound-bridge-$($manifest.version)-firefox.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# Entry by entry, with forward slashes, which AMO requires and Compress-Archive on Windows
# PowerShell 5.1 does not guarantee.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $name = $file.FullName.Substring($distPrefix.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

$written = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($written.Entries | ForEach-Object FullName)
    if ($names -notcontains 'manifest.json') { throw "manifest.json is not at the root of the zip." }
    if (@($names | Where-Object { $_.Contains('\') }).Count -gt 0) { throw "The zip has backslashes in its entry names." }
}
finally { $written.Dispose() }

Write-Host "Firefox package (unsigned, for AMO): $zipPath"
Write-Host "  $($manifest.name) $($manifest.version), $($names.Count) files, $([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB"
