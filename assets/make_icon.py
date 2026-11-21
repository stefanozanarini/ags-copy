#!/usr/bin/env python3
"""Draws the AGS-Copy icon from the AGS Media mark: red circling arrows on black, "AGS COPY" on a
white band below. Writes assets/ags-copy.png (256 px preview) and src/AgsCopy.App/AGS-Copy.ico.

Sizes up to 40 px leave the text out (it would be a grey smear) and give the arrows the whole tile.
Needs Pillow and a bold oblique sans font (FreeSans Bold Oblique by default).
"""
import math
import os
import sys
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FONT = os.environ.get("ICON_FONT", "/usr/share/fonts/truetype/freefont/FreeSansBoldOblique.ttf")
RED = (226, 18, 26, 255)
BLACK = (12, 12, 12, 255)
WHITE = (255, 255, 255, 255)
S = 1024  # drawing size; every icon size is scaled down from it


def arrows(size, squash):
    """Two arcs chasing each other round an ellipse, each ending in an arrowhead pointing inward."""
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    c = size / 2
    r = size * 0.36
    w = size * 0.13
    for start in (200, 20):  # degrees; the second arc is the first turned half a circle
        end = start + 120
        box = [c - r, c - r, c + r, c + r]
        d.arc(box, start, end, fill=RED, width=int(w))
        # Arrowhead at the end of the arc, along the tangent and bent toward the centre.
        a = math.radians(end)
        tip_dir = a + math.pi / 2 + math.radians(28)
        base = (c + (r - w / 2) * math.cos(a), c + (r - w / 2) * math.sin(a))
        head = w * 1.25
        tip = (base[0] + head * 1.5 * math.cos(tip_dir), base[1] + head * 1.5 * math.sin(tip_dir))
        side = (math.cos(tip_dir + math.pi / 2), math.sin(tip_dir + math.pi / 2))
        back = (base[0] - head * 0.15 * math.cos(tip_dir), base[1] - head * 0.15 * math.sin(tip_dir))
        d.polygon([tip,
                   (back[0] + side[0] * head, back[1] + side[1] * head),
                   (back[0] - side[0] * head, back[1] - side[1] * head)], fill=RED)
    return layer.resize((size, int(size * squash)), Image.LANCZOS)


def tile(with_text):
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=S * 0.16, fill=255)
    body = Image.new("RGBA", (S, S), BLACK)
    d = ImageDraw.Draw(body)

    if with_text:
        band = int(S * 0.66)
        d.rectangle([0, band, S, S], fill=WHITE)
        a = arrows(int(S * 0.98), 0.64)
        body.alpha_composite(a, ((S - a.width) // 2, (band - a.height) // 2 + int(S * 0.01)))
        # "AGS" in black like the AGS Media mark, "COPY" in the red of the arrows.
        parts = [("AGS ", BLACK), ("COPY", RED)]
        size = int(S * 0.30)
        while True:
            font = ImageFont.truetype(FONT, size)
            widths = [d.textlength(t, font=font) for t, _ in parts]
            if sum(widths) <= S * 0.90 or size < 40:
                break
            size -= 4
        box = d.textbbox((0, 0), "AGS COPY", font=font)
        x = (S - sum(widths)) / 2
        y = band + (S - band - (box[3] - box[1])) / 2 - box[1]
        for (t, colour), w in zip(parts, widths):
            d.text((x, y), t, font=font, fill=colour)
            x += w
    else:
        a = arrows(int(S * 1.12), 0.80)
        body.alpha_composite(a, ((S - a.width) // 2, (S - a.height) // 2))

    img.paste(body, (0, 0), mask)
    return img


def main():
    big, small = tile(True), tile(False)
    sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
    frames = [(small if n <= 40 else big).resize((n, n), Image.LANCZOS) for n in sizes]
    big.resize((256, 256), Image.LANCZOS).save(os.path.join(ROOT, "assets", "ags-copy.png"))
    small.resize((256, 256), Image.LANCZOS).save(os.path.join(ROOT, "assets", "ags-copy-small.png"))
    # Tray: no text, the state dot is drawn on top at runtime.
    small.resize((64, 64), Image.LANCZOS).save(os.path.join(ROOT, "src", "AgsCopy.App", "Tray.png"))
    ico = os.path.join(ROOT, "src", "AgsCopy.App", "AGS-Copy.ico")
    frames[-1].save(ico, format="ICO", sizes=[(n, n) for n in sizes], append_images=frames[:-1])
    print("wrote", ico)


if __name__ == "__main__":
    sys.exit(main())
