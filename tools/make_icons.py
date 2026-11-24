"""Generates assets/app.ico and assets/icons/<ext>.ico (multi-size) for ArcBox."""
import os, sys
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "assets", "icons")
os.makedirs(OUT, exist_ok=True)
FONT = "/usr/share/fonts/opentype/inter/Inter-Bold.otf"
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

EXTS = ("zip zipx rar 7z alz egg cab bh 001 arj lha lzh pma ace arc aes zpaq zst br pea wim sqx zoo ha "
        "tar gz tgz bz bz2 tbz tbz2 xz txz lzma tlz lz uu uue xxe z taz lz4 tzst cpio rpm deb ar "
        "iso img vhd vhdx dmg chm msi apk jar xpi").split()

def color(ext):
    if ext in ("zip", "zipx"): return (0x2B, 0x6B, 0xD6)
    if ext == "7z": return (0x1F, 0x8A, 0x70)
    if ext == "rar": return (0x8E, 0x44, 0xAD)
    if ext in ("alz", "egg"): return (0xD9, 0x40, 0x5A)
    if ext in ("tar", "gz", "tgz", "bz", "bz2", "tbz", "tbz2", "xz", "txz", "lzma", "tlz", "lz", "z", "taz", "lz4", "tzst", "zst", "cpio", "rpm", "deb", "ar", "uu", "uue", "xxe"):
        return (0xD3, 0x6E, 0x1E)
    if ext in ("iso", "img", "vhd", "vhdx", "dmg", "wim"): return (0x4F, 0x6B, 0x7D)
    return (0x00, 0x5F, 0xB8)

def shade(c, f): return tuple(max(0, min(255, int(v * f))) for v in c)

def box(size, c, label):
    """Archive box: rounded body, darker lid, zipper, label."""
    S = 4  # supersample
    W = size * S
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    m = W * 0.08
    top = W * 0.14
    r = W * 0.10
    d.rounded_rectangle([m, top, W - m, W - m * 0.7], radius=r, fill=c + (255,))
    lid_h = W * 0.22
    d.rounded_rectangle([m - W * 0.02, top - W * 0.04, W - m + W * 0.02, top + lid_h], radius=r * 0.8, fill=shade(c, 0.78) + (255,))
    # zipper / tape
    zx = W / 2
    tw = W * 0.10
    d.rectangle([zx - tw / 2, top - W * 0.04, zx + tw / 2, top + lid_h + W * 0.16], fill=(255, 255, 255, 235))
    if size >= 32 and label:
        txt = label.upper()
        fs = W * (0.30 if len(txt) <= 2 else 0.24 if len(txt) == 3 else 0.19)
        font = ImageFont.truetype(FONT, int(fs))
        bb = d.textbbox((0, 0), txt, font=font)
        tx = (W - (bb[2] - bb[0])) / 2 - bb[0]
        ty = top + lid_h + (W - m * 0.7 - top - lid_h) * 0.58 - (bb[3] - bb[1]) / 2 - bb[1]
        d.text((tx, ty), txt, font=font, fill=(255, 255, 255, 255))
    return im.resize((size, size), Image.LANCZOS)

def app_icon(size):
    S = 4
    W = size * S
    im = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    d = ImageDraw.Draw(im)
    d.rounded_rectangle([W * 0.04, W * 0.04, W * 0.96, W * 0.96], radius=W * 0.22, fill=(0x00, 0x5F, 0xB8, 255))
    lw = max(S * 2, int(W * 0.065))
    # open box glyph
    x0, x1, y0, y1 = W * 0.26, W * 0.74, W * 0.42, W * 0.76
    d.rounded_rectangle([x0, y0, x1, y1], radius=W * 0.04, outline=(255, 255, 255, 255), width=lw)
    d.line([(x0, y0), (x0 + W * 0.08, W * 0.27), (x1 - W * 0.08, W * 0.27), (x1, y0)], fill=(255, 255, 255, 255), width=lw, joint="curve")
    d.line([(W * 0.42, W * 0.54), (W * 0.58, W * 0.54)], fill=(255, 255, 255, 255), width=lw)
    return im.resize((size, size), Image.LANCZOS)

def save(path, frames):
    big = frames[-1]
    big.save(path, format="ICO", sizes=[(f.width, f.height) for f in frames], append_images=frames[:-1])

save(os.path.join(ROOT, "assets", "app.ico"), [app_icon(s) for s in SIZES])
save(os.path.join(OUT, "archive.ico"), [box(s, color("x"), "") for s in SIZES])
for e in EXTS:
    save(os.path.join(OUT, e + ".ico"), [box(s, color(e), e) for s in SIZES])
# preview sheet
sheet = Image.new("RGBA", (12 * 72, 6 * 72), (255, 255, 255, 255))
for i, e in enumerate(["app"] + EXTS):
    img = app_icon(64) if e == "app" else box(64, color(e), e)
    sheet.alpha_composite(img, ((i % 12) * 72 + 4, (i // 12) * 72 + 4))
sheet.save(sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, "assets", "preview.png"))
print("icons:", len(EXTS) + 2)
