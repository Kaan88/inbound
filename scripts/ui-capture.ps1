# Saves a PNG of every visible top-level window of a running test UI (main window, dialogs' host window, menus and
# flyouts, which are windows of their own) with PrintWindow and PW_RENDERFULLCONTENT. Starts nothing and sends no input.
# Attaches only to a UI whose executable path contains -PathPart, and refuses the installed app.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PathPart,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Name,
    # Also capture windows smaller than this (menus are small); tooltips and empty helpers are skipped below it.
    [int]$MinSize = 24
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class UiCaptureNative {
  public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  public struct RECT { public int Left, Top, Right, Bottom; }
  public static List<IntPtr> Windows(uint pid) {
    var list = new List<IntPtr>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
    return list;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
}
'@

# Every matching process: a download's progress window runs in a process of its own (--job).
$procs = @(Get-CimInstance Win32_Process -Filter "Name='DownloadManager.Desktop.WinUI.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath -like "*$PathPart*" })
if ($procs.Count -eq 0) { Write-Output 'NO TEST UI'; exit 1 }
if ($procs | Where-Object { $_.ExecutablePath -like '*\Programs\Inbound Download Manager\*' }) { Write-Output 'REFUSED: installed app'; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$index = 0
$handles = foreach ($proc in $procs) { [UiCaptureNative]::Windows([uint32]$proc.ProcessId) }
foreach ($h in $handles) {
    $r = New-Object UiCaptureNative+RECT
    [UiCaptureNative]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
    if ($w -lt $MinSize -or $hgt -lt $MinSize) { continue }
    $cls = [UiCaptureNative]::Cls($h); $title = [UiCaptureNative]::Title($h)
    $bmp = New-Object System.Drawing.Bitmap $w, $hgt
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [UiCaptureNative]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc); $g.Dispose()
    $suffix = if ($index -eq 0) { '' } else { "-$index" }
    $file = Join-Path $OutDir "$Name$suffix.png"
    $bmp.Save($file, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    Write-Output ("CAPTURED {0} class={1} title='{2}' rect={3},{4} {5}x{6}" -f $file, $cls, $title, $r.Left, $r.Top, $w, $hgt)
    $index++
}
if ($index -eq 0) { Write-Output 'NO VISIBLE WINDOW' }
