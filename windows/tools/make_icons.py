"""Draws the SD Backup icons (an SD card in Windows blue) at every size Windows uses.

    python windows/tools/make_icons.py

Writes src/SDBackup.App/Assets/SDBackup.ico and SDBackupAttention.ico (with an orange dot,
shown in the notification area when a card is waiting for a project).
"""
from pathlib import Path

from PIL import Image, ImageDraw

SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
BLUE = (0, 120, 212, 255)
WHITE = (255, 255, 255, 255)
ORANGE = (247, 99, 12, 255)
SCALE = 8  # Draw large, then shrink, for smooth edges.


def card(size: int, attention: bool) -> Image.Image:
    s = size * SCALE
    image = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    u = s / 24  # Drawn on a 24-unit grid.

    # Card body with the cut top-right corner.
    left, top, right, bottom, cut, radius = 4 * u, 1.5 * u, 20 * u, 22.5 * u, 4.5 * u, 2.2 * u
    body = [(left, top + radius), (left + radius, top), (right - cut, top), (right, top + cut), (right, bottom - radius),
            (right - radius, bottom), (left + radius, bottom), (left, bottom - radius)]
    draw.polygon(body, fill=BLUE)
    for cx, cy in [(left + radius, top + radius), (right - radius, bottom - radius), (left + radius, bottom - radius)]:
        draw.ellipse([cx - radius, cy - radius, cx + radius, cy + radius], fill=BLUE)

    # Contacts.
    for x in (7.0, 10.5, 14.0):
        draw.rounded_rectangle([x * u, 4 * u, (x + 2) * u, 8.5 * u], radius=0.6 * u, fill=WHITE)

    if attention:
        r = 5.2 * u
        cx, cy = 18 * u, 18 * u
        draw.ellipse([cx - r - 1.2 * u, cy - r - 1.2 * u, cx + r + 1.2 * u, cy + r + 1.2 * u], fill=WHITE)
        draw.ellipse([cx - r, cy - r, cx + r, cy + r], fill=ORANGE)

    return image.resize((size, size), Image.LANCZOS)


def main() -> None:
    assets = Path(__file__).resolve().parent.parent / "src" / "SDBackup.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    for name, attention in (("SDBackup.ico", False), ("SDBackupAttention.ico", True)):
        images = [card(size, attention) for size in SIZES]
        images[-1].save(assets / name, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
        print(f"Wrote {assets / name}")
    card(256, False).save(assets / "SDBackup.png")


if __name__ == "__main__":
    main()
