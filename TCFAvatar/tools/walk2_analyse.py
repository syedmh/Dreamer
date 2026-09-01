"""Answer the structural questions about `Walking2.png` before anything is built on it.

The art is only useful if the sequences actually join up, and that cannot be taken
on faith from the captions:

1. Is `walkRight`/`walkLeft` a **loop**?  Frame 13 must lead back into frame 0 as
   smoothly as any interior step, or the cycle will hitch once per stride.
2. Where does each turn row **leave** the walk?  `turn_00` should be one of the
   walk frames, so stopping can hand over without a jump.
3. Where does each turn row **arrive**?  `turn_13` should be an idle frame, so the
   settle can hand over to the idle loop without a jump.

Everything is measured on normalised cut-outs: aligned on the ground line and on
the centroid of the head-and-torso block, which unlike the bounding box does not
move as the limbs swing.
"""
from pathlib import Path

import numpy as np
from PIL import Image

import walk2_sheet as sheet

OUT = sheet.OUT
CANVAS = (260, 240)     # h, w -- generous, everything is centred in it
TORSO_FRACTION = 0.45   # top of the figure used to find a stable centre


def normalise(fig):
    """Cut-out placed on a common canvas, aligned on ground line and torso centre."""
    rgba = np.asarray(sheet.cutout(fig)).astype(np.float32)
    alpha = rgba[..., 3] / 255.0
    h, w = alpha.shape
    top = alpha[: max(1, int(h * TORSO_FRACTION))]
    weight = top.sum()
    cx = float((top.sum(axis=0) * np.arange(w)).sum() / weight) if weight else w / 2

    canvas = np.zeros((CANVAS[0], CANVAS[1], 4), np.float32)
    dx = int(round(CANVAS[1] / 2 - cx))
    dy = CANVAS[0] - h
    x0, y0 = max(0, dx), max(0, dy)
    sx, sy = max(0, -dx), max(0, -dy)
    cw = min(w - sx, CANVAS[1] - x0)
    ch = min(h - sy, CANVAS[0] - y0)
    canvas[y0:y0 + ch, x0:x0 + cw] = rgba[sy:sy + ch, sx:sx + cw]
    return canvas


def delta(a, b):
    """Mean absolute difference over the union of the two silhouettes."""
    aa, ab = a[..., 3] / 255.0, b[..., 3] / 255.0
    union = np.maximum(aa, ab)
    if union.sum() == 0:
        return 0.0
    rgb = np.abs(a[..., :3] * aa[..., None] - b[..., :3] * ab[..., None]).mean(axis=2)
    shape = np.abs(aa - ab) * 255.0
    return float(((rgb + shape) * union).sum() / union.sum())


def main():
    figs = sheet.figures()
    norm = {name: [normalise(f) for f in items] for name, items in figs.items()}

    print("1. Is the walk a loop?  (interior step vs the 13->0 wrap)\n")
    for name in ("walkRight", "walkLeft"):
        frames = norm[name]
        steps = [delta(frames[i], frames[i + 1]) for i in range(len(frames) - 1)]
        wrap = delta(frames[-1], frames[0])
        print(f"  {name}: interior mean={np.mean(steps):6.2f} "
              f"min={min(steps):6.2f} max={max(steps):6.2f}   wrap={wrap:6.2f} "
              f"({wrap / np.mean(steps):.2f}x mean)")
        print(f"    steps: {' '.join(f'{s:.0f}' for s in steps)}")

    print("\n2. Where does the turn leave the walk?  (turn_00 vs every walk frame)\n")
    for turn, walk in (("turnRight", "walkRight"), ("turnLeft", "walkLeft")):
        d = [delta(norm[turn][0], f) for f in norm[walk]]
        order = np.argsort(d)
        print(f"  {turn}_00 -> {walk}: best={order[0]} ({d[order[0]]:.2f}), "
              f"next={order[1]} ({d[order[1]]:.2f})")
        print(f"    all: {' '.join(f'{v:.0f}' for v in d)}")

    print("\n3. Where does the turn arrive?  (turn_13 vs every idle frame)\n")
    idle = norm["idleFront"] + norm["idleFrontB"]
    for turn in ("turnRight", "turnLeft"):
        d = [delta(norm[turn][-1], f) for f in idle]
        order = np.argsort(d)
        print(f"  {turn}_13 -> idle: best={order[0]} ({d[order[0]]:.2f}), "
              f"next={order[1]} ({d[order[1]]:.2f})")
        print(f"    all: {' '.join(f'{v:.0f}' for v in d)}")

    print("\n4. Interior smoothness of the turn and idle rows\n")
    for name in ("turnRight", "turnLeft", "idleFront", "idleFrontB"):
        frames = norm[name]
        steps = [delta(frames[i], frames[i + 1]) for i in range(len(frames) - 1)]
        print(f"  {name:11s} mean={np.mean(steps):6.2f} max={max(steps):6.2f}  "
              f"steps: {' '.join(f'{s:.0f}' for s in steps)}")

    OUT.mkdir(exist_ok=True)
    for name, frames in norm.items():
        strip = Image.new("RGBA", (CANVAS[1] * len(frames), CANVAS[0]), (24, 24, 28, 255))
        for i, f in enumerate(frames):
            strip.alpha_composite(Image.fromarray(f.astype(np.uint8), "RGBA"),
                                  (i * CANVAS[1], 0))
        strip.save(OUT / f"norm-{name}.png")
    print(f"\nwrote norm-*.png to {OUT}")


if __name__ == "__main__":
    main()
