[CmdletBinding()]
param(
    # The release tag whose assets will carry the XPI, for example v0.6.3. The update file points there.
    [Parameter(Mandatory = $true)][string]$ReleaseTag,
    # A JSON file with the addons.mozilla.org API key pair: { "issuer": "user:...", "secret": "..." }.
    # Kept outside the repository, like the update-signing key. Not needed with -SignedXpi.
    [string]$ApiKeysPath,
    # An XPI already signed by addons.mozilla.org (downloaded from the Developer Hub), used instead of signing.
    [string]$SignedXpi,
    [switch]$NoBuild,
    [string]$BuildsRepository = "Kaan88/inbound"
)

# EXT.S4: the Firefox add-on is self-distributed. addons.mozilla.org signs it as an unlisted version, Setup
# ships the signed XPI, the GitHub release carries it, and Firefox updates it from
# extensions/firefox-updates.json on main (the manifest's update_url). This script:
#   1. builds and checks the Firefox package (package-extension-firefox.ps1);
#   2. has addons.mozilla.org sign it (web-ext sign --channel unlisted), or takes -SignedXpi;
#   3. checks that the XPI is signed and holds exactly the files that were built;
#   4. writes artifacts\firefox-addon\inbound-bridge-<version>-firefox.xpi and extensions\firefox-updates.json.
# The API secret goes to web-ext through its environment only, never on a command line or in output.

$ErrorActionPreference = "Stop"
if ($ReleaseTag -notmatch '^v\d+(?:\.\d+){2,3}$') { throw "ReleaseTag must look like v1.2.3." }
if ($BuildsRepository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw "BuildsRepository must use owner/repository form." }
if (-not $ApiKeysPath -and -not $SignedXpi) { throw "Pass -ApiKeysPath (addons.mozilla.org API keys) or -SignedXpi." }

$root = Split-Path -Parent $PSScriptRoot
$extension = Join-Path $root "extensions\chrome"
$dist = Join-Path $extension "dist-firefox"
$outDir = Join-Path $root "artifacts\firefox-addon"
$geckoId = "inbound-bridge@kaan88"

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "package-extension-firefox.ps1")
    if (-not $?) { throw "The Firefox package failed." }
}
$manifest = [IO.File]::ReadAllText((Join-Path $dist "manifest.json")) | ConvertFrom-Json
$version = $manifest.version
$updateUrl = $manifest.browser_specific_settings.gecko.update_url
if (-not $updateUrl -or -not $updateUrl.StartsWith("https://")) { throw "The Firefox manifest has no https update_url." }
$xpiName = "inbound-bridge-$version-firefox.xpi"
$xpiPath = Join-Path $outDir $xpiName

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# The files web-ext packs: everything in the build except names starting with a dot (its default ignore, which
# also covers the .amo-upload-uuid it writes into the folder it signs).
function Get-PackedFiles([string]$folder) {
    $prefix = $folder.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $folder -Recurse -File -Force) {
        $name = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        if (@($name.Split('/') | Where-Object { $_.StartsWith('.') }).Count -eq 0) { [pscustomobject]@{ Name = $name; Path = $file.FullName } }
    }
}

# Signed by AMO, and holding exactly the built files: each one matches the build byte for byte and is listed with
# that digest in META-INF/manifest.mf, which the signature covers.
function Assert-SignedXpiMatchesBuild([string]$xpi) {
    $zip = [IO.Compression.ZipFile]::OpenRead($xpi)
    try {
        $entries = @($zip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
        $names = @($entries | ForEach-Object FullName)
        if ($names -notcontains 'META-INF/manifest.mf' -or ($names -notcontains 'META-INF/mozilla.rsa' -and $names -notcontains 'META-INF/cose.sig')) {
            throw "The XPI is not signed by addons.mozilla.org (no META-INF signature)."
        }
        $reader = [IO.StreamReader]::new($zip.GetEntry('META-INF/manifest.mf').Open())
        try { $mf = $reader.ReadToEnd() } finally { $reader.Dispose() }
        # JAR manifest: 72-byte lines, continued on lines that start with a space; one section per file.
        $mf = ($mf -replace "`r`n", "`n") -replace "`n ", ""
        $listed = @{}
        foreach ($section in ($mf -split "`n`n")) {
            $nameLine = [regex]::Match($section, '(?m)^Name: (.+)$')
            if (-not $nameLine.Success) { continue }
            $listed[$nameLine.Groups[1].Value.Trim()] = @{
                Sha256 = [regex]::Match($section, '(?m)^SHA256-Digest: (\S+)$').Groups[1].Value
                Sha1 = [regex]::Match($section, '(?m)^SHA1-Digest: (\S+)$').Groups[1].Value
            }
        }
        $content = @($entries | Where-Object { -not $_.FullName.StartsWith('META-INF/') })
        $built = @(Get-PackedFiles $dist)
        if ($content.Count -ne $built.Count) { throw "The XPI has $($content.Count) files; the build has $($built.Count)." }
        foreach ($file in $built) {
            $entry = $zip.GetEntry($file.Name)
            if (-not $entry) { throw "The XPI lacks '$($file.Name)'." }
            $stream = $entry.Open()
            try { $memory = [IO.MemoryStream]::new(); $stream.CopyTo($memory) } finally { $stream.Dispose() }
            $bytes = $memory.ToArray()
            $sha256 = [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash($bytes))
            $builtBytes = [IO.File]::ReadAllBytes($file.Path)
            if ($sha256 -ne [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash($builtBytes))) {
                # web-ext writes manifest.json again when it packs (it dropped the final newline in 0.5.3's signing),
                # so that one file is compared by its JSON content; every other file byte for byte.
                $same = $false
                if ($file.Name -eq 'manifest.json') {
                    $utf8 = [Text.UTF8Encoding]::new($false)
                    $same = (($utf8.GetString($bytes) | ConvertFrom-Json) | ConvertTo-Json -Depth 32 -Compress) -ceq
                        (($utf8.GetString($builtBytes) | ConvertFrom-Json) | ConvertTo-Json -Depth 32 -Compress)
                }
                if (-not $same) { throw "'$($file.Name)' in the XPI differs from the build." }
            }
            $digest = $listed[$file.Name]
            $sha1 = [Convert]::ToBase64String([Security.Cryptography.SHA1]::Create().ComputeHash($bytes))
            if (-not $digest -or -not (($digest.Sha256 -and $digest.Sha256 -eq $sha256) -or (-not $digest.Sha256 -and $digest.Sha1 -eq $sha1))) {
                throw "'$($file.Name)' is not covered by the XPI's signature manifest."
            }
        }
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $signedManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($signedManifest.version -ne $version) { throw "The XPI is version $($signedManifest.version), the build $version." }
        if ($signedManifest.browser_specific_settings.gecko.id -ne $geckoId) { throw "The XPI's gecko ID is not $geckoId." }
    }
    finally { $zip.Dispose() }
}

if ($SignedXpi) {
    $source = (Resolve-Path -LiteralPath $SignedXpi).Path
    Assert-SignedXpiMatchesBuild $source
    # Re-pointing the update file for a later release passes the XPI that is already in place.
    if (-not $source.Equals([IO.Path]::GetFullPath($xpiPath), [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $source -Destination $xpiPath -Force
    }
}
else {
    $webExt = Join-Path $extension "node_modules\web-ext\package.json"
    if (-not (Test-Path -LiteralPath $webExt) -or ([IO.File]::ReadAllText($webExt) | ConvertFrom-Json).version -ne "10.8.0") {
        throw "web-ext 10.8.0 is not installed. Run npm ci in extensions\chrome first."
    }
    $keys = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ApiKeysPath).Path) | ConvertFrom-Json
    if (-not $keys.issuer -or -not $keys.secret) { throw "The API key file needs 'issuer' and 'secret'." }

    # web-ext signs a copy, never the build itself: it writes .amo-upload-uuid into the folder it signs, which lets
    # a re-run of the same version resume (download what AMO signed) instead of uploading again. So the copy is kept
    # per version until signing succeeds.
    $stage = Join-Path $outDir ".sign-$version"
    $stageSource = Join-Path $stage "src"
    $stageOut = Join-Path $stage "out"
    New-Item -ItemType Directory -Force -Path $stageSource, $stageOut | Out-Null
    Get-ChildItem -LiteralPath $stageSource -Force | Where-Object { -not $_.Name.StartsWith('.') } | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $stageOut -Force | Remove-Item -Recurse -Force
    foreach ($file in Get-PackedFiles $dist) {
        $target = Join-Path $stageSource $file.Name
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Copy-Item -LiteralPath $file.Path -Destination $target
    }

    $env:WEB_EXT_API_KEY = $keys.issuer
    $env:WEB_EXT_API_SECRET = $keys.secret
    Push-Location $extension
    try {
        # Unlisted versions are signed automatically, usually within minutes.
        & npx --no-install web-ext sign --channel unlisted --source-dir $stageSource --artifacts-dir $stageOut --approval-timeout 900000
        $signExit = $LASTEXITCODE
    }
    finally {
        Pop-Location
        Remove-Item Env:WEB_EXT_API_KEY, Env:WEB_EXT_API_SECRET -ErrorAction SilentlyContinue
    }
    if ($signExit -ne 0) {
        throw ("web-ext sign failed (exit code $signExit). If AMO took the upload (a version number can be signed only once), " +
            "run this again: it resumes from $stage. Or download the signed file from the Developer Hub and pass -SignedXpi.")
    }
    $produced = @(Get-ChildItem -LiteralPath $stageOut -Filter *.xpi -File)
    if ($produced.Count -ne 1) { throw "Expected one signed XPI from web-ext in $stageOut, found $($produced.Count)." }
    # Kept beside the result until it is checked, so a failed check never loses a file AMO has signed.
    $unverified = Join-Path $outDir "inbound-bridge-$version-firefox.unverified.xpi"
    Move-Item -LiteralPath $produced[0].FullName -Destination $unverified -Force
    Assert-SignedXpiMatchesBuild $unverified
    Move-Item -LiteralPath $unverified -Destination $xpiPath -Force
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}

$hash = (Get-FileHash -LiteralPath $xpiPath -Algorithm SHA256).Hash.ToLowerInvariant()
$minVersion = $manifest.browser_specific_settings.gecko.strict_min_version
if ($version -notmatch '^\d+(?:\.\d+){1,3}$' -or $minVersion -notmatch '^\d+(?:\.\d+){0,3}$') { throw "Unexpected version format." }
$link = "https://github.com/$BuildsRepository/releases/download/$ReleaseTag/$xpiName"
# Firefox's update manifest (https://extensionworkshop.com/documentation/manage/updating-your-extension/).
# Every value is checked above, so the text needs no escaping. LF and no BOM: GitHub serves it as it is.
$updatesJson = @"
{
  "addons": {
    "$geckoId": {
      "updates": [
        {
          "version": "$version",
          "update_link": "$link",
          "update_hash": "sha256:$hash",
          "applications": { "gecko": { "strict_min_version": "$minVersion" } }
        }
      ]
    }
  }
}
"@
$null = $updatesJson | ConvertFrom-Json
$updatesPath = Join-Path $root "extensions\firefox-updates.json"
[IO.File]::WriteAllText($updatesPath, ($updatesJson -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))

Write-Host "Signed Firefox add-on: $xpiPath"
Write-Host "  $version, sha256 $hash"
Write-Host "Update file: $updatesPath (points at $ReleaseTag; publish the XPI with that release)"
