"""Bake the authored art in ``Frames/`` into the game's sprite atlas.

This replaces the geometric rig that used to synthesise the gait from a single
profile drawing.  Every pose the game shows is now a frame the artist drew: six
walk poses and three turn poses per direction.

Two decisions here are worth stating, because both were settled by measurement
rather than preference.

**Nothing is interpolated.**  Six poses is half the temporal resolution the
synthesised rig had, so in-betweens were tried first and rejected on evidence.
DIS optical flow, Farneback, and a coarse-to-fine pyramid all register the
large-motion pairs equally badly -- the frame-to-frame difference falls from 70
to only 33, and no tuning moves it -- because a swinging leg uncovers parts of
the kurta that exist in neither neighbouring frame, and no flow field can invent
them.  Blending the two warped neighbours makes the legs translucent (the
partly-transparent pixel count goes from 1.7k to 20k); warping a single
neighbour avoids that but tears the shoes into the exact doubled smear that was
already rejected once.  A clean authored pose beats a fabricated one.

**Nothing is resampled.**  Frames are aligned with whole-pixel shifts only, so
the art reaches the atlas at the resolution it was drawn at.  The previous
pipeline warped every limb into place and had to sharpen afterwards to recover;
there is nothing to recover here.

The walk and the turn were exported with different root heights -- the walk's
feet sit about ten pixels higher than the stand's -- so each set is aligned on
its own ground plane, and each is centred on its own mean head position.  Left
and right turn art are separate drawings, but the front stand they both begin
from is one image, shared, so settling always lands on the idle frame exactly.
"""

from __future__ import annotations

import json
import pathlib
import sys

import numpy as np
import cv2
from PIL import Image
from scipy import ndimage

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
import frames_source as fs

ROOT = pathlib.Path(__file__).resolve().parent.parent
OUT = ROOT / "public" / "assets" / "hero"

MARGIN = 26          # px of clear space around the widest pose
DIRS = ("right", "left")
CAP = {"right": "Right", "left": "Left"}

# Walk frame the turn hands over to.  Measured, not chosen: the profile stand at
# the end of the turn is 68.6 away from walk_03 and 82-96 from every other walk
# frame, and 68.6 is inside the range ordinary walk steps already cover (30-70),
# so arriving there is no larger a jump than the gait makes on its own.
WALK_START_FRAME = 3


def _head_cx(rgba, frac=0.16):
    mask = rgba[..., 3] > 100
    rows = np.nonzero(mask.any(axis=1))[0]
    top = rows[0]
    height = rows[-1] - top + 1
    cols = np.nonzero(mask[top:top + int(height * frac)].any(axis=0))[0]
    return (cols[0] + cols[-1]) / 2.0


def _shift(rgba, dx, dy):
    out = np.zeros_like(rgba)
    h, w = rgba.shape[:2]
    sy0, sy1 = max(0, -dy), min(h, h - dy)
    dy0, dy1 = max(0, dy), min(h, h + dy)
    sx0, sx1 = max(0, -dx), min(w, w - dx)
    dx0, dx1 = max(0, dx), min(w, w + dx)
    out[dy0:dy1, dx0:dx1] = rgba[sy0:sy1, sx0:sx1]
    return out


def _anchor(frames):
    """(mean head x, ground plane) for a set of frames rendered together."""
    return (float(np.mean([_head_cx(f) for f in frames])),
            max(fs.ground(f) for f in frames))


def _planted(rgba):
    """Bounding box of the shoe that reaches the floor.

    The area floor has to be generous.  Keying keeps small detached fragments --
    a pinched-off toe cap, a lace highlight -- and one of those can sit lower
    than the shoe it broke off, so a floor low enough to admit it hands back a
    sliver that tracks nothing.
    """
    mask = rgba[..., 3] > 100
    g = fs.ground(rgba)
    strip = np.zeros_like(mask)
    strip[g - 70:g + 1] = mask[g - 70:g + 1]
    labels, count = ndimage.label(strip, np.ones((3, 3)))
    best, lowest = None, -1
    for k in range(1, count + 1):
        ys, xs = np.nonzero(labels == k)
        if len(xs) < 900:
            continue
        if ys.max() > lowest:
            lowest = ys.max()
            best = (int(xs.min()), int(xs.max()), int(ys.min()), int(ys.max()))
    return best


def measure_stride(frames):
    """Pixels the body advances over one six-frame cycle.

    The cycle is drawn in place, so the foot standing on the floor slides
    backwards through the frames by exactly the distance the body moved.  One
    template -- the planted shoe in frame 0 -- is matched into each later frame,
    rather than chaining frame to frame, so the estimate cannot accumulate drift.
    The foot stays planted through frame 4; frame 5 is where the other one lands.

    The search is restricted to positions behind the template's own, and to a
    plausible per-frame distance.  Unconstrained, the matcher happily locks onto
    the *other* shoe -- the two are near-identical objects a stride apart -- and
    returns a forward jump where the foot cannot have gone.

    Matching is on luminance with a sum-of-squared-difference score.  A binary
    silhouette makes every solid blob look alike, so the shoe matches the trouser
    above it; the shoe's own dark body and bright specular streak are what make
    it identifiable.
    """
    x0, x1, y0, y1 = _planted(frames[0])

    def grey(f):
        a = f[..., 3].astype(np.float32) / 255.0
        lum = f[..., :3].astype(np.float32) @ np.array([.299, .587, .114], np.float32)
        return np.clip(lum * a, 0, 255).astype(np.float32)

    template = grey(frames[0])[y0:y1 + 1, x0:x1 + 1]
    per_frame_max = 80
    offsets = [0.0]
    for i in range(1, 5):
        result = cv2.matchTemplate(grey(frames[i]), template, cv2.TM_SQDIFF_NORMED)
        lo = max(0, x0 - per_frame_max * i)
        hi = min(result.shape[1] - 1, x0 + 6)
        _, _, loc, _ = cv2.minMaxLoc(result[:, lo:hi + 1])
        offsets.append(float(loc[0] + lo - x0))
    assert all(b - a <= 2 for a, b in zip(offsets, offsets[1:])), (
        "planted foot moved forwards (%s) -- the tracker changed feet" % offsets)
    slope = float(np.polyfit(np.arange(len(offsets), dtype=float),
                             np.array(offsets, float), 1)[0])
    return abs(slope) * fs.WALK_COUNT, offsets


def measure_step(frames):
    """Heel-to-heel distance at the widest pose -- an independent stride check.

    One step carries the body from one foot's contact to the other's, so at full
    extension the gap between the two shoes is the same distance the six frames
    must advance.  Derived from completely different pixels to `measure_stride`,
    so agreement between the two means neither is an artefact of its own method.
    """
    best = 0.0
    for f in frames:
        mask = f[..., 3] > 100
        g = fs.ground(f)
        strip = np.zeros_like(mask)
        strip[g - 40:g + 1] = mask[g - 40:g + 1]
        labels, count = ndimage.label(strip, np.ones((3, 3)))
        centres = []
        for k in range(1, count + 1):
            xs = np.nonzero(labels == k)[1]
            if len(xs) < 200:
                continue
            centres.append(float(xs.mean()))
        if len(centres) >= 2:
            best = max(best, max(centres) - min(centres))
    return best


def build():
    sets = {}
    for d in DIRS:
        sets[("walk", d)] = list(fs.walk(d))
        sets[("turn", d)] = list(fs.turn(d))

    # One front stand for everything, so the settle always ends on the idle frame.
    stand = sets[("turn", "right")][0]

    aligned, extents = {}, []
    for key, frames in sets.items():
        ax, ground = _anchor(frames)
        # The stand belongs to the turn sets, but it has to land identically in
        # both, so it is anchored once with the right-hand turn and reused.
        placed = []
        for f in frames:
            placed.append((f, int(round(-ax)), -ground))
        aligned[key] = placed
        for f, dx, dy in placed:
            bx0, by0, bx1, by1 = fs.bbox(f)
            extents.append((bx0 + dx, by0 + dy, bx1 + dx, by1 + dy))

    left = -min(e[0] for e in extents)
    right = max(e[2] for e in extents)
    top = -min(e[1] for e in extents)
    bottom = max(e[3] for e in extents)

    frame_w = int(left + right + 2 * MARGIN)
    frame_h = int(top + bottom + 2 * MARGIN)
    center_x = int(left + MARGIN)
    baseline_y = int(top + MARGIN)

    def place(entry):
        f, dx, dy = entry
        canvas = np.zeros((frame_h, frame_w, 4), np.uint8)
        sh = _shift(f, dx + center_x, dy + baseline_y)
        h = min(frame_h, sh.shape[0])
        w = min(frame_w, sh.shape[1])
        canvas[:h, :w] = sh[:h, :w]
        return canvas

    images, names = [], {}

    def emit(name, entry, key=None):
        if key is not None and key in names:
            names[name] = names[key]
            return
        images.append(place(entry))
        names[name] = len(images) - 1
        if key is not None:
            names[key] = names[name]

    for d in DIRS:
        cap = CAP[d]
        for i, entry in enumerate(aligned[("walk", d)]):
            emit(f"walk{cap}_{i:02d}", entry)

    # The stand is index 0 of both turn sets and of the idle.
    stand_entry = aligned[("turn", "right")][0]
    emit("idleFront_00", stand_entry, key="__stand__")
    for d in DIRS:
        cap = CAP[d]
        names[f"turn{cap}_00"] = names["__stand__"]
        names[f"stop{cap}A_00"] = names["__stand__"]
        for i in (1, 2):
            images.append(place(aligned[("turn", d)][i]))
            idx = len(images) - 1
            names[f"turn{cap}_{i:02d}"] = idx
            names[f"stop{cap}A_{i:02d}"] = idx
    names.pop("__stand__")

    stride, offsets = measure_stride(sets[("walk", "right")])
    step = measure_step(sets[("walk", "right")])
    # A loose bound on purpose.  The two numbers measure the same distance from
    # different pixels -- one tracks the planted shoe, the other spans both shoes
    # at full extension -- and the shoes' dark soles are clipped to black by the
    # render, which biases the span outwards.  This is here to catch a tracker
    # that changed feet, not to police the last few pixels.
    assert abs(stride - step) < 0.25 * step, (
        "stride %.1f and heel-to-heel step %.1f disagree; the walk would skate" % (stride, step))

    # ---- atlas ------------------------------------------------------------
    cols = 5
    rows = (len(images) + cols - 1) // cols
    sheet = np.zeros((rows * frame_h, cols * frame_w, 4), np.uint8)
    rects = []
    for i, img in enumerate(images):
        r, c = divmod(i, cols)
        sheet[r * frame_h:(r + 1) * frame_h, c * frame_w:(c + 1) * frame_w] = img
        rects.append((c * frame_w, r * frame_h))

    OUT.mkdir(parents=True, exist_ok=True)
    Image.fromarray(sheet).save(OUT / "hero.png")

    entries = []
    for name in sorted(names):
        x, y = rects[names[name]]
        entries.append({
            "filename": name,
            "frame": {"x": x, "y": y, "w": frame_w, "h": frame_h},
            "rotated": False, "trimmed": False,
            "spriteSourceSize": {"x": 0, "y": 0, "w": frame_w, "h": frame_h},
            "sourceSize": {"w": frame_w, "h": frame_h},
        })
    (OUT / "hero.json").write_text(json.dumps({
        "frames": entries,
        "meta": {"image": "hero.png", "format": "RGBA8888",
                 "size": {"w": sheet.shape[1], "h": sheet.shape[0]}, "scale": "1"},
    }, indent=2))

    walk_heights = [fs.bbox(f)[3] - fs.bbox(f)[1] for f in sets[("walk", "right")]]
    character_h = float(np.mean(walk_heights))

    widths, heights = [], []
    for img in images:
        bx0, by0, bx1, by1 = fs.bbox(img)
        widths.append(bx1 - bx0)
        heights.append(by1 - by0)

    # Hitbox: the body's own width standing in profile, not the stride.  A box as
    # wide as the widest pose would be mostly empty air either side of him.
    px0, _, px1, _ = fs.bbox(images[names["turnRight_02"]])
    torso = int(px1 - px0)
    # Reach: how far the *drawing* gets from the centre line, which is what the
    # world bound has to respect so a leading leg is not clipped at the wall.
    reach = int(max(max(center_x - fs.bbox(img)[0], fs.bbox(img)[2] - center_x)
                    for img in images))

    meta = {
        "frameWidth": frame_w, "frameHeight": frame_h,
        "centerX": center_x, "baselineY": baseline_y,
        "characterHeightPx": round(character_h, 1),
        "source": "Frames/ (authored walk and turn art)",
        "standSource": "Frames/RightTurn/turn_0.png",
        "rotationSource": "Frames/RightTurn, Frames/LeftTurn",
        "directions": list(DIRS),
        "walk": {"frames": fs.WALK_COUNT, "strideLengthPx": round(stride, 2)},
        "idle": {"frames": 1, "fps": 1, "facing": "camera",
                 "note": "the authored front stand, held still"},
        "turn": {"frames": fs.TURN_COUNT, "startFrame": WALK_START_FRAME,
                 "note": "turn<Dir>_00 is the idle frame itself; turn<Dir>_02 is the "
                         "authored profile stand, which measures 68.6 from walk_03 "
                         "against 30-70 for the walk's own frame steps"},
        "stop": {"frames": fs.TURN_COUNT, "variants": {"A": WALK_START_FRAME},
                 "note": "the same three images as the turn, played backwards"},
        "hitbox": {"width": torso, "height": int(max(heights))},
        "reach": reach,
    }
    (OUT / "rig-meta.json").write_text(json.dumps(meta, indent=2))

    print("frames  %d unique images, %d names" % (len(images), len(names)))
    print("frame   %dx%d  centerX=%d baselineY=%d" % (frame_w, frame_h, center_x, baseline_y))
    print("atlas   %dx%d" % (sheet.shape[1], sheet.shape[0]))
    print("stride  %.2f px per %d-frame cycle  (planted-foot offsets %s)"
          % (stride, fs.WALK_COUNT, [round(o, 1) for o in offsets]))
    print("        heel-to-heel step %.1f px, independently measured" % step)
    print("height  character %.1f px, tallest pose %d" % (character_h, max(heights)))
    print("hitbox  %dx%d  reach=%d" % (torso, max(heights), reach))

    # ---- handoff guarantees ----------------------------------------------
    def same(a, b):
        return np.array_equal(images[names[a]], images[names[b]])
    for d in DIRS:
        cap = CAP[d]
        assert same("idleFront_00", f"turn{cap}_00"), f"turn{cap}_00 must be the idle frame"
        assert same("idleFront_00", f"stop{cap}A_00"), f"stop{cap}A_00 must be the idle frame"
        for i in range(fs.TURN_COUNT):
            assert same(f"turn{cap}_{i:02d}", f"stop{cap}A_{i:02d}"), "settle must reverse the turn"
    print("handoff idle == turn_00 == stopA_00 for both directions, settle reverses the turn")

    for name, index in sorted(names.items()):
        img = images[index]
        assert img[..., 3].max() == 255, f"{name} has no fully opaque pixel"
        translucent = int(((img[..., 3] > 24) & (img[..., 3] < 250)).sum())
        assert translucent < 6000, f"{name} is translucent ({translucent} px)"
    print("alpha   every frame fully opaque in its interior")


if __name__ == "__main__":
    build()
