"""Generates the TermDeck logo: Assets/TermDeck.ico (multi-size) and Assets/logo.png.

Concept: a "deck" of terminal cards — blue (Windows) and orange (WSL) cards behind a dark
terminal card showing a green ">_" prompt. Small sizes (<= 24 px) use a simplified single card
so the prompt stays readable in the taskbar and title bar.

Usage: python tools/make_icon.py
"""
from pathlib import Path
from PIL import Image, ImageDraw

ASSETS = Path(__file__).resolve().parent.parent / "src" / "TermDeck" / "Assets"
SS = 4  # supersampling factor

BLUE = (30, 115, 216, 255)
ORANGE = (224, 112, 30, 255)
CARD = (27, 31, 36, 255)
CARD_EDGE = (58, 64, 72, 255)
GREEN = (22, 198, 12, 255)


def rounded(draw, box, radius, fill, outline=None, width=0):
    draw.rounded_rectangle(box, radius=radius, fill=fill, outline=outline, width=width)


def prompt(draw, x0, y0, size, stroke):
    """Draws '>_' inside a square area starting at (x0, y0) with the given side length."""
    s = size
    chevron = [(x0 + 0.20 * s, y0 + 0.30 * s), (x0 + 0.44 * s, y0 + 0.50 * s), (x0 + 0.20 * s, y0 + 0.70 * s)]
    draw.line(chevron, fill=GREEN, width=stroke, joint="curve")
    r = stroke / 2
    for px, py in (chevron[0], chevron[2]):  # round caps
        draw.ellipse((px - r, py - r, px + r, py + r), fill=GREEN)
    cursor_top = y0 + 0.70 * s - stroke / 2
    draw.rounded_rectangle((x0 + 0.52 * s, cursor_top, x0 + 0.80 * s, cursor_top + stroke),
                           radius=stroke / 3, fill=GREEN)


def full_logo(px):
    n = px * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = n / 256  # design units: 256 x 256 grid
    rad = 30 * u
    rounded(d, (60 * u, 8 * u, 248 * u, 196 * u), rad, BLUE)
    rounded(d, (36 * u, 32 * u, 224 * u, 220 * u), rad, ORANGE)
    rounded(d, (8 * u, 60 * u, 200 * u, 248 * u), rad, CARD, CARD_EDGE, max(1, int(4 * u)))
    prompt(d, 8 * u, 60 * u, 192 * u, int(20 * u))
    return img.resize((px, px), Image.LANCZOS)


def small_logo(px):
    n = px * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    u = n / 16
    # A thin blue/orange edge hints at the deck; the dark card fills most of the icon.
    rounded(d, (3 * u, 0, 16 * u, 13 * u), 3 * u, BLUE)
    rounded(d, (0, 3 * u, 13 * u, 16 * u), 3 * u, CARD)
    d.rectangle((13 * u, 13 * u - 1, 16 * u, 16 * u), fill=(0, 0, 0, 0))
    rounded(d, (13.2 * u, 3 * u, 16 * u, 13 * u), 1.5 * u, ORANGE)
    prompt(d, 0, 3 * u, 13 * u, int(2.2 * u))
    return img.resize((px, px), Image.LANCZOS)


def main():
    sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]
    images = [small_logo(s) if s <= 24 else full_logo(s) for s in sizes]
    big = images[-1]
    big.save(ASSETS / "TermDeck.ico", format="ICO", sizes=[(s, s) for s in sizes], append_images=images[:-1])
    full_logo(512).save(ASSETS / "logo.png")

    # Preview sheet for eyeballing all sizes on light and dark backgrounds.
    sheet = Image.new("RGBA", (sum(sizes) + 12 * len(sizes) + 12, 2 * 256 + 36), (255, 255, 255, 255))
    ImageDraw.Draw(sheet).rectangle((0, 256 + 24, sheet.width, sheet.height), fill=(32, 32, 32, 255))
    x = 12
    for s, im in zip(sizes, images):
        sheet.alpha_composite(im, (x, 12))
        sheet.alpha_composite(im, (x, 256 + 30))
        x += s + 12
    sheet.save(Path(__file__).resolve().parent / "icon-preview.png")
    print("wrote", ASSETS / "TermDeck.ico", "and logo.png")


if __name__ == "__main__":
    main()
