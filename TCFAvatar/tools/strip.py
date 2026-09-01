"""Contact strips from the per-tick capture in .build/turnshots.

The numeric gates in tools/verify_walk.mjs check that the frame *index* never
skips.  They cannot see what the frames look like, and every real defect found so
far -- translucent frames, a head that did not turn, a double-exposed face,
washed-out colour -- passed them.  This renders what actually reached the screen,
in order, so it can be looked at.
"""
import re
import sys
from pathlib import Path

from PIL import Image

BUILD = Path(__file__).resolve().parent.parent / ".build"
SHOTS = BUILD / "turnshots"


def strip(prefix, out, columns=14):
    tiles = sorted(SHOTS.glob(f"{prefix}-*.png"), key=lambda p: int(p.name.split("-")[1]))
    if not tiles:
        print(f"no {prefix} tiles in {SHOTS}")
        return
    images = [Image.open(p).convert("RGBA") for p in tiles]
    w, h = images[0].size
    rows = (len(images) + columns - 1) // columns
    sheet = Image.new("RGBA", (columns * w, rows * h), (30, 30, 34, 255))
    for i, im in enumerate(images):
        sheet.alpha_composite(im, ((i % columns) * w, (i // columns) * h))
    sheet.save(out)
    names = [re.sub(r"^\w+-\d+-", "", p.stem) for p in tiles]
    print(f"{out.name}: {len(images)} tiles {w}x{h}")
    print("  " + " ".join(names))


if __name__ == "__main__":
    for name in sys.argv[1:] or ["start", "stop"]:
        strip(name, BUILD / f"strip-{name}.png")
