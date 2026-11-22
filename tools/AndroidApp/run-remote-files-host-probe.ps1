param(
    [Parameter(Mandatory)][string]$Serial,
    [Parameter(Mandatory)][string]$Apk,
    [Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$Adb = (Join-Path $env:LOCALAPPDATA 'Android/Sdk/platform-tools/adb.exe'),
    [string]$AndroidSdk = (Join-Path $env:LOCALAPPDATA 'Android/Sdk'),
    [ValidateRange(1024, 65535)][int]$Port = 47658
)

$ErrorActionPreference = 'Stop'
$package = 'app.inbound.android.rcloneprobe'
$activity = "$package/app.inbound.android.MainActivity"
$apkPath = (Resolve-Path -LiteralPath $Apk).Path
$tools = Get-ChildItem -LiteralPath (Join-Path $AndroidSdk 'build-tools') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' -and (Test-Path (Join-Path $_.FullName 'aapt.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $tools) { throw 'Android build-tools with aapt.exe are required.' }
$badging = & (Join-Path $tools.FullName 'aapt.exe') dump badging $apkPath
if ($LASTEXITCODE -or -not ($badging -match "^package: name='$([regex]::Escape($package))' ")) {
    throw "Refusing to install an APK other than $package."
}
$out = New-Item -ItemType Directory -Path $EvidenceDirectory
function Invoke-Adb {
    $result = & $Adb -s $Serial @args
    if ($LASTEXITCODE) { throw "ADB failed ($LASTEXITCODE): $($args -join ' ')" }
    $result
}

# Only the dedicated test emulator: never the owner's phone, which needs the display baseline first (review T1).
if ($Serial -notmatch '^emulator-\d+$') { throw 'Refusing a physical device; only the dedicated emulator is allowed.' }
if (((Invoke-Adb emu avd name) | Select-Object -First 1).Trim() -ne 'inbound33') { throw 'Refusing anything other than the dedicated inbound33 AVD.' }
if ((Invoke-Adb shell getprop ro.build.version.sdk).Trim() -ne '33') { throw 'Refusing anything other than API 33.' }
if (Invoke-Adb shell "pm list packages $package" | Where-Object { $_ -eq "package:$package" }) {
    throw 'The probe is already installed. This runner requires a fresh, isolated probe.'
}
if (Invoke-Adb reverse --list | Where-Object { $_ -match "\btcp:$Port\b" }) {
    throw "An ADB reverse already uses port $Port."
}
$fixture = $null
$reversed = $false
$installed = $false
try {
    $script = Join-Path $PSScriptRoot 'remote-files-fixture.py'
    $fixture = Start-Process python -ArgumentList @('-I', '-u', "`"$script`"", '--port', $Port, '--delay-get', '6') -PassThru `
        -RedirectStandardOutput (Join-Path $out.FullName 'fixture.stdout') `
        -RedirectStandardError (Join-Path $out.FullName 'fixture.log')
    Invoke-Adb shell 'getprop ro.product.cpu.abi; getprop ro.build.version.sdk; getconf PAGE_SIZE' |
        Tee-Object (Join-Path $out.FullName 'device.txt')
    Get-FileHash -LiteralPath $apkPath | Format-List | Out-File (Join-Path $out.FullName 'apk.txt')
    if ($fixture.HasExited) { throw 'The fixture could not start.' }
    Invoke-Adb reverse "tcp:$Port" "tcp:$Port"
    $reversed = $true
    Invoke-Adb install $apkPath
    $installed = $true
    Invoke-Adb shell am start -W -n $activity --ez skip_first_run true --ei remote_files_fixture_port $Port
    $deadline = [DateTime]::UtcNow.AddSeconds(65)
    do {
        $pids = Invoke-Adb shell "pidof $package || true"
        if ($pids) {
            $appPid = ($pids -split '\s+')[0]
            $probeLog = Invoke-Adb logcat -d "--pid=$appPid" -s InboundRemoteFilesProbe AndroidRuntime
            $probeLog | Set-Content -LiteralPath (Join-Path $out.FullName 'probe.log') -Encoding UTF8
            if ($probeLog -match 'PASS: bundled rclone via local IPC') { break }
            if ($probeLog -match ' E (InboundRemoteFilesProbe|AndroidRuntime)') { break }
        }
        Start-Sleep -Seconds 2
    } while ([DateTime]::UtcNow -lt $deadline)
    $probeLog
    if (-not ($probeLog -match 'PASS: bundled rclone via local IPC')) { throw 'Remote Files Host probe did not pass.' }
    if (-not (Select-String -LiteralPath (Join-Path $out.FullName 'fixture.log') -Pattern '"PROPFIND / HTTP/1.1" 207' -Quiet)) {
        throw 'The fixture did not record a successful PROPFIND.'
    }
    if (-not ($probeLog -match 'finished: 3 bytes sha256=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') -or
        -not (Select-String -LiteralPath (Join-Path $out.FullName 'fixture.log') -Pattern '"GET /inbound-remote-files\.txt HTTP/1.1" 200' -Quiet)) {
        throw 'The fixture GET and matching transfer hash were not both observed.'
    }
    Invoke-Adb shell 'if pidof librclone.so; then exit 1; fi; echo no-rclone-child'
}
finally {
    try {
        if ($installed) { Invoke-Adb uninstall $package }
    } finally {
        try { if ($reversed) { Invoke-Adb reverse --remove "tcp:$Port" } }
        finally {
            if ($fixture -and -not $fixture.HasExited) { Stop-Process -Id $fixture.Id }
        }
    }
}
