"""Renders the MSIX visual assets from the 1024 px icon made by make-icon.py.

Usage: python tools/make-store-assets.py <icon_1024.png> [out_dir]
Writes into src/PocketCake/Assets/Store by default; needs Pillow.
"""
import os
import sys
from PIL import Image

SRC = sys.argv[1]
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.join(
    os.path.dirname(__file__), "..", "src", "PocketCake", "Assets", "Store")

# (name, base width, base height, icon fraction of the shorter side)
TILES = [
    ("Square44x44Logo", 44, 44, 1.0),
    ("Square150x150Logo", 150, 150, 0.66),
    ("Wide310x150Logo", 310, 150, 0.66),
    ("StoreLogo", 50, 50, 1.0),
    ("SplashScreen", 620, 300, 0.66),
]
SCALES = [100, 125, 150, 200, 400]
TARGET_SIZES = [16, 24, 32, 48, 256]

icon = Image.open(SRC).convert("RGBA")
os.makedirs(OUT, exist_ok=True)


def render(w, h, frac):
    canvas = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    side = max(1, round(min(w, h) * frac))
    canvas.alpha_composite(icon.resize((side, side), Image.LANCZOS), ((w - side) // 2, (h - side) // 2))
    return canvas


for name, w, h, frac in TILES:
    for scale in SCALES:
        render(round(w * scale / 100), round(h * scale / 100), frac).save(
            os.path.join(OUT, f"{name}.scale-{scale}.png"))

# Taskbar / Start list icons drawn without a plate.
for size in TARGET_SIZES:
    img = render(size, size, 1.0)
    img.save(os.path.join(OUT, f"Square44x44Logo.targetsize-{size}.png"))
    img.save(os.path.join(OUT, f"Square44x44Logo.altform-unplated_targetsize-{size}.png"))
    img.save(os.path.join(OUT, f"Square44x44Logo.altform-lightunplated_targetsize-{size}.png"))
