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
    # listed (the default): the version goes to the public listing on addons.mozilla.org, which holds it for review
    # and updates it for every user; the source zip below is attached because AMO asks for source for listed versions.
    # unlisted: the older self-distributed flow (AMO signs it, the GitHub release carries it, update_url names
    # extensions/firefox-updates.json).
    [ValidateSet("listed", "unlisted")][string]$Channel = "listed",
    # Rebuilds the XPI's files from the source zip (npm ci, npm run build:firefox in a scratch folder) and compares
    # every file byte for byte with the build. Slow; the source zip is made either way for the listed channel.
    [switch]$VerifySource,
    [string]$BuildsRepository = "Kaan88/inbound"
)

# The Firefox add-on is public on addons.mozilla.org (owner, 2026-11-17, EXT.S5), so the default channel is listed: the
# store build has no update_url and AMO updates it. Setup still ships the AMO-signed XPI, and extensions\firefox-updates.json
# moves installs of the older unlisted add-on (whose update_url names it) to the listed version once. This script:
#   1. builds and checks the Firefox package (package-extension-firefox.ps1; -Channel unlisted builds the update_url one);
#   2. listed: writes the source zip AMO asks for (it rebuilds the XPI's files byte for byte; -VerifySource proves it),
#      and submits the version (web-ext sign --channel listed). AMO reviews it and publishes it; web-ext cannot download
#      a listed version, so the next step is by hand: after approval, download the signed file from the Developer Hub
#      and run this again with -SignedXpi. unlisted: has AMO sign it (web-ext sign --channel unlisted), or takes -SignedXpi;
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
    if ($Channel -eq "unlisted") { & (Join-Path $PSScriptRoot "package-extension-firefox.ps1") -Unlisted }
    else { & (Join-Path $PSScriptRoot "package-extension-firefox.ps1") }
    if (-not $?) { throw "The Firefox package failed." }
}
$manifest = [IO.File]::ReadAllText((Join-Path $dist "manifest.json")) | ConvertFrom-Json
$version = $manifest.version
$updateUrl = $manifest.browser_specific_settings.gecko.update_url
if ($Channel -eq "unlisted" -and (-not $updateUrl -or -not $updateUrl.StartsWith("https://"))) { throw "The Firefox manifest has no https update_url." }
if ($Channel -eq "listed" -and $updateUrl) { throw "The listed Firefox manifest must have no update_url; build without -Unlisted." }
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
        $signedUpdateUrl = $signedManifest.browser_specific_settings.gecko.update_url
        if ($Channel -eq "listed" -and $signedUpdateUrl) { throw "The XPI names an update_url; a listed version has none." }
        if ($Channel -eq "unlisted" -and -not $signedUpdateUrl) { throw "The XPI has no update_url; an unlisted version needs one." }
    }
    finally { $zip.Dispose() }
}

# The source AMO asks for with a listed version: the extension's tracked files and the web UI files its build
# copies (the stylesheets and theme.js) or imports (src/lib: format, icons, types), in the same layout (the build reads ..\..\webui), with the steps. Sorted, fixed timestamps, so the same
# tree gives the same zip.
function New-SourceZip([string]$zipPath) {
    $extra = @("webui/src/tokens.css", "webui/src/ui-kit.css", "webui/public/theme.js",
        "webui/src/lib/format.ts", "webui/src/lib/icons.ts", "webui/src/lib/types.ts", "LICENSE")
    Push-Location $root
    try { $tracked = @(& git ls-files -- extensions/chrome) + $extra }
    finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw "git ls-files failed." }
    $names = @($tracked | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf } | Sort-Object -Unique -CaseSensitive)
    if ($names -notcontains "extensions/chrome/package-lock.json" -or $names -notcontains "webui/src/tokens.css") { throw "The source file list is incomplete." }
    $readme = @"
Inbound Download Manager: Firefox add-on, source

Needs Node.js 22 or later with npm. From the folder that holds this file:

    cd extensions/chrome
    npm ci
    npm run build:firefox

The add-on's files are then in extensions/chrome/dist-firefox (the XPI is these files, signed by Mozilla). The
build bundles src/*.ts with esbuild (pinned in package-lock.json), copies the HTML, CSS and icons, and writes
dist-firefox/manifest.json from manifest.json (scripts/build.mjs, function firefoxManifest). tokens.css, ui-kit.css,
theme.js and src/lib/{format,icons,types}.ts come from the webui folder, kept in this zip next to extensions/chrome because the build reads them there.
Nothing is downloaded at build time except the npm packages in package-lock.json.
"@
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    $stamp = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $names) {
            $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $in = [IO.File]::OpenRead((Join-Path $root $name)); $out = $entry.Open()
            try { $in.CopyTo($out) } finally { $out.Dispose(); $in.Dispose() }
        }
        $entry = $zip.CreateEntry("README-BUILD.txt", [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $stamp
        $out = $entry.Open()
        try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($readme -replace "`r`n", "`n")); $out.Write($bytes, 0, $bytes.Length) } finally { $out.Dispose() }
    }
    finally { $zip.Dispose() }
}

# Unpacks the source zip in a scratch folder, builds it there and compares every file with this build.
function Assert-SourceZipRebuilds([string]$zipPath) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ("inbound-firefox-source-" + [Guid]::NewGuid().ToString("N"))
    try {
        [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $scratch)
        Push-Location (Join-Path $scratch "extensions\chrome")
        try {
            & npm ci --no-audit --no-fund
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed in the source zip." }
            & npm run build:firefox
            if ($LASTEXITCODE -ne 0) { throw "The build from the source zip failed." }
        }
        finally { Pop-Location }
        $rebuilt = Join-Path $scratch "extensions\chrome\dist-firefox"
        $prefixA = $dist.TrimEnd('\') + '\'; $prefixB = $rebuilt.TrimEnd('\') + '\'
        $a = @(Get-ChildItem -LiteralPath $dist -Recurse -File | ForEach-Object { $_.FullName.Substring($prefixA.Length) } | Sort-Object)
        $b = @(Get-ChildItem -LiteralPath $rebuilt -Recurse -File | ForEach-Object { $_.FullName.Substring($prefixB.Length) } | Sort-Object)
        if (($a -join "|") -cne ($b -join "|")) { throw "The source zip builds a different set of files." }
        foreach ($name in $a) {
            if ((Get-FileHash -LiteralPath (Join-Path $dist $name) -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath (Join-Path $rebuilt $name) -Algorithm SHA256).Hash) {
                throw "'$name' built from the source zip differs from this build."
            }
        }
        Write-Host "The source zip rebuilds all $($a.Count) files byte for byte."
    }
    finally { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
}

$sourceZip = Join-Path $outDir "inbound-bridge-$version-firefox-source.zip"
if ($Channel -eq "listed") {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
    New-SourceZip $sourceZip
    Write-Host "Source zip for AMO: $sourceZip"
    if ($VerifySource) { Assert-SourceZipRebuilds $sourceZip }
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
    $webExtArgs = @("--channel", $Channel)
    if ($Channel -eq "listed") { $webExtArgs += @("--upload-source-code", $sourceZip) }

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
        # Unlisted versions are signed automatically, usually within minutes, and web-ext waits up to 15 minutes for the
        # file. A listed one is submitted for review: an approval timeout of 0 makes web-ext return after the upload and
        # the source code (with any other value it polls until approval and fails when the time runs out).
        $approvalTimeout = if ($Channel -eq "listed") { "0" } else { "900000" }
        & npx --no-install web-ext sign @webExtArgs --source-dir $stageSource --artifacts-dir $stageOut --approval-timeout $approvalTimeout
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
    if ($Channel -eq "listed") {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Submitted $version to the AMO listing. When AMO has approved it, download the signed file from the Developer Hub and run:"
        Write-Host "  scripts\sign-extension-firefox.ps1 -ReleaseTag $ReleaseTag -SignedXpi <the downloaded .xpi>"
        exit 0
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
