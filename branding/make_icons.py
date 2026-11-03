"""Draws the Notch logo and writes every icon file the Mac and Windows apps use.

    python3 branding/make_icons.py        (needs Pillow: python3 -m pip install pillow)

The generated files are committed, so building the apps doesn't need this script. Run it again after
changing the logo here.

Writes:
    branding/notch.svg                                 the logo as a vector, for documents and the web
    branding/notch.png                                 512 px, shown in the READMEs
    Resources/AppIcon.icns                             Mac app icon (macOS grid: margin and shadow)
    windows/src/SDBackup.App/Assets/Notch.ico          Windows app icon (Start menu, taskbar, Explorer)
    windows/src/SDBackup.App/Assets/Tray.ico           notification-area icon
    windows/src/SDBackup.App/Assets/TrayAttention.ico  the same with a blue dot: a card is waiting
    windows/src/SDBackup.App/Assets/Notch.png          small logo for the panel's footer

The logo, on a 100-unit square: an orange SD card (named for its cut top-right corner, the notch)
with a dark check, on a graphite rounded square. Tray icons and the smallest Windows sizes show just
the card, filling the icon, so it stays legible. The Mac menu bar icon is drawn in code from the same
card shape (Sources/SDBackup/MenuBarIcon.swift).
"""
from __future__ import annotations

import math
from functools import lru_cache
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
BRANDING = ROOT / "branding"
MAC_RESOURCES = ROOT / "Resources"
WINDOWS_ASSETS = ROOT / "windows" / "src" / "SDBackup.App" / "Assets"

GRAPHITE_TOP = (54, 60, 75)     # #363C4B
GRAPHITE_BOTTOM = (20, 23, 30)  # #14171E
ORANGE_TOP = (255, 154, 82)     # #FF9A52, the card's top left
ORANGE_BOTTOM = (229, 64, 58)   # #E5403A, the card's bottom right
CHECK = (26, 29, 37)            # #1A1D25
WHITE = (255, 255, 255)
BLUE = (10, 132, 255)           # the attention dot

WORK = 2048  # Drawn at this size, then scaled down, for smooth edges.


class Art:
    """Where the card and the check go, in logo units."""

    def __init__(self, card, check, check_width, background):
        self.card = card  # left, top, right, bottom, corner radius, size of the cut corner
        self.check = check
        self.check_width = check_width
        self.background = background


# The full logo: the card on the graphite rounded square.
FULL = Art(card=(23.68, 13.28, 76.32, 84.72, 4.7, 13.16),
           check=[(36.84, 50.88), (46.24, 60.28), (64.10, 41.48)], check_width=7.05, background=True)
# Small sizes and the tray: just the card, filling the icon.
MARK = Art(card=(15.0, 2.5, 85.0, 97.5, 6.25, 17.5),
           check=[(33.0, 54.0), (45.0, 66.0), (68.0, 42.0)], check_width=11.0, background=False)

MAC_MARGIN = 100 / 1024  # Apple's icon grid: an 824 px body on a 1024 px canvas.


def squircle(n: float = 5.0, steps: int = 720) -> list[tuple[float, float]]:
    """The rounded square (a superellipse) filling the 100-unit logo square."""
    points = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        points.append((50 + 50 * math.copysign(abs(c) ** (2 / n), c), 50 + 50 * math.copysign(abs(s) ** (2 / n), s)))
    return points


def card_outline(left, top, right, bottom, radius, cut, steps: int = 16) -> list[tuple[float, float]]:
    """An SD card: rounded corners, except the top right, which is cut off."""
    def arc(cx, cy, start, end):
        return [(cx + radius * math.cos(math.radians(a)), cy + radius * math.sin(math.radians(a)))
                for a in (start + (end - start) * i / steps for i in range(steps + 1))]
    return (arc(left + radius, top + radius, 180, 270)
            + [(right - cut, top), (right, top + cut)]
            + arc(right - radius, bottom - radius, 0, 90)
            + arc(left + radius, bottom - radius, 90, 180))


def gradient(width: int, height: int, start, end, diagonal: bool) -> Image.Image:
    small = Image.new("RGB", (64, 64))
    for y in range(64):
        for x in range(64):
            t = (x + y) / 126 if diagonal else y / 63
            small.putpixel((x, y), tuple(round(a + (b - a) * t) for a, b in zip(start, end)))
    return small.resize((max(width, 1), max(height, 1)), Image.BICUBIC)


@lru_cache(maxsize=None)
def render_work(full: bool, margin: float, shadow: bool, dot: bool) -> Image.Image:
    art = FULL if full else MARK
    k = WORK * (1 - 2 * margin) / 100  # pixels per logo unit
    offset = WORK * margin

    def px(points):
        return [(offset + x * k, offset + y * k) for x, y in points]

    def mask(points) -> Image.Image:
        m = Image.new("L", (WORK, WORK), 0)
        ImageDraw.Draw(m).polygon(px(points), fill=255)
        return m

    image = Image.new("RGBA", (WORK, WORK), (0, 0, 0, 0))

    if art.background:
        body = mask(squircle())
        if shadow:  # The soft shadow under every macOS icon.
            blurred = body.filter(ImageFilter.GaussianBlur(WORK * 0.012))
            layer = Image.new("RGBA", (WORK, WORK), (0, 0, 0, 0))
            layer.paste(Image.new("RGBA", (WORK, WORK), (0, 0, 0, 77)), (0, round(WORK * 0.010)), blurred)
            image = Image.alpha_composite(image, layer)
        image.paste(gradient(WORK, WORK, GRAPHITE_TOP, GRAPHITE_BOTTOM, diagonal=False), (0, 0), body)

    # The card, with its orange running corner to corner.
    (left, top), (right, bottom) = ((round(x), round(y)) for x, y in px([art.card[:2], art.card[2:4]]))
    fill = Image.new("RGB", (WORK, WORK))
    fill.paste(gradient(right - left, bottom - top, ORANGE_TOP, ORANGE_BOTTOM, diagonal=True), (left, top))
    image.paste(fill, (0, 0), mask(card_outline(*art.card)))

    # The check: a thick line with round ends and a round corner.
    draw = ImageDraw.Draw(image)
    points = px(art.check)
    width = art.check_width * k
    draw.line(points, fill=CHECK + (255,), width=round(width))
    for x, y in points:
        draw.ellipse([x - width / 2, y - width / 2, x + width / 2, y + width / 2], fill=CHECK + (255,))

    if dot:  # Bottom right, ringed in white so it stands out on any taskbar.
        cx = cy = offset + 79 * k
        draw.ellipse([cx - 21 * k, cy - 21 * k, cx + 21 * k, cy + 21 * k], fill=WHITE + (255,))
        draw.ellipse([cx - 16 * k, cy - 16 * k, cx + 16 * k, cy + 16 * k], fill=BLUE + (255,))

    return image


def render(size: int, *, full: bool = True, margin: float = 0.0, shadow: bool = False, dot: bool = False) -> Image.Image:
    return render_work(full, margin, shadow, dot).resize((size, size), Image.LANCZOS)


def save_ico(path: Path, images: dict[int, Image.Image]) -> None:
    sizes = sorted(images)
    images[sizes[-1]].save(path, format="ICO", sizes=[(s, s) for s in sizes], append_images=[images[s] for s in sizes[:-1]])
    print(f"Wrote {path.relative_to(ROOT)}")


def svg() -> str:
    def path(points) -> str:
        return "M" + " L".join(f"{x:.2f} {y:.2f}" for x, y in points) + " Z"

    check = " L".join(f"{x:.2f} {y:.2f}" for x, y in FULL.check)
    return f"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100" width="512" height="512">
  <title>Notch</title>
  <defs>
    <linearGradient id="graphite" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#363C4B"/>
      <stop offset="1" stop-color="#14171E"/>
    </linearGradient>
    <linearGradient id="orange" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#FF9A52"/>
      <stop offset="1" stop-color="#E5403A"/>
    </linearGradient>
  </defs>
  <path d="{path(squircle(steps=360))}" fill="url(#graphite)"/>
  <path d="{path(card_outline(*FULL.card))}" fill="url(#orange)"/>
  <path d="M{check}" fill="none" stroke="#1A1D25" stroke-width="{FULL.check_width}" stroke-linecap="round" stroke-linejoin="round"/>
</svg>
"""


def main() -> None:
    BRANDING.mkdir(exist_ok=True)
    (BRANDING / "notch.svg").write_text(svg(), encoding="utf-8")
    print("Wrote branding/notch.svg")
    render(512).save(BRANDING / "notch.png")
    print("Wrote branding/notch.png")

    # Mac: the shadow is part of the artwork on macOS.
    mac = {size: render(size, margin=MAC_MARGIN, shadow=size >= 64) for size in (16, 32, 64, 128, 256, 512, 1024)}
    mac[1024].save(MAC_RESOURCES / "AppIcon.icns", append_images=[mac[s] for s in (16, 32, 64, 128, 256, 512)])
    print("Wrote Resources/AppIcon.icns")

    # Windows: Explorer, Start and the taskbar pick the size they need from the .ico.
    WINDOWS_ASSETS.mkdir(parents=True, exist_ok=True)
    save_ico(WINDOWS_ASSETS / "Notch.ico",
             {size: render(size, full=size >= 24, margin=0.0 if size < 24 else 0.03) for size in (16, 20, 24, 32, 40, 48, 64, 128, 256)})
    tray_sizes = (16, 20, 24, 32, 40, 48, 64)
    save_ico(WINDOWS_ASSETS / "Tray.ico", {size: render(size, full=False) for size in tray_sizes})
    save_ico(WINDOWS_ASSETS / "TrayAttention.ico", {size: render(size, full=False, dot=True) for size in tray_sizes})
    render(64, full=False).save(WINDOWS_ASSETS / "Notch.png")
    print("Wrote windows/src/SDBackup.App/Assets/Notch.png")


if __name__ == "__main__":
    main()
