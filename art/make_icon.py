#!/usr/bin/env python3
"""Generates the L2Start launcher icon (art/l2start_icon_1024.png + src/L2Launcher/icon.ico).
Colors follow App.xaml: Bg #15171C, Panel #1F232B, Accent (gold) #C9A45C. Small sizes are drawn separately
(simpler, bolder) so the emblem stays legible at 16 px.  Requires Pillow."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
CINZEL = "/usr/share/fonts/truetype/sand-box/google/Cinzel/Cinzel-VariableFont_wght.ttf"
SANS_BOLD = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
BG, PANEL, GOLD, GOLD_HI, GOLD_LO = (21, 23, 28), (31, 35, 43), (201, 164, 92), (240, 212, 150), (138, 106, 46)

def grad(size, top, bottom):
    g = Image.new("RGBA", (1, size))
    for y in range(size):
        t = y / max(1, size - 1)
        g.putpixel((0, y), tuple(int(top[i] + (bottom[i] - top[i]) * t) for i in range(3)) + (255,))
    return g.resize((size, size))

def cinzel(px):
    f = ImageFont.truetype(CINZEL, px)
    try: f.set_variation_by_axes([900])
    except Exception: pass
    return f

def big(S=1024):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    m = int(S * 0.04); r = int(S * 0.2)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([m, m, S - m, S - m], r, fill=255)
    bg = grad(S, (40, 45, 56), BG)
    img.paste(bg, (0, 0), mask)
    d = ImageDraw.Draw(img)
    w = int(S * 0.035)
    d.rounded_rectangle([m, m, S - m, S - m], r, outline=GOLD, width=w)
    i = m + int(S * 0.07)
    d.rounded_rectangle([i, i, S - i, S - i], r - int(S * 0.07), outline=GOLD_LO, width=max(2, w // 3))
    # "L2" in Cinzel with a vertical gold gradient + soft glow
    font = cinzel(int(S * 0.5))
    tm = Image.new("L", (S, S), 0)
    td = ImageDraw.Draw(tm)
    bb = td.textbbox((0, 0), "L2", font=font)
    x = (S - (bb[2] - bb[0])) // 2 - bb[0]; y = (S - (bb[3] - bb[1])) // 2 - bb[1] - int(S * 0.02)
    td.text((x, y), "L2", font=font, fill=255)
    glow = Image.new("RGBA", (S, S), GOLD + (0,)); glow.putalpha(tm.filter(ImageFilter.GaussianBlur(S * 0.03)).point(lambda v: v * 0.55))
    img.alpha_composite(glow)
    img.paste(grad(S, GOLD_HI, GOLD_LO), (0, 0), tm)
    # small underline accent
    uy = int(S * 0.79); d.line([(S * 0.36, uy), (S * 0.64, uy)], fill=GOLD, width=max(2, int(S * 0.012)))
    return img

def small(n):
    """Pixel-friendly version for 16-48 px: flat dark tile, 1 px gold frame, bold sans 'L2'."""
    k = 8; S = n * k
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    r = int(S * (0.18 if n >= 32 else 0.12))
    d.rounded_rectangle([0, 0, S - 1, S - 1], r, fill=PANEL, outline=GOLD, width=k if n < 32 else int(k * n / 24))
    font = ImageFont.truetype(SANS_BOLD if n < 32 else CINZEL, int(S * (0.62 if n < 32 else 0.56)))
    if n >= 32:
        try: font.set_variation_by_axes([900])
        except Exception: pass
    bb = d.textbbox((0, 0), "L2", font=font)
    x = (S - (bb[2] - bb[0])) // 2 - bb[0]; y = (S - (bb[3] - bb[1])) // 2 - bb[1]
    d.text((x, y), "L2", font=font, fill=GOLD_HI if n < 32 else GOLD)
    return img.resize((n, n), Image.LANCZOS)

if __name__ == "__main__":
    art = ROOT / "art"; art.mkdir(exist_ok=True)
    master = big(1024)
    master.save(art / "l2start_icon_1024.png")
    imgs = {n: small(n) for n in (16, 20, 24, 32, 40, 48)}
    for n in (64, 128, 256):
        imgs[n] = master.resize((n, n), Image.LANCZOS)
    for n, im in imgs.items():
        im.save(art / f"icon_{n}.png")
    order = [256, 128, 64, 48, 40, 32, 24, 20, 16]
    imgs[256].save(ROOT / "src" / "L2Launcher" / "icon.ico", format="ICO",
                   sizes=[(n, n) for n in order], append_images=[imgs[n] for n in order[1:]])
    # preview sheet
    sheet = Image.new("RGBA", (sum(order) + 10 * len(order) + 10, 276), BG + (255,))
    x = 10
    for n in order:
        sheet.alpha_composite(imgs[n], (x, 10)); x += n + 10
    sheet.save(art / "icon_preview.png")
    print("ok")
