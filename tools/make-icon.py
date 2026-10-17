"""Draws the PocketCake icon: a guitar pedal built like a layer cake.

Writes <prefix>.png (256 px), <prefix>.ico (16-256 px; 32 px and below use a simplified
drawing), <prefix>_1024.png and <prefix>_preview.png. Copy the first two to
src/PocketCake/Assets/app_icon.png and AppIcon.ico.
"""
import sys
from PIL import Image, ImageDraw, ImageFilter, ImageChops

S = 2048  # supersampled canvas
OUT = sys.argv[1] if len(sys.argv) > 1 else "icon"


def k(v):
    return int(v * S / 1024)


def vgrad(size, top, bottom):
    w, h = size
    g = Image.new("RGBA", (1, h))
    for y in range(h):
        t = y / max(h - 1, 1)
        g.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(4)))
    return g.resize((w, h))


def rgba(hex_, a=255):
    hex_ = hex_.lstrip("#")
    return tuple(int(hex_[i:i + 2], 16) for i in (0, 2, 4)) + (a,)


def layer():
    return Image.new("RGBA", (S, S), (0, 0, 0, 0))


def mask_of(draw_fn):
    m = Image.new("L", (S, S), 0)
    draw_fn(ImageDraw.Draw(m))
    return m


def fill(img, mask, top, bottom=None):
    g = vgrad((S, S), rgba(top), rgba(bottom or top))
    img.paste(g, (0, 0), mask)


def draw(detail):
    img = layer()
    
    # Body geometry
    bx0, by0, bx1, by1, br = k(128), k(236), k(896), k(960), k(150)
    body = mask_of(lambda d: d.rounded_rectangle((bx0, by0, bx1, by1), br, fill=255))
    
    # Soft drop shadow
    sh = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    shm = mask_of(lambda d: d.rounded_rectangle((bx0 + k(8), by0 + k(28), bx1 - k(8), by1 + k(28)), br, fill=255))
    sh.paste((60, 20, 10, 90), (0, 0), shm)
    sh = sh.filter(ImageFilter.GaussianBlur(k(22)))
    img = Image.alpha_composite(img, sh)
    
    # Sponge (the enclosure)
    sponge = layer()
    fill(sponge, body, "#FBCB7E", "#E8973F")
    img = Image.alpha_composite(img, sponge)
    
    # Cream filling stripe
    cream = mask_of(lambda d: d.rectangle((0, k(628), S, k(680)), fill=255))
    stripe = layer()
    fill(stripe, ImageChops.multiply(cream, body), "#FFF6E6", "#FCE7C4")
    img = Image.alpha_composite(img, stripe)
    
    # Frosting top with drips
    def frosting(d):
        d.rectangle((0, 0, S, k(440)), fill=255)
        for x, depth, w in [(190, 540, 92), (318, 492, 76), (440, 566, 96), (572, 500, 78), (694, 552, 92), (820, 488, 76)]:
            d.rounded_rectangle((k(x - w / 2), k(400), k(x + w / 2), k(depth)), k(w / 2), fill=255)
    
    fm = ImageChops.multiply(mask_of(frosting), body)
    fr = layer()
    fill(fr, fm, "#FF8AC8", "#EC4899")
    img = Image.alpha_composite(img, fr)
    
    # Frosting gloss along the top edge
    gloss = mask_of(lambda d: d.rounded_rectangle((bx0 + k(40), by0 + k(26), bx1 - k(40), by0 + k(70)), k(22), fill=255))
    gl = layer()
    if detail:
        gl.paste((255, 255, 255, 90), (0, 0), gloss)
    img = Image.alpha_composite(img, gl)
    
    d = ImageDraw.Draw(img)
    
    # Chocolate knobs
    for cx in ((300, 512, 724) if detail else ()):
        cy, r = 366, 52
        d.ellipse((k(cx - r - 4), k(cy - r + 4), k(cx + r + 4), k(cy + r + 12)), fill=rgba("#9D174D", 90))
        kn = layer()
        km = mask_of(lambda dd: dd.ellipse((k(cx - r), k(cy - r), k(cx + r), k(cy + r)), fill=255))
        fill(kn, km, "#7B4A2E", "#4A2A18")
        img = Image.alpha_composite(img, kn)
        d = ImageDraw.Draw(img)
        d.rounded_rectangle((k(cx - 7), k(cy - r + 10), k(cx + 7), k(cy - 10)), k(7), fill=rgba("#FFE9D6"))
    
    # Footswitch: chrome
    cx, cy = 512, 806
    fs = 1.0 if detail else 1.35
    d.ellipse((k(cx - 108 * fs), k(cy - 104 * fs), k(cx + 108 * fs), k(cy + 114 * fs)), fill=rgba("#9A5414", 80))
    outer = layer()
    fill(outer, mask_of(lambda dd: dd.ellipse((k(cx - 100 * fs), k(cy - 100 * fs), k(cx + 100 * fs), k(cy + 100 * fs)), fill=255)), "#F4F6F8", "#9AA3AD")
    img = Image.alpha_composite(img, outer)
    inner = layer()
    fill(inner, mask_of(lambda dd: dd.ellipse((k(cx - 70 * fs), k(cy - 70 * fs), k(cx + 70 * fs), k(cy + 70 * fs)), fill=255)), "#B9C1C9", "#EEF1F4")
    img = Image.alpha_composite(img, inner)
    
    # Cherry on top = status LED
    d = ImageDraw.Draw(img)
    ccx, ccy, cr = 512, 214, (70 if detail else 96)
    if detail:
        d.line((k(ccx + 10), k(ccy - 40), k(ccx + 70), k(ccy - 150)), fill=rgba("#3F6212"), width=k(16))
    glow = layer()
    glow.paste((255, 60, 80, 120), (0, 0), mask_of(lambda dd: dd.ellipse((k(ccx - cr - 24), k(ccy - cr - 24), k(ccx + cr + 24), k(ccy + cr + 24)), fill=255)))
    glow = glow.filter(ImageFilter.GaussianBlur(k(20)))
    img = Image.alpha_composite(img, glow)
    ch = layer()
    fill(ch, mask_of(lambda dd: dd.ellipse((k(ccx - cr), k(ccy - cr), k(ccx + cr), k(ccy + cr)), fill=255)), "#FF4D5E", "#C81E3A")
    img = Image.alpha_composite(img, ch)
    d = ImageDraw.Draw(img)
    if detail:
        d.ellipse((k(ccx - 40), k(ccy - 44), k(ccx - 8), k(ccy - 16)), fill=(255, 255, 255, 200))
    return img


master = draw(True).resize((1024, 1024), Image.LANCZOS)
simple = draw(False).resize((1024, 1024), Image.LANCZOS)
master.save(f"{OUT}_1024.png")

sizes = [256, 128, 64, 48, 32, 24, 16]
frames = [(master if s > 32 else simple).resize((s, s), Image.LANCZOS) for s in sizes]
frames[0].save(f"{OUT}.png")
frames[0].save(f"{OUT}.ico", sizes=[(s, s) for s in sizes], append_images=frames[1:])

# Preview sheet on light and dark backgrounds
sheet = Image.new("RGBA", (1200, 600), (243, 243, 243, 255))
sheet.paste((32, 32, 32, 255), (0, 300, 1200, 600))
x = 20
for s, f in zip(sizes, frames):
    for y0 in (0, 300):
        sheet.alpha_composite(f, (x, y0 + 150 - s // 2))
    x += s + 30
sheet.save(f"{OUT}_preview.png")
print("ok")
