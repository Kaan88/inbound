[CmdletBinding()]
param(
    # JSON with client_id, client_secret, publisher_id and refresh_token, kept outside the repository.
    [Parameter(Mandatory = $true)][string]$KeysPath,
    # The loopback port Google sends the sign-in back to. A "Web application" OAuth client needs
    # http://127.0.0.1:<port>/ in its Authorized redirect URIs; a "Desktop app" client needs nothing.
    [int]$Port = 53682
)
# One-time sign-in for scripts\publish-extension-chrome.ps1. It opens Google's consent page in the
# default browser for the Chrome Web Store scope, receives the answer on 127.0.0.1, and saves the
# refresh token into the key file. Nothing secret is printed. Run it again only if the token stops
# working (for example after revoking the app's access in the Google account).
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Web

if (-not (Test-Path -LiteralPath $KeysPath)) { throw "Key file not found: $KeysPath" }
$keys = Get-Content -LiteralPath $KeysPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($keys.client_id) -or [string]::IsNullOrWhiteSpace($keys.client_secret)) {
    throw "The key file needs client_id and client_secret."
}

function New-Base64Url([byte[]]$bytes) {
    [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$verifierBytes = New-Object byte[] 48; $rng.GetBytes($verifierBytes)
$verifier = New-Base64Url $verifierBytes
$sha = [System.Security.Cryptography.SHA256]::Create()
$challenge = New-Base64Url ($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)))
$stateBytes = New-Object byte[] 16; $rng.GetBytes($stateBytes)
$state = New-Base64Url $stateBytes

$redirect = "http://127.0.0.1:$Port/"
$query = @{
    client_id             = $keys.client_id
    redirect_uri          = $redirect
    response_type         = "code"
    scope                 = "https://www.googleapis.com/auth/chromewebstore"
    access_type           = "offline"
    prompt                = "consent"
    code_challenge        = $challenge
    code_challenge_method = "S256"
    state                 = $state
}
$pairs = foreach ($k in $query.Keys) { "$k=" + [System.Web.HttpUtility]::UrlEncode([string]$query[$k]) }
$authUrl = "https://accounts.google.com/o/oauth2/v2/auth?" + ($pairs -join "&")

$listener = New-Object System.Net.HttpListener
$listener.Prefixes.Add($redirect)
$listener.Start()
try {
    Write-Host "Opening Google's sign-in page. Sign in with the account that owns the Chrome Web Store item and allow access."
    Start-Process $authUrl
    $task = $listener.GetContextAsync()
    if (-not $task.Wait([TimeSpan]::FromMinutes(5))) { throw "No answer from the sign-in page within 5 minutes." }
    $context = $task.Result
    $params = $context.Request.QueryString
    $html = "<html><body style='font-family:Segoe UI'><h3>You can close this tab.</h3></body></html>"
    if ($params["error"]) { $html = "<html><body style='font-family:Segoe UI'><h3>Sign-in was not completed.</h3></body></html>" }
    $buffer = [Text.Encoding]::UTF8.GetBytes($html)
    $context.Response.ContentType = "text/html; charset=utf-8"
    $context.Response.OutputStream.Write($buffer, 0, $buffer.Length)
    $context.Response.Close()
}
finally {
    $listener.Stop()
}

if ($params["error"]) { throw "Google answered: $($params['error'])" }
if ($params["state"] -ne $state) { throw "The answer did not match this sign-in (state mismatch)." }
$code = $params["code"]
if ([string]::IsNullOrWhiteSpace($code)) { throw "Google sent no authorization code." }

$token = Invoke-RestMethod -Method Post -Uri "https://oauth2.googleapis.com/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    code          = $code
    client_id     = $keys.client_id
    client_secret = $keys.client_secret
    redirect_uri  = $redirect
    grant_type    = "authorization_code"
    code_verifier = $verifier
}
if ([string]::IsNullOrWhiteSpace($token.refresh_token)) {
    throw "Google returned no refresh token. Remove the app's access in your Google account (Security, Third-party access) and run this again."
}

$keys | Add-Member -NotePropertyName refresh_token -NotePropertyValue $token.refresh_token -Force
$temp = "$KeysPath.tmp"
$keys | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
Move-Item -LiteralPath $temp -Destination $KeysPath -Force
Write-Host "Saved the refresh token to $KeysPath."
if ([string]::IsNullOrWhiteSpace($keys.publisher_id)) {
    Write-Host "Still needed: publisher_id. Find it in the Developer Dashboard under Publisher, Settings, and add it to the key file."
}
