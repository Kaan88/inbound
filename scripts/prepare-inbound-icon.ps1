[CmdletBinding()]
param([string]$Source, [string]$Out)
# Converts the owner-supplied Inbound artwork (assets\inbound-icon-source.png, opaque white background)
# into a square, tightly cropped, transparent 1024px master for scripts\build-icons.ps1.
# Only background-connected near-white pixels become transparent, so the white plane body stays opaque.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $Source) { $Source = Join-Path $root 'assets\inbound-icon-source.png' }
if (-not $Out) { $Out = Join-Path $root 'assets\inbound-icon.png' }
Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Collections.Generic; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
public static class IconPrep {
  public static Bitmap Run(Bitmap src, int size) {
    int w = src.Width, h = src.Height;
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) g.DrawImage(src, 0, 0, w, h);
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[w * h * 4]; Marshal.Copy(data.Scan0, px, 0, px.Length);
    Func<int, int> white = i => Math.Min(px[i], Math.Min(px[i + 1], px[i + 2]));
    var seen = new bool[w * h]; var q = new Queue<int>();
    for (int x = 0; x < w; x++) { q.Enqueue(x); q.Enqueue((h - 1) * w + x); }
    for (int y = 0; y < h; y++) { q.Enqueue(y * w); q.Enqueue(y * w + w - 1); }
    while (q.Count > 0) {
      int p = q.Dequeue(); if (seen[p]) continue;
      int v = white(p * 4); if (v < 200) continue; // stop at outline / coloured art
      seen[p] = true;
      // Soft edge: anti-aliased light pixels keep partial alpha.
      px[p * 4 + 3] = (byte)(v >= 245 ? 0 : (int)(255 * (245 - v) / 45.0));
      int x = p % w, y = p / w;
      if (x > 0) q.Enqueue(p - 1); if (x < w - 1) q.Enqueue(p + 1);
      if (y > 0) q.Enqueue(p - w); if (y < h - 1) q.Enqueue(p + w);
    }
    int minX = w, minY = h, maxX = 0, maxY = 0;
    for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (px[(y * w + x) * 4 + 3] > 24) {
      if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
    Marshal.Copy(px, 0, data.Scan0, px.Length); bmp.UnlockBits(data);
    int side = Math.Max(maxX - minX, maxY - minY) + 1; int pad = side / 40;
    var outBmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(outBmp)) {
      g.Clear(Color.Transparent);
      g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
      g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
      float scale = size / (float)(side + 2 * pad);
      float cw = (maxX - minX + 1) * scale, ch = (maxY - minY + 1) * scale;
      g.DrawImage(bmp, new RectangleF((size - cw) / 2, (size - ch) / 2, cw, ch),
        new RectangleF(minX, minY, maxX - minX + 1, maxY - minY + 1), GraphicsUnit.Pixel);
    }
    bmp.Dispose(); return outBmp;
  }
}
'@
$src = [System.Drawing.Bitmap]::new($Source)
try { $result = [IconPrep]::Run($src, 1024) } finally { $src.Dispose() }
$result.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png); $result.Dispose()
Write-Output $Out
