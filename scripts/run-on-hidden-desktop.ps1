# Starts a process on a separate, never-shown Windows desktop (WinSta0\<Desktop>), so a test UI's windows never
# appear, never take focus and get no input from the owner's mouse or keyboard. Used for SITES.11 screenshots.
# The process inherits this shell's environment (set DOWNLOADMANAGER_INSTANCE / _DATA first). With -Wait the exit
# code is returned. Run ui-drive.ps1 / ui-capture.ps1 on the same desktop: window enumeration is per desktop.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Desktop,
    [Parameter(Mandatory)][string]$Exe,
    [string]$Arguments = '',
    [string]$WorkingDirectory,
    [switch]$Wait,
    [int]$TimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class HiddenDesktop {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  public struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags; public short wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
  [StructLayout(LayoutKind.Sequential)]
  public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr mode, uint flags, uint access, IntPtr sa);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool CreateProcess(string app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
    IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
}
'@
if ($Desktop -notmatch '^[A-Za-z0-9_-]+$') { throw "Desktop name must be letters, digits, - or _." }
# Without an isolated instance a test UI would signal or attach to the owner's running app.
if (-not $env:DOWNLOADMANAGER_INSTANCE -or -not $env:DOWNLOADMANAGER_DATA) {
    throw "Set DOWNLOADMANAGER_INSTANCE and DOWNLOADMANAGER_DATA first, so nothing reaches the installed app."
}
# GENERIC_ALL. The handle is deliberately kept open by this process only while it runs; a desktop lives on while any
# process started on it runs, so the started process keeps it alive.
$h = [HiddenDesktop]::CreateDesktop($Desktop, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x10000000, [IntPtr]::Zero)
if ($h -eq [IntPtr]::Zero) { throw "CreateDesktop failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())" }
$si = New-Object HiddenDesktop+STARTUPINFO
$si.cb = [Runtime.InteropServices.Marshal]::SizeOf($si)
$si.lpDesktop = "WinSta0\$Desktop"
$pi = New-Object HiddenDesktop+PROCESS_INFORMATION
$cmd = "`"$Exe`" $Arguments"
if (-not $WorkingDirectory) { $WorkingDirectory = Split-Path $Exe -Parent }
# CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW (console children get no console window anywhere)
if (-not [HiddenDesktop]::CreateProcess([NullString]::Value, (New-Object System.Text.StringBuilder $cmd, 32768), [IntPtr]::Zero, [IntPtr]::Zero, $false, 0x08000400, [IntPtr]::Zero,
        $WorkingDirectory, [ref]$si, [ref]$pi)) {
    throw "CreateProcess failed: $([Runtime.InteropServices.Marshal]::GetLastWin32Error())"
}
Write-Output "pid $($pi.dwProcessId) on WinSta0\$Desktop"
if ($Wait) {
    if ([HiddenDesktop]::WaitForSingleObject($pi.hProcess, [uint32]($TimeoutSeconds * 1000)) -ne 0) {
        [void][HiddenDesktop]::CloseHandle($pi.hProcess); [void][HiddenDesktop]::CloseHandle($pi.hThread)
        [Console]::Error.WriteLine("Timed out after $TimeoutSeconds s; process $($pi.dwProcessId) is still running on WinSta0\$Desktop.")
        exit 124
    }
    $code = 0; [void][HiddenDesktop]::GetExitCodeProcess($pi.hProcess, [ref]$code)
    [void][HiddenDesktop]::CloseHandle($pi.hProcess); [void][HiddenDesktop]::CloseHandle($pi.hThread)
    exit $code
}
[void][HiddenDesktop]::CloseHandle($pi.hProcess); [void][HiddenDesktop]::CloseHandle($pi.hThread)
