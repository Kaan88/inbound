[CmdletBinding()]
param(
    # JSON with client_id, client_secret, publisher_id and refresh_token (scripts\chrome-store-auth.ps1 fills the token).
    [Parameter(Mandatory = $true)][string]$KeysPath,
    # The store zip from scripts\package-extension-store.ps1. Defaults to this version's zip.
    [string]$ZipPath,
    [string]$ItemId = "olhbcdbjdnfoiinllnlpandggadfabfm",
    # Only report the item's current state; upload nothing.
    [switch]$StatusOnly,
    # Upload the draft but don't submit it for review.
    [switch]$NoSubmit,
    # After approval, keep it staged until it is published from the dashboard.
    [switch]$Staged
)
# Uploads the extension to the Chrome Web Store and submits it for review, with the Chrome Web Store
# API v2 (https://developer.chrome.com/docs/webstore/api/reference/rest). Edge users install from the
# same listing. The store listing (screenshots, texts) is not changed here. Tokens are never printed.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath $KeysPath)) { throw "Key file not found: $KeysPath" }
$keys = Get-Content -LiteralPath $KeysPath -Raw | ConvertFrom-Json
foreach ($field in "client_id", "client_secret", "refresh_token", "publisher_id") {
    if ([string]::IsNullOrWhiteSpace($keys.$field)) {
        if ($field -eq "refresh_token") { throw "No refresh token yet: run scripts\chrome-store-auth.ps1 -KeysPath `"$KeysPath`" once." }
        throw "The key file needs $field."
    }
}

$token = (Invoke-RestMethod -Method Post -Uri "https://oauth2.googleapis.com/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    client_id     = $keys.client_id
    client_secret = $keys.client_secret
    refresh_token = $keys.refresh_token
    grant_type    = "refresh_token"
}).access_token
$headers = @{ Authorization = "Bearer $token" }
$name = "publishers/$($keys.publisher_id)/items/$ItemId"
$api = "https://chromewebstore.googleapis.com"

function Get-ItemStatus {
    Invoke-RestMethod -Method Get -Uri "$api/v2/${name}:fetchStatus" -Headers $headers
}

function Write-ItemStatus($status) {
    foreach ($kind in "publishedItemRevisionStatus", "submittedItemRevisionStatus") {
        $revision = $status.$kind
        if ($null -eq $revision) { continue }
        $versions = ($revision.distributionChannels | ForEach-Object { "$($_.crxVersion) at $($_.deployPercentage)%" }) -join ", "
        Write-Host ("{0}: {1} {2}" -f $kind.Replace("ItemRevisionStatus", ""), $revision.state, $versions)
    }
    if ($status.lastAsyncUploadState) { Write-Host "Last upload: $($status.lastAsyncUploadState)" }
    if ($status.takenDown) { Write-Host "The item is taken down." }
    if ($status.warned) { Write-Host "The item has a warning in the dashboard." }
}

if ($StatusOnly) {
    Write-ItemStatus (Get-ItemStatus)
    return
}

$manifest = Get-Content -LiteralPath (Join-Path $root "extensions\chrome\manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.version
if (-not $ZipPath) { $ZipPath = Join-Path $root "artifacts\chrome-store\inbound-bridge-$version-chrome.zip" }
if (-not (Test-Path -LiteralPath $ZipPath)) { throw "Store zip not found: $ZipPath (run scripts\package-extension-store.ps1)." }

Write-Host "Uploading $([IO.Path]::GetFileName($ZipPath)) ($version)..."
$upload = Invoke-RestMethod -Method Post -Uri "$api/upload/v2/${name}:upload" -Headers $headers -InFile $ZipPath -ContentType "application/zip"
$state = $upload.uploadState
$waited = 0
while ($state -eq "IN_PROGRESS" -or $state -eq "UPLOAD_IN_PROGRESS") {
    if ($waited -ge 300) { throw "The upload is still being processed after 5 minutes; check with -StatusOnly later." }
    Start-Sleep -Seconds 5; $waited += 5
    $state = (Get-ItemStatus).lastAsyncUploadState
}
if ($state -ne "SUCCEEDED") { throw "The store did not accept the upload: $state" }
if ($upload.crxVersion -and $upload.crxVersion -ne $version) { throw "The store read version $($upload.crxVersion), expected $version." }
Write-Host "Uploaded as a draft."

if ($NoSubmit) { return }

$body = @{ publishType = $(if ($Staged) { "STAGED_PUBLISH" } else { "DEFAULT_PUBLISH" }) } | ConvertTo-Json
$published = Invoke-RestMethod -Method Post -Uri "$api/v2/${name}:publish" -Headers $headers -ContentType "application/json" -Body $body
Write-Host "Submitted for review: $($published.state)"
foreach ($warning in $published.warningInfo.warnings) { Write-Host "Warning: $($warning.reason) $($warning.description)" }
Write-ItemStatus (Get-ItemStatus)
