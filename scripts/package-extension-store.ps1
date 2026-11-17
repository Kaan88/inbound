[CmdletBinding()]
param(
    [switch]$NoBuild
)

# Builds the zip to upload to the Chrome Web Store. It differs from the unpacked build in one way:
# the manifest's "key" is removed, because the store refuses a first upload that has one and assigns
# the extension its own ID. Everything else is checked here, so a bad package fails now rather than
# at the dashboard.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$extension = Join-Path $root "extensions\chrome"
$dist = Join-Path $extension "dist"
$outDir = Join-Path $root "artifacts\chrome-store"

if (-not $NoBuild) {
    Push-Location $extension
    try {
        & npm run build
        if ($LASTEXITCODE -ne 0) { throw "The extension build failed." }
    }
    finally { Pop-Location }
}

$manifestText = [IO.File]::ReadAllText((Join-Path $dist "manifest.json"))
$manifest = $manifestText | ConvertFrom-Json
$package = [IO.File]::ReadAllText((Join-Path $extension "package.json")) | ConvertFrom-Json

# Removed as text, so the rest of the file stays exactly as it was built.
$stripped = [regex]::Replace($manifestText, '(?m)^[ \t]*"key"[ \t]*:[ \t]*"[^"]*",[ \t]*\r?\n', '')
$check = $stripped | ConvertFrom-Json
if ($check.PSObject.Properties.Name -contains 'key') { throw "The manifest key could not be removed." }

$problems = [Collections.Generic.List[string]]::new()
if ($manifest.manifest_version -ne 3) { $problems.Add("manifest_version is not 3.") }
if ($manifest.version -ne $package.version) { $problems.Add("manifest version $($manifest.version) differs from package.json $($package.version).") }
if ($manifest.name.Length -gt 75) { $problems.Add("The name is over the store's 75 characters.") }
if ($manifest.description.Length -gt 132) { $problems.Add("The description is $($manifest.description.Length) characters; the store allows 132.") }

$referenced = @(
    $manifest.background.service_worker
    $manifest.action.default_popup
    $manifest.options_ui.page
    $manifest.icons.PSObject.Properties.Value
    $manifest.action.default_icon.PSObject.Properties.Value
    $manifest.content_scripts | ForEach-Object { $_.js }
)
foreach ($file in $referenced) {
    if (-not (Test-Path -LiteralPath (Join-Path $dist $file))) { $problems.Add("The manifest names '$file', which is not in the build.") }
}

Add-Type -AssemblyName System.Drawing
$icon = [Drawing.Image]::FromFile((Join-Path $dist "icons\icon128.png"))
try {
    if ($icon.Width -ne 128 -or $icon.Height -ne 128) { $problems.Add("icons/icon128.png is $($icon.Width)x$($icon.Height), not 128x128.") }
}
finally { $icon.Dispose() }

$files = @(Get-ChildItem -LiteralPath $dist -Recurse -File)
$distPrefix = $dist.TrimEnd('\') + '\'
foreach ($file in $files) {
    if ($file.Extension -in '.map', '.pem', '.ts', '.crx' -or $file.Name -like '.*') { $problems.Add("'$($file.Name)' must not be in a store package.") }
}
if ($problems.Count -gt 0) { throw ("The package is not ready:`n  " + ($problems -join "`n  ")) }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$zipPath = Join-Path $outDir "inbound-bridge-$($manifest.version)-chrome.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }

# Written entry by entry: names must use forward slashes, which Compress-Archive on Windows
# PowerShell 5.1 does not guarantee.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files) {
        $name = $file.FullName.Substring($distPrefix.Length).Replace('\', '/')
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
        $stream = $entry.Open()
        try {
            $bytes = if ($name -eq 'manifest.json') { (New-Object System.Text.UTF8Encoding($false)).GetBytes($stripped) }
                     else { [IO.File]::ReadAllBytes($file.FullName) }
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
    }
}
finally { $zip.Dispose() }

# Read back what was written, rather than trusting it.
$written = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($written.Entries | ForEach-Object FullName)
    if ($names -notcontains 'manifest.json') { throw "manifest.json is not at the root of the zip." }
    if (@($names | Where-Object { $_.Contains('\') }).Count -gt 0) { throw "The zip has backslashes in its entry names." }
    $reader = New-Object IO.StreamReader ($written.GetEntry('manifest.json').Open())
    try { $inZip = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($inZip -match '"key"\s*:') { throw "The manifest in the zip still has a key." }
}
finally { $written.Dispose() }

Write-Host "Store package: $zipPath"
Write-Host "  $($manifest.name) $($manifest.version), $($names.Count) files, $([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB"
