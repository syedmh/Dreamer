"""Recover the gait phase of each authored frame.

The frames in `Walking2.png` were drawn independently, so they are almost certainly
not evenly spaced in time.  Playing them at a uniform rate would then hitch --
which is exactly the complaint.  To find out, measure the one signal that defines
where a frame sits in a walk cycle: the horizontal offset of each foot from the
body centre.

Emits a zoomed leg strip alongside the numbers so the measurement can be checked
against the actual art rather than trusted.
"""
import numpy as np
from PIL import Image

import walk2_sheet as sheet
from walk2_analyse import CANVAS, normalise

FOOT_BAND = 0.10   # bottom fraction of the figure that is unambiguously foot
ZOOM = 3


def feet(frame):
    """Centre x of each foot, relative to the body centre, nearest-first."""
    alpha = frame[..., 3] / 255.0
    rows = alpha[int(CANVAS[0] * (1 - FOOT_BAND)):]
    cols = rows.max(axis=0) > 0.35
    runs, start = [], None
    for x, on in enumerate(cols):
        if on and start is None:
            start = x
        elif not on and start is not None:
            if x - start >= 4:
                runs.append((start, x - 1))
            start = None
    if start is not None and len(cols) - start >= 4:
        runs.append((start, len(cols) - 1))
    centre = CANVAS[1] / 2
    return [((a + b) / 2 - centre, b - a + 1) for a, b in runs]


def main():
    figs = sheet.figures()

    for name in ("walkRight", "walkLeft", "turnRight", "turnLeft"):
        frames = [normalise(f) for f in figs[name]]
        print(f"\n{name}")
        print("  fr | n |          foot offsets (px from body centre)  | spread")
        spreads = []
        for i, fr in enumerate(frames):
            f = feet(fr)
            offs = ", ".join(f"{o:+6.1f}(w{w})" for o, w in f)
            spread = (max(o for o, _ in f) - min(o for o, _ in f)) if len(f) > 1 else 0.0
            spreads.append(spread)
            print(f"  {i:2d} | {len(f)} | {offs:44s} | {spread:5.1f}")
        print(f"  spread sequence: {' '.join(f'{s:.0f}' for s in spreads)}")

        legs = Image.new("RGBA", (CANVAS[1] * len(frames) * ZOOM, 110 * ZOOM),
                         (24, 24, 28, 255))
        for i, fr in enumerate(frames):
            crop = Image.fromarray(fr.astype(np.uint8), "RGBA").crop(
                (0, CANVAS[0] - 110, CANVAS[1], CANVAS[0]))
            crop = crop.resize((CANVAS[1] * ZOOM, 110 * ZOOM), Image.LANCZOS)
            legs.alpha_composite(crop, (i * CANVAS[1] * ZOOM, 0))
        legs.save(sheet.OUT / f"legs-{name}.png")

    print(f"\nwrote legs-*.png to {sheet.OUT}")


if __name__ == "__main__":
    main()
