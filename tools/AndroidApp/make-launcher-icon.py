# Writes the Android launcher icon from the app's own artwork (assets/inbound-icon.png, the transparent 1024 px master).
# Adaptive icon (min SDK 31): foreground and monochrome (themed icons, Android 13+) layers per density, on the dark
# background of the web app's maskable icon. The artwork's longest side is 52 of the 108 dp, inside the 66 dp circle
# every launcher mask keeps. Run: python -I tools/AndroidApp/make-launcher-icon.py (from the repo root).
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[2]
RES = ROOT / "src/DownloadManager.Android/Resources"
art = Image.open(ROOT / "assets/inbound-icon.png").convert("RGBA")
art = art.crop(art.getchannel("A").getbbox())
DENSITIES = {"mdpi": 1.0, "hdpi": 1.5, "xhdpi": 2.0, "xxhdpi": 3.0, "xxxhdpi": 4.0}
for name, scale in DENSITIES.items():
    side = round(108 * scale)
    longest = 52 * scale
    k = longest / max(art.size)
    a = art.resize((round(art.width * k), round(art.height * k)), Image.LANCZOS)
    fg = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    fg.paste(a, ((side - a.width) // 2, (side - a.height) // 2), a)
    mono = Image.new("RGBA", (side, side), (255, 255, 255, 0))
    mono.putalpha(fg.getchannel("A"))
    out = RES / f"mipmap-{name}"
    out.mkdir(exist_ok=True)
    fg.save(out / "ic_launcher_foreground.png", optimize=True)
    mono.save(out / "ic_launcher_monochrome.png", optimize=True)
print("written")
