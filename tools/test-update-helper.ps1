[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$testRoot = [IO.Path]::GetFullPath((Join-Path $artifactsRoot "update-helper-tests-$([guid]::NewGuid().ToString('N'))"))
$artifactsPrefix = $artifactsRoot.TrimEnd('\') + '\'
if (-not $testRoot.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Test root escaped the artifacts directory: $testRoot"
}
if (Test-Path -LiteralPath $testRoot) {
    throw "Unique test root unexpectedly already exists: $testRoot"
}

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$helper = Join-Path (Split-Path -Parent $PSScriptRoot) "scripts\apply-update.ps1"
$powershellExe = Join-Path $env:WINDIR "System32\WindowsPowerShell\v1.0\powershell.exe"
if (-not (Test-Path -LiteralPath $helper)) { throw "Updater helper not found: $helper" }
if (-not (Test-Path -LiteralPath $powershellExe)) { throw "Windows PowerShell not found: $powershellExe" }

function Assert-That([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}

function Write-Text([string]$path, [string]$value) {
    $parent = Split-Path -Parent $path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText($path, $value, [Text.UTF8Encoding]::new($false))
}

function Invoke-UpdateCase([string]$packageDir, [string]$installRoot, [string]$version, [string]$resultPath) {
    $savedErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        # Reproduce the real old Host's inherited native working directory.
        # The parent stays outside app so only the helper can release this lock.
        $workingApp = Join-Path $installRoot "app"
        New-Item -ItemType Directory -Path $workingApp -Force | Out-Null
        $start = New-Object System.Diagnostics.ProcessStartInfo
        $start.FileName = $powershellExe
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.WorkingDirectory = $workingApp
        $start.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -PackageDir "{1}" -InstallRoot "{2}" -Version "{3}" -ResultPath "{4}" -NoStart' -f $helper, $packageDir, $installRoot, $version, $resultPath
        $process = [Diagnostics.Process]::Start($start)
        $process.WaitForExit()
        $exitCode = $process.ExitCode
        $output = @()
    }
    finally {
        $ErrorActionPreference = $savedErrorActionPreference
    }
    foreach ($line in $output) { Write-Host $line }
    Assert-That (Test-Path -LiteralPath $resultPath) "Updater did not write result JSON: $resultPath"
    $result = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json
    Assert-That ($result.version -eq $version) "Unexpected result version '$($result.version)' in $resultPath"
    return @{ ExitCode = $exitCode; Result = $result }
}

$oldVersion = "0.1.1"
$newVersion = "0.1.2"

# A missing installer must fail before replacing the baseline app or metadata.
$missingPackage = Join-Path $testRoot "missing-package"
$missingInstall = Join-Path $testRoot "missing-install-root"
$missingResult = Join-Path $testRoot "missing-result.json"
New-Item -ItemType Directory -Path $missingPackage -Force | Out-Null
Write-Text (Join-Path $missingInstall "app\baseline.txt") "baseline-app"
Write-Text (Join-Path $missingInstall "install.json") (@{ version = $oldVersion } | ConvertTo-Json)
$missing = Invoke-UpdateCase $missingPackage $missingInstall $newVersion $missingResult
Assert-That ($missing.ExitCode -eq 1) "Missing-installer case returned exit $($missing.ExitCode), expected 1."
Assert-That ($missing.Result.status -eq "failed") "Missing-installer result status was '$($missing.Result.status)'."
Assert-That ($missing.Result.error -match 'no install.ps1') "Missing-installer case did not reach package validation: $($missing.Result.error)"
Assert-That ((Get-Content -LiteralPath (Join-Path $missingInstall "app\baseline.txt") -Raw) -eq "baseline-app") `
    "Missing-installer case changed the baseline app."
Assert-That ((Get-Content -LiteralPath (Join-Path $missingInstall "install.json") -Raw | ConvertFrom-Json).version -eq $oldVersion) `
    "Missing-installer case changed baseline install metadata."

# A failing installer deliberately overwrites one marker and metadata before exiting.
$failurePackage = Join-Path $testRoot "failure-package"
$failureInstall = Join-Path $testRoot "failure-install-root"
$failureResult = Join-Path $testRoot "failure-result.json"
$failureInstaller = @'
param([string]$Source, [string]$InstallRoot, [string]$Version, [switch]$NoShortcut, [switch]$NoLaunch)
$ErrorActionPreference = "Stop"
$appDir = Join-Path $InstallRoot "app"
[IO.File]::WriteAllText((Join-Path $appDir "baseline.txt"), "partial-replacement")
[IO.File]::WriteAllText((Join-Path $appDir "partial.txt"), "partial")
[IO.File]::WriteAllText((Join-Path $InstallRoot "extension\extension-marker.txt"), "partial-extension")
[IO.File]::WriteAllText((Join-Path $InstallRoot "install.json"), (@{ version = $Version } | ConvertTo-Json))
exit 1
'@
Write-Text (Join-Path $failurePackage "install.ps1") $failureInstaller
New-Item -ItemType Directory -Path (Join-Path $failurePackage "app") -Force | Out-Null
Write-Text (Join-Path $failureInstall "app\baseline.txt") "baseline-app"
Write-Text (Join-Path $failureInstall "extension\extension-marker.txt") "baseline-extension"
Write-Text (Join-Path $failureInstall "install.json") (@{ version = $oldVersion } | ConvertTo-Json)
$failed = Invoke-UpdateCase $failurePackage $failureInstall $newVersion $failureResult
Assert-That ($failed.ExitCode -eq 1) "Failing-installer case returned exit $($failed.ExitCode), expected 1."
Assert-That ($failed.Result.status -eq "failed") "Failing-installer result status was '$($failed.Result.status)'."
Assert-That ($failed.Result.error -match 'installer exited with 1') "Partial-failure case did not run its fixture installer: $($failed.Result.error)"
Assert-That ((Get-Content -LiteralPath (Join-Path $failureInstall "app\baseline.txt") -Raw) -eq "baseline-app") `
    "Rollback did not restore the original app marker."
Assert-That (-not (Test-Path -LiteralPath (Join-Path $failureInstall "app\partial.txt"))) `
    "Rollback left a partial replacement file in the app."
Assert-That ((Get-Content -LiteralPath (Join-Path $failureInstall "extension\extension-marker.txt") -Raw) -eq "baseline-extension") `
    "Rollback did not restore the original extension marker."
Assert-That ((Get-Content -LiteralPath (Join-Path $failureInstall "install.json") -Raw | ConvertFrom-Json).version -eq $oldVersion) `
    "Rollback did not restore original install metadata."

# A persistent file lock may prevent binary rollback, but must never leave the
# new version record behind. This reproduces the owner's false up-to-date state.
$lockedInstall = Join-Path $testRoot "locked-install-root"
$lockedResult = Join-Path $testRoot "locked-result.json"
Write-Text (Join-Path $lockedInstall "app\baseline.txt") "baseline-app"
Write-Text (Join-Path $lockedInstall "extension\extension-marker.txt") "baseline-extension"
Write-Text (Join-Path $lockedInstall "install.json") (@{ version = $oldVersion } | ConvertTo-Json)
$lockedFile = [IO.File]::Open((Join-Path $lockedInstall "app\baseline.txt"),
    [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
try {
    $locked = Invoke-UpdateCase $failurePackage $lockedInstall $newVersion $lockedResult
    Assert-That ($locked.ExitCode -eq 1) "Locked-file fixture unexpectedly succeeded."
    Assert-That ($locked.Result.error -match 'rollback error') "Fixture did not exercise locked-file rollback."
    Assert-That ((Get-Content -LiteralPath (Join-Path $lockedInstall "install.json") -Raw | ConvertFrom-Json).version -eq $oldVersion) `
        "Locked-file rollback left a falsely advanced version record."
}
finally { $lockedFile.Dispose() }

# Success writes a marker with an .exe name, but NoStart guarantees it is never executed.
$successPackage = Join-Path $testRoot "success-package"
$successInstall = Join-Path $testRoot "success-install-root"
$successResult = Join-Path $testRoot "success-result.json"
$successInstaller = @'
param([string]$Source, [string]$InstallRoot, [string]$Version, [switch]$NoShortcut, [switch]$NoLaunch)
$ErrorActionPreference = "Stop"
$appDir = Join-Path $InstallRoot "app"
if (Test-Path -LiteralPath $appDir) { Remove-Item -LiteralPath $appDir -Recurse -Force }
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $appDir "DownloadManager.Host.exe"), "NOT AN EXECUTABLE - TEST MARKER")
[IO.File]::WriteAllText((Join-Path $appDir "version-marker.txt"), $Version)
[IO.File]::WriteAllText((Join-Path $InstallRoot "install.json"), (@{ version = $Version } | ConvertTo-Json))
exit 0
'@
Write-Text (Join-Path $successPackage "install.ps1") $successInstaller
New-Item -ItemType Directory -Path (Join-Path $successPackage "app") -Force | Out-Null
$succeeded = Invoke-UpdateCase $successPackage $successInstall $newVersion $successResult
Assert-That ($succeeded.ExitCode -eq 0) "Successful-installer case returned exit $($succeeded.ExitCode), expected 0."
Assert-That ($succeeded.Result.status -eq "succeeded") "Success result status was '$($succeeded.Result.status)'."
Assert-That ((Get-Content -LiteralPath (Join-Path $successInstall "install.json") -Raw | ConvertFrom-Json).version -eq $newVersion) `
    "Successful install metadata did not contain the expected version."
Assert-That ((Get-Content -LiteralPath (Join-Path $successInstall "app\version-marker.txt") -Raw) -eq $newVersion) `
    "Successful installer did not write the expected app marker."
Assert-That ((Get-Content -LiteralPath (Join-Path $successInstall "app\DownloadManager.Host.exe") -Raw) -eq "NOT AN EXECUTABLE - TEST MARKER") `
    "The fake Host marker was changed or missing."

Write-Host "Updater helper regression checks passed. Fixture artifacts retained at: $testRoot"
