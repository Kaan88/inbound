param(
    [Parameter(Mandatory)][string]$SourceDirectory,
    [Parameter(Mandatory)][string]$Go,
    [Parameter(Mandatory)][string]$NdkDirectory,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = 'Stop'
$commit = 'e982ff36668f5130d94b4f70409babb3d964df42' # rclone v1.76.0
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
$Go = (Resolve-Path -LiteralPath $Go).Path
$NdkDirectory = (Resolve-Path -LiteralPath $NdkDirectory).Path
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Choose a new output path.' }
if ((& git -C $SourceDirectory rev-parse HEAD) -ne $commit -or $LASTEXITCODE) {
    throw "Expected rclone source at $commit."
}
if ((& git -C $SourceDirectory status --porcelain --untracked-files=all) -or $LASTEXITCODE) {
    throw 'Use a clean rclone source checkout.'
}
if (-not (Select-String -LiteralPath (Join-Path $NdkDirectory 'source.properties') -Pattern '^Pkg\.Revision\s*=\s*28\.2\.13676358\s*$' -Quiet)) {
    throw 'Expected Android NDK 28.2.13676358.'
}
$bin = Join-Path $NdkDirectory 'toolchains/llvm/prebuilt/windows-x86_64/bin'
$cc = Join-Path $bin 'clang.exe'
$readelf = Join-Path $bin 'llvm-readelf.exe'
$variables = @{
    GOENV = 'off'; GOTOOLCHAIN = 'local'; GOFLAGS = ''; GOWORK = 'off'
    GOOS = 'android'; GOARCH = 'arm64'; GOARM64 = 'v8.0'; CGO_ENABLED = '1'
    CC = ('"' + ($cc -replace '\\', '/') + '" --target=aarch64-linux-android31')
    CGO_CFLAGS = '-O2 -g'; CGO_CPPFLAGS = ''; CGO_CXXFLAGS = '-O2 -g'; CGO_LDFLAGS = '-O2 -g'
}
$saved = @{}
try {
    foreach ($key in $variables.Keys) {
        $saved[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $variables[$key], 'Process')
    }
    if ((& $Go version) -ne 'go version go1.27.3 windows/amd64' -or $LASTEXITCODE) {
        throw 'Expected Go 1.27.3 for Windows amd64.'
    }
    Push-Location -LiteralPath $SourceDirectory
    try {
        & $Go build -mod=readonly -trimpath -buildvcs=false -buildmode=pie -tags noselfupdate `
            '-ldflags=-s -w -linkmode=external -extldflags=-Wl,-z,max-page-size=16384' -o $Output .
        if ($LASTEXITCODE) { throw "rclone build failed ($LASTEXITCODE)." }
    } finally { Pop-Location }
    $elf = & $readelf -h -lW $Output
    if ($LASTEXITCODE -or -not ($elf -match 'Machine:\s+AArch64') -or
        -not ($elf -match 'interpreter: /system/bin/linker64')) {
        throw 'Output is not an Android AArch64 executable.'
    }
    $loads = @($elf -match '^\s+LOAD\s')
    if (!$loads.Count) { throw 'No ELF LOAD segments.' }
    foreach ($load in $loads) {
        $fields = $load.Trim() -split '\s+'
        $align = [Convert]::ToInt64($fields[-1].Substring(2), 16)
        $offset = [Convert]::ToInt64($fields[1].Substring(2), 16)
        $address = [Convert]::ToInt64($fields[2].Substring(2), 16)
        if ($align -lt 16384 -or ($address - $offset) % 16384 -ne 0) {
            throw "ELF LOAD segment does not support 16 KiB pages: $load"
        }
    }
    $loads
    Get-Item -LiteralPath $Output | Select-Object -ExpandProperty Length
    Get-FileHash -LiteralPath $Output -Algorithm SHA256
} finally {
    foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key, $saved[$key], 'Process') }
}
