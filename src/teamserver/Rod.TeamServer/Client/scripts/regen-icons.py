#!/usr/bin/env python3
"""Regenerates the operator UI's icon set from the project logo.

The logo is a dark-slate mark (~rgb(64,68,72)) on a transparent canvas.
Rendered small on a browser tab that is close to invisible -- a dark mark
over a dark tab bar, and mush wherever the browser does its own scaling.
The icon set therefore gets two favicon-only treatments on top of the
Lanczos resize the flat geometry needs anyway:

  - a solid dark rounded tile behind the mark, so the icon owns its
    contrast on both light and dark tab bars,
  - the mark trimmed to its content bounds and centered to fill the tile
    (the raw canvas is half padding), and
  - a luminance lift that moves the slate arms to a mid grey while the
    white rod channels stay white.

docs/assets/rod-logo.png stays the source of truth and is untouched; only
the generated icon files carry the treatment. The in-app brand mark skips
the tile (the UI slot is its own surface) and ships trimmed and transparent
instead, in a plain and a dark-lifted variant.

Run from anywhere; paths are resolved from this file's location:

    python3 Client/scripts/regen-icons.py

Requires Pillow (a user-level `pip install --user pillow` is enough).
Change the logo there, then re-run this.
"""

from pathlib import Path

from PIL import Image, ImageDraw

# (size, filename): the PNG icon sizes index.html declares, plus the
# apple-touch icon (180 px, iOS home-screen bookmark). The in-app brand
# mark is generated separately (see BRAND below).
ICONS = [
    (16, "favicon-16.png"),
    (32, "favicon-32.png"),
    (48, "favicon-48.png"),
    (180, "apple-touch-icon.png"),
]

# The tile the mark sits on, and how far the mark's dark tones are lifted
# toward white (0 = untouched, 1 = all white). Tuned so the hexagon reads
# at 16 px without changing the logo's character.
TILE = (24, 24, 27)
LIFT = 0.42
# Corner radius of the tile as a fraction of the icon size.
CORNER = 0.22
# How much of the tile the trimmed mark fills. Edge-to-edge would read
# cramped at 16 px; a hexagon still wants a sliver of breathing room.
FILL = 0.86


def lift_rgb(source: Image.Image) -> Image.Image:
    """Lifts RGB toward white while leaving alpha untouched.

    Image.point() would lift the alpha channel too, turning fully
    transparent pixels into visible fog -- over the favicon tile as a gray
    film, and standalone in the brand mark.
    """
    r, g, b, a = source.split()
    rgb = Image.merge("RGB", (r, g, b)).point(lambda v: round(255 - (255 - v) * (1 - LIFT)))
    r, g, b = rgb.split()
    return Image.merge("RGBA", (r, g, b, a))


def treat(source: Image.Image) -> Image.Image:
    """Applies the favicon treatment at full resolution, before resizing."""
    size = source.width
    tile = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(tile)
    draw.rounded_rectangle(
        (0, 0, size - 1, size - 1), radius=int(size * CORNER), fill=TILE + (255,)
    )

    # Trim the source's transparent margins and re-center the mark so it
    # fills the tile; the raw canvas is half padding, which at 16 px reads
    # as a dark square with a speck in the middle.
    cropped = source.crop(source.getbbox())
    scale = (size * FILL) / max(cropped.size)
    mark = cropped.resize(
        (round(cropped.width * scale), round(cropped.height * scale)), Image.LANCZOS
    )
    centered = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    centered.paste(
        mark, ((size - mark.width) // 2, (size - mark.height) // 2), mark
    )

    # lift_rgb, not a bare point(): the lift must leave alpha alone, or the
    # transparent padding turns into a gray film over the whole tile.
    lifted = lift_rgb(centered)
    return Image.alpha_composite(tile, lifted)


# The in-app brand mark (sidebar, login card). Unlike a favicon, the UI slot
# gives the logo its own surface, so the mark ships transparent and trimmed
# to the content bounds -- the favicon tile would read as a dark square with
# a small mark floating inside. Two variants: the untouched mark for light
# surfaces, and the same luminance lift as the favicons for dark ones; the
# stylesheet picks per color scheme.
BRAND_SIZE = 96
BRAND = [
    ("brand-logo.png", False),
    ("brand-logo-dark.png", True),
]


def brand(source: Image.Image, lift: bool) -> Image.Image:
    """Crops to the mark and centers it on a square, transparent canvas."""
    cropped = source.crop(source.getbbox())
    side = max(cropped.size)
    square = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square.paste(cropped, ((side - cropped.width) // 2, (side - cropped.height) // 2), cropped)
    if lift:
        square = lift_rgb(square)
    return square.resize((BRAND_SIZE, BRAND_SIZE), Image.LANCZOS)


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
    for name, lift in BRAND:
        out = client / "public" / name
        brand(source, lift).save(out, optimize=True)
        print(f"{out.relative_to(client)}  {BRAND_SIZE}x{BRAND_SIZE} (brand)")


if __name__ == "__main__":
    main()
