#!/usr/bin/env python3
"""Regenerates the operator UI's icon set from the project logo.

The browser tab icon is the project logo, but a browser scaling the
full-size logo down to 16-32 px itself samples it into mush. This script
pre-renders the exact sizes the page declares (Lanczos, so the flat
geometry stays crisp) and writes them into Client/public/, which the Vite
build copies into the served bundle.

Run from anywhere; paths are resolved from this file's location:

    python3 Client/scripts/regen-icons.py

Requires Pillow (a user-level `pip install --user pillow` is enough).
The source of truth is docs/assets/rod-logo.png -- change the logo there,
then re-run this.
"""

from pathlib import Path

from PIL import Image

# (size, filename): the PNG icon sizes index.html declares, plus the
# apple-touch icon (180 px, iOS home-screen bookmark).
ICONS = [
    (16, "favicon-16.png"),
    (32, "favicon-32.png"),
    (48, "favicon-48.png"),
    (180, "apple-touch-icon.png"),
]


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
    for size, name in ICONS:
        icon = source.resize((size, size), Image.LANCZOS)
        out = client / "public" / name
        icon.save(out, optimize=True)
        print(f"{out.relative_to(client)}  {size}x{size}")


if __name__ == "__main__":
    main()
