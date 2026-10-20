"""Builds the icon atlas of the GeoFoundry OpenMapTiles labels from the SVG icons of the openmaptiles style.

Usage:
    python build_atlas.py <openmaptiles>/style/icons <output dir> [--inkscape PATH] [--scale 2] [--width 1024]

Writes <output dir>/omt-icons.png and <output dir>/omt-icons.json in the format read by OmtIconAtlas:
    {"width": W, "height": H, "icons": [{"name": "cafe", "x": 0, "y": 0, "w": 28, "h": 28, "pixelRatio": 2}, ...]}
Icon names are the SVG file names without the extension (e.g. "bus_stop.12", "road_motorway").
Requirements: Inkscape 1.x on PATH (or --inkscape) and Pillow. The icons are CC0 / CC-BY 4.0 (OpenMapTiles,
derived from OSM Carto); keep the attribution in "Third Party Notices.md".
"""
import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

from PIL import Image

PADDING = 2

# Fill and line patterns of the style (not used by labels).
PATTERNS = {
    "arete-mid", "arete2", "cliff", "cliff2", "ridge-mid", "ridge2", "embankment", "danger_red_hatch",
    "military_red_hatch", "grey_vertical_hatch", "intermittent_water", "golf_rough", "rock_overlay", "scree_overlay",
}


def rasterize(svg_dir, work_dir, inkscape, scale):
    svgs = sorted(f for f in os.listdir(svg_dir) if f.lower().endswith(".svg"))
    for f in svgs:
        shutil.copy(os.path.join(svg_dir, f), os.path.join(work_dir, f))
    files = [os.path.join(work_dir, f) for f in svgs]
    dpi = str(int(96 * scale))
    # Inkscape exports each input next to itself when several files are given.
    for i in range(0, len(files), 50):
        subprocess.run([inkscape, "--export-type=png", "--export-dpi=" + dpi] + files[i:i + 50],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    icons = []
    for f in svgs:
        png = os.path.join(work_dir, os.path.splitext(f)[0] + ".png")
        if not os.path.exists(png):
            print("warning: no output for", f, file=sys.stderr)
            continue
        icons.append((os.path.splitext(f)[0], Image.open(png).convert("RGBA")))
    return icons


def pack(icons, width):
    # Shelf packing, tallest first.
    order = sorted(icons, key=lambda it: (-it[1].height, it[0]))
    x = y = shelf = 0
    placed = []
    for name, img in order:
        w, h = img.width + 2 * PADDING, img.height + 2 * PADDING
        if x + w > width:
            x, y, shelf = 0, y + shelf, 0
        placed.append((name, img, x + PADDING, y + PADDING))
        x += w
        shelf = max(shelf, h)
    height = 1
    while height < y + shelf:
        height *= 2
    return placed, height


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("icons")
    ap.add_argument("output")
    ap.add_argument("--inkscape", default="inkscape")
    ap.add_argument("--scale", type=float, default=2.0)
    ap.add_argument("--width", type=int, default=1024)
    ap.add_argument("--max-size", type=int, default=96, help="skip rasterized icons larger than this (patterns)")
    args = ap.parse_args()

    os.makedirs(args.output, exist_ok=True)
    work = tempfile.mkdtemp(prefix="omt-icons-")
    icons = [(n, img) for n, img in rasterize(args.icons, work, args.inkscape, args.scale)
             if n not in PATTERNS and img.width <= args.max_size and img.height <= args.max_size]
    placed, height = pack(icons, args.width)

    atlas = Image.new("RGBA", (args.width, height), (0, 0, 0, 0))
    entries = []
    for name, img, x, y in placed:
        atlas.paste(img, (x, y))
        entries.append({"name": name, "x": x, "y": y, "w": img.width, "h": img.height, "pixelRatio": args.scale})
    entries.sort(key=lambda e: e["name"])

    atlas.save(os.path.join(args.output, "omt-icons.png"), optimize=True)
    with open(os.path.join(args.output, "omt-icons.json"), "w", encoding="utf-8") as f:
        json.dump({"width": args.width, "height": height, "icons": entries}, f, indent=1)
    shutil.rmtree(work, ignore_errors=True)
    print(f"{len(entries)} icons, atlas {args.width}x{height}")


if __name__ == "__main__":
    main()
