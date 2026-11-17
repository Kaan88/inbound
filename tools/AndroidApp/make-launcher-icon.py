# Writes the Android launcher icon from the app's own artwork (assets/inbound-icon.png, the transparent 1024 px master).
# A plain (not adaptive) icon with a transparent background, as on Windows. Owner, 2026-11-18: "could you make it
# transparent background, that's how it is on windows."; an adaptive icon with a transparent background layer still got
# a backdrop from Samsung's launcher, so there is no adaptive icon (and so no themed icon on Android 13+).
# 48 dp per density; the artwork's longest side is 46 dp. Run: python -I tools/AndroidApp/make-launcher-icon.py
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
RES = ROOT / "src/DownloadManager.Android/Resources"
art = Image.open(ROOT / "assets/inbound-icon.png").convert("RGBA")
art = art.crop(art.getchannel("A").getbbox())
for name, scale in {"mdpi": 1.0, "hdpi": 1.5, "xhdpi": 2.0, "xxhdpi": 3.0, "xxxhdpi": 4.0}.items():
    side = round(48 * scale)
    k = 46 * scale / max(art.size)
    a = art.resize((round(art.width * k), round(art.height * k)), Image.LANCZOS)
    icon = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    icon.paste(a, ((side - a.width) // 2, (side - a.height) // 2), a)
    out = RES / f"mipmap-{name}"
    out.mkdir(exist_ok=True)
    icon.save(out / "ic_launcher.png", optimize=True)
print("written")
