[CmdletBinding()]
param(
    # JSON with client_id, api_key and product_id (Partner Center > Microsoft Edge > Publish API).
    [Parameter(Mandatory = $true)][string]$KeysPath,
    # The store zip from scripts\package-extension-store.ps1. Defaults to this version's zip.
    [string]$ZipPath,
    # Certification notes for the reviewers.
    [string]$Notes = "Inbound Bridge hands browser downloads to the Inbound Download Manager desktop app through Native Messaging.",
    # Upload the draft package but don't submit it for review.
    [switch]$NoSubmit,
    # Report the state of an operation instead of uploading. Give -OperationId (printed by an earlier run);
    # the API has no call that lists the latest submission, so without an id this only explains that.
    [switch]$StatusOnly,
    # With -StatusOnly: the operation id, and -Kind says which kind it is.
    [string]$OperationId,
    [ValidateSet("Package", "Submission")][string]$Kind = "Submission"
)
# Uploads the extension to Microsoft Edge Add-ons and submits it for review, with the Edge Add-ons API v1.1
# (https://learn.microsoft.com/microsoft-edge/extensions/publish/api/using-addons-api). It updates the existing
# product only; the store listing (screenshots, texts) is not changed here. The API key is never printed.
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not (Test-Path -LiteralPath $KeysPath)) { throw "Key file not found: $KeysPath" }
$keys = Get-Content -LiteralPath $KeysPath -Raw | ConvertFrom-Json
foreach ($field in "client_id", "api_key", "product_id") {
    if ([string]::IsNullOrWhiteSpace($keys.$field)) { throw "The key file needs $field." }
}
$headers = @{ Authorization = "ApiKey $($keys.api_key)"; "X-ClientID" = $keys.client_id }
$api = "https://api.addons.microsoftedge.microsoft.com/v1/products/$($keys.product_id)/submissions"

# Failures from the API carry a JSON body with a message; show it, never the headers.
function Invoke-EdgeApi([string]$Method, [string]$Uri, $Body, [string]$ContentType, [string]$InFile) {
    $args2 = @{ Method = $Method; Uri = $Uri; Headers = $headers; UseBasicParsing = $true }
    if ($ContentType) { $args2.ContentType = $ContentType }
    if ($InFile) { $args2.InFile = $InFile }
    elseif ($null -ne $Body) { $args2.Body = $Body }
    try { return Invoke-WebRequest @args2 }
    catch {
        $detail = ""
        $response = $_.Exception.Response
        if ($null -ne $response) {
            $detail = "HTTP $([int]$response.StatusCode)"
            try {
                $text = if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $_.ErrorDetails.Message } else { (New-Object IO.StreamReader($response.GetResponseStream())).ReadToEnd() }
                if ($text) { $detail += ": $text" }
            } catch { }
        }
        else { $detail = $_.Exception.Message }
        throw "Edge Add-ons API $Method failed. $detail"
    }
}

function Get-Header($response, [string]$name) {
    $value = $response.Headers[$name]
    if ($value -is [array]) { $value = $value[0] }
    return $value
}

# Polls an operation until it is Succeeded or Failed. $Path is "draft/package/operations" or "operations".
function Wait-Operation([string]$Path, [string]$Id, [string]$What, [int]$TimeoutSeconds = 600) {
    $waited = 0
    while ($true) {
        $r = (Invoke-EdgeApi "GET" "$api/$Path/$Id").Content | ConvertFrom-Json
        $status = $r.status
        if ($status -eq "Succeeded") { Write-Host ("${What}: Succeeded. $($r.message)").TrimEnd(); return $r }
        if ($status -eq "Failed") {
            $lines = @("${What}: Failed. $($r.message)")
            if ($r.errorCode) { $lines += "Error code: $($r.errorCode)" }
            foreach ($e in @($r.errors)) { if ($e) { $lines += "  $($e.message)" } }
            throw ($lines -join [Environment]::NewLine)
        }
        if ($waited -ge $TimeoutSeconds) { throw "$What is still '$status' after $TimeoutSeconds seconds; check it later with -StatusOnly -Kind $(if ($Path -like '*package*') { 'Package' } else { 'Submission' }) -OperationId $Id." }
        Start-Sleep -Seconds 5; $waited += 5
    }
}

if ($StatusOnly) {
    if (-not $OperationId) {
        Write-Host "The Edge Add-ons API reports only a named operation, not the product's latest submission."
        Write-Host "Pass -OperationId <id> (the id a run printed) with -Kind Package or Submission, or look at Partner Center."
        return
    }
    $path = if ($Kind -eq "Package") { "draft/package/operations" } else { "operations" }
    $r = (Invoke-EdgeApi "GET" "$api/$path/$OperationId").Content | ConvertFrom-Json
    Write-Host ("${Kind} operation ${OperationId}: $($r.status) $($r.message)").TrimEnd()
    if ($r.errorCode) { Write-Host "Error code: $($r.errorCode)" }
    foreach ($e in @($r.errors)) { if ($e) { Write-Host "  $($e.message)" } }
    return
}

$manifest = Get-Content -LiteralPath (Join-Path $root "extensions\chrome\manifest.json") -Raw | ConvertFrom-Json
$version = $manifest.version
if (-not $ZipPath) { $ZipPath = Join-Path $root "artifacts\chrome-store\inbound-bridge-$version-chrome.zip" }
if (-not (Test-Path -LiteralPath $ZipPath)) { throw "Store zip not found: $ZipPath (run scripts\package-extension-store.ps1)." }

Write-Host "Uploading $([IO.Path]::GetFileName($ZipPath)) ($version)..."
$upload = Invoke-EdgeApi "POST" "$api/draft/package" $null "application/zip" $ZipPath
$packageOp = Get-Header $upload "Location"
if (-not $packageOp) { throw "The upload was answered without an operation id (HTTP $([int]$upload.StatusCode))." }
$packageOp = $packageOp.TrimEnd('/').Split('/')[-1]
Write-Host "Package operation: $packageOp"
Wait-Operation "draft/package/operations" $packageOp "Upload" | Out-Null
Write-Host "Uploaded as a draft."

if ($NoSubmit) { return }

$body = @{ notes = $Notes } | ConvertTo-Json
$submit = Invoke-EdgeApi "POST" $api $body "application/json"
$submitOp = Get-Header $submit "Location"
if (-not $submitOp) { throw "The submission was answered without an operation id (HTTP $([int]$submit.StatusCode))." }
$submitOp = $submitOp.TrimEnd('/').Split('/')[-1]
Write-Host "Submission operation: $submitOp"
Wait-Operation "operations" $submitOp "Submission" | Out-Null
Write-Host "Submitted for review. Edge publishes it after certification."
