# Saves one PNG per screen of the agreed Sites mockup (SITES.11 R0) at 1360 x 800, dark mode by default.
# Usage: powershell -ExecutionPolicy Bypass -File scripts\shoot-sites-mockups.ps1 [-Light]
param([switch]$Light)
# Chrome writes harmless errors to stderr; 'Stop' would turn them into failures.
$ErrorActionPreference = 'Continue'
$root = Split-Path $PSScriptRoot -Parent
$mock = Join-Path $root 'docs\plans\sites-mockups\4-agreed-design.html'
$out = Join-Path $root 'docs\plans\sites-mockups\compare'
New-Item -ItemType Directory -Force $out | Out-Null
$chrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
$html = Get-Content $mock -Raw -Encoding UTF8
# Screens are S.x= / S['x']= lines; one key can be assigned twice (S.phone), so keep each once.
$keys = [regex]::Matches($html, "(?m)^S(?:\.([A-Za-z0-9_]+)|\['([A-Za-z0-9_-]+)'\])=") |
    ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } } |
    Select-Object -Unique
$scheme = if ($Light) { 'light' } else { 'dark' }
$suffix = if ($Light) { '-light' } else { '' }

# The copy shows only the window, filling the viewport, and fixes the mockup's own theme switch (which otherwise
# follows the OS or a value remembered in localStorage).
$themeLine = "try{tm=localStorage.getItem('inb-theme')||'system'}catch(e){}"
if (-not $html.Contains($themeLine)) { throw "The mockup's theme line changed; update this script." }
if (([regex]::Matches($html, '</body>')).Count -ne 1) { throw "Expected exactly one </body> in the mockup." }
$only = '<style>header.top,#rail,#notes{display:none!important}.wrap,.layout,.stage{margin:0!important;padding:0!important;max-width:none!important;display:block!important;border:0!important;background:none!important}body{padding:0!important}.stage{overflow:visible!important}#win{width:1360px!important;height:800px!important}#win>.win{height:800px;border-radius:0}</style></body>'
$copyHtml = $html.Replace($themeLine, "tm='$scheme';").Replace('</body>', $only)
$copy = Join-Path $env:TEMP 'sites-mock-only.html'
Set-Content -Encoding utf8 $copy $copyHtml
$uri = ([System.Uri]$copy).AbsoluteUri

# A fresh profile each run: a Chrome left running on a shared profile would take the request and exit.
$profile = Join-Path $env:TEMP "sites-mock-profile-$PID"
try {
    foreach ($k in $keys) {
        $png = Join-Path $out "$k-mockup$suffix.png"
        Remove-Item -LiteralPath $png -ErrorAction SilentlyContinue
        & $chrome --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$profile" `
            --window-size=1360,800 --screenshot="$png" "$uri#$k" 2>$null | Out-Null
        if (-not (Test-Path $png)) { throw "No screenshot for $k" }
        Write-Output "$k -> $png"
    }
} finally {
    Remove-Item -LiteralPath $profile -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "$($keys.Count) screens ($scheme)."
