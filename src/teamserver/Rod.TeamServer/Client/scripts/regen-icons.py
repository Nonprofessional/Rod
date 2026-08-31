#!/usr/bin/env python3
"""Regenerates the operator UI's icon set from the project logo.

The logo is a dark-slate mark (~rgb(64,68,72)) on a transparent canvas.
Rendered small on a browser tab that is close to invisible -- a dark mark
over a dark tab bar, and mush wherever the browser does its own scaling.
The icon set therefore gets two favicon-only treatments on top of the
Lanczos resize the flat geometry needs anyway:

  - a solid dark rounded tile behind the mark, so the icon owns its
    contrast on both light and dark tab bars, and
  - a luminance lift that moves the slate arms to a mid grey while the
    white rod channels stay white.

docs/assets/rod-logo.png stays the source of truth and is untouched; only
the generated icon files carry the treatment.

Run from anywhere; paths are resolved from this file's location:

    python3 Client/scripts/regen-icons.py

Requires Pillow (a user-level `pip install --user pillow` is enough).
Change the logo there, then re-run this.
"""

from pathlib import Path

from PIL import Image, ImageDraw

# (size, filename): the PNG icon sizes index.html declares, the apple-touch
# icon (180 px, iOS home-screen bookmark), and the in-app brand mark the
# sidebar and login card render at ~28 px (96 px keeps it crisp on hi-dpi).
ICONS = [
    (16, "favicon-16.png"),
    (32, "favicon-32.png"),
    (48, "favicon-48.png"),
    (96, "brand-logo.png"),
    (180, "apple-touch-icon.png"),
]

# The tile the mark sits on, and how far the mark's dark tones are lifted
# toward white (0 = untouched, 1 = all white). Tuned so the hexagon reads
# at 16 px without changing the logo's character.
TILE = (24, 24, 27)
LIFT = 0.42
# Corner radius of the tile as a fraction of the icon size.
CORNER = 0.22


def treat(source: Image.Image) -> Image.Image:
    """Applies the favicon treatment at full resolution, before resizing."""
    size = source.width
    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(tile)
    draw.rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=int(size * CORNER), fill=TILE + (255,)
    )

    lifted = source.point(lambda v: round(255 - (255 - v) * (1 - LIFT)))
    return Image.alpha_composite(tile, lifted)


def main() -> None:
    here = Path(__file__).resolve()
    client = here.parent.parent

    # Walk up from the script to the repository root (the directory that
    # carries docs/assets/), so the script works regardless of where in the
    # tree the client lives.
    repo = client
    while repo != repo.parent:
        if (repo / "docs" / "assets" / "rod-logo.png").exists():
            break
        repo = repo.parent
    logo = repo / "docs" / "assets" / "rod-logo.png"
    if not logo.exists():
        raise SystemExit(f"logo not found above {client}")

    source = Image.open(logo).convert("RGBA")
    treated = treat(source)
    for icon_size, name in ICONS:
        icon = treated.resize((icon_size, icon_size), Image.LANCZOS)
        out = client / "public" / name
        icon.save(out, optimize=True)
        print(f"{out.relative_to(client)}  {icon_size}x{icon_size}")


if __name__ == "__main__":
    main()
