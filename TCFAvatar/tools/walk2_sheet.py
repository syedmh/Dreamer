"""Segment and measure `Walking2.png`.

`Walking2.png` is the source of every pose in the turn: the front stand and, more
importantly, the authored *three-quarter* views between front and profile.  Those
intermediate angles are the thing no amount of interpolation can invent, and their
absence is what made the previous turn read as missing frames.

Nothing is used from it until it has been measured, because what it actually
contains is not what its captions claim.  Measured with `walk2_analyse.py` and
`walk2_phase.py`:

* **The walk rows are not a walk cycle.**  Foot spread across `walkRight` runs
  52 56 58 58 58 58 58 61 62 46 46 46 0 0 -- nine near-identical contact poses and
  then the legs closing.  A gait cycle would oscillate.  The rig in
  `build_hero_sprites.py` therefore still synthesises the walk; only the turn is
  taken from here.
* **The captions lie.**  The walk rows are labelled 01..12,14,15,16 but hold only
  fourteen figures -- "15" captions empty space.  Counts come from the art.
* **Figures touch.**  A swinging hand or a shoe tip bridges neighbours, so
  connected components merge them.  Figures are therefore found as runs of
  occupied columns, and a run wider than one pitch is divided by pitch, which
  makes the segmentation self-checking: the pieces must total the expected count.

Run directly for the measurements; import `figures()` for the cut-outs.
"""
from functools import lru_cache
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "Walking2.png"
# Optional 4x reconstruction of the same sheet, written by tools/upscale_walk2.py.
# Segmentation always runs on the pinned original -- only `cutout` reads pixels
# from here, at four times the box, so cell geometry and captions are unaffected.
SRC_X4 = ROOT / "art" / "Walking2-x4.png"
X4_SCALE = 4
# How much of the reconstruction to keep over the head, and the figure-height
# fractions across which it ramps up to full strength on the body.  See `cutout`.
HEAD_SR_WEIGHT = 0.35
HEAD_SR_BAND = (0.13, 0.23)
OUT = ROOT / ".build"

INK = 26              # luma above this counts as a mark of any kind
MIN_FIGURE_H = 60     # caption glyphs are ~14 px tall, figures ~180
MIN_FILL = 0.12       # the idle panel border encloses a big box but fills ~1 % of it
COL_GAP = 2           # columns of clear black that separate two figures
MIN_SPAN_PX = 800

# Bands of art, top to bottom, and the groups in each.  Counted off the art, not
# off the captions.  Groups within a band are separated by the widest gaps.
LAYOUT = [
    [("walkRight", 14)],
    [("walkLeft", 14)],
    [("turnRight", 14), ("idleFront", 4)],
    [("turnLeft", 14), ("idleFrontB", 4)],
]


def _ink():
    a = np.asarray(Image.open(SRC).convert("RGBA")).astype(np.int16)
    return (a[..., 3] > INK) & (a[..., :3].max(axis=2) > INK)


def figure_mask(ink=None):
    """Just the figures: caption glyphs and the idle panel border removed.

    Colour-keying the green captions would be wrong -- the waistcoat is green too.
    Size and solidity separate them safely: glyphs are ~14 px tall against a
    figure's ~180, and the panel border encloses a large box but fills ~1 % of it.
    """
    ink = _ink() if ink is None else ink
    labelled, _ = ndimage.label(ink, structure=np.ones((3, 3), int))
    out = np.zeros_like(ink)
    for i, sl in enumerate(ndimage.find_objects(labelled), start=1):
        sub = labelled[sl] == i
        h, w = sub.shape
        if h >= MIN_FIGURE_H and sub.sum() >= MIN_FILL * h * w:
            out[sl] |= sub
    return out


def _runs(flags, gap):
    runs, start, blank = [], None, 0
    for i, on in enumerate(flags):
        if on:
            start = i if start is None else start
            blank = 0
        elif start is not None:
            blank += 1
            if blank > gap:
                runs.append((start, i - blank))
                start, blank = None, 0
    if start is not None:
        runs.append((start, len(flags) - 1))
    return runs


def _split(spans, profile, count):
    """Divide runs of columns into exactly `count` figures, by pitch."""
    x0, x1 = spans[0][0], spans[-1][1]
    pitch = (x1 - x0 + 1) / count
    cells, total = [], 0
    for a, b in spans:
        k = max(1, int(round((b - a + 1) / pitch)))
        total += k
        step = (b - a + 1) / k
        cells += [(int(a + step * i), int(a + step * (i + 1)) - 1) for i in range(k)]
    if total != count:
        raise SystemExit(f"split gave {total} figures, expected {count} "
                         f"(pitch {pitch:.1f}, spans {spans})")
    return cells


def figures():
    """{row name: [figure, ...]} left to right, each with sheet-space bounds."""
    art = figure_mask()
    bands = [(a, b) for a, b in _runs(art.any(axis=1), 30) if b - a > 120]
    if len(bands) != len(LAYOUT):
        raise SystemExit(f"expected {len(LAYOUT)} bands, found {len(bands)}")

    out = {}
    for (y0, y1), groups in zip(bands, LAYOUT):
        strip = art[y0:y1 + 1]
        profile = strip.sum(axis=0)
        spans = [s for s in _runs(profile > 0, COL_GAP)
                 if profile[s[0]:s[1] + 1].sum() > MIN_SPAN_PX]

        # Groups within a band are separated by the widest gaps: the idle panel
        # stands well clear of the transition row beside it.
        gaps = sorted(((spans[i + 1][0] - spans[i][1], i + 1) for i in range(len(spans) - 1)),
                      reverse=True)[:len(groups) - 1]
        bounds = [0] + sorted(i for _, i in gaps) + [len(spans)]

        for k, (name, count) in enumerate(groups):
            group = spans[bounds[k]:bounds[k + 1]]
            items = []
            for index, (cx0, cx1) in enumerate(_split(group, profile, count)):
                sub = strip[:, cx0:cx1 + 1]
                ys = np.nonzero(sub.any(axis=1))[0]
                xs = np.nonzero(sub.any(axis=0))[0]
                items.append({
                    "name": f"{name}_{index:02d}",
                    "x0": int(cx0 + xs[0]), "x1": int(cx0 + xs[-1]),
                    "y0": int(y0 + ys[0]), "y1": int(y0 + ys[-1]),
                    "cell": (int(cx0), int(cx1)),
                    "band": (int(y0), int(y1)),
                })
            out[name] = items
    return out


@lru_cache(maxsize=2)
def _sheet_rgb(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(np.float32)


@lru_cache(maxsize=1)
def _announce(have_sr):
    """Say once whether the turn is being baked from super-resolved pixels.

    Without this the fallback is silent: the bake succeeds either way, and the
    only symptom of a missing `Walking2-x4.png` is that the turn frames come out
    soft again -- which is exactly the defect this pass exists to fix.
    """
    if have_sr:
        print(f"walk2   super-resolved source in use ({SRC_X4.name}, x{X4_SCALE}), "
              f"head held at {HEAD_SR_WEIGHT:.2f}")
    else:
        print(f"walk2   WARNING {SRC_X4.name} missing -- turn frames will be baked "
              f"from the {SRC.name} original and will look soft. "
              f"Run tools/upscale_walk2.py to restore.")


def _key(src, fig, k):
    """Lift one figure off the black sheet, from a sheet scaled by `k`."""
    box = src[fig["y0"] * k:(fig["y1"] + 1) * k, fig["x0"] * k:(fig["x1"] + 1) * k]
    # The sheet is flat black and the art is anti-aliased, so luma *is* the matte.
    # Keeping it soft rather than thresholding matters: a hard cut leaves a jagged
    # silhouette once the frame is scaled.
    luma = box.max(axis=2)
    alpha = np.clip((luma - 6.0) / 22.0, 0.0, 1.0)
    # Un-premultiply so edge pixels keep their colour instead of darkening.
    colour = np.clip(box / np.maximum(alpha[..., None], 1e-3), 0, 255)
    return np.dstack([colour, alpha * 255]).astype(np.float32)


def cutout(fig, pad=0):
    """The figure's pixels with a soft alpha matte lifted off the black sheet.

    Pixels come from `SRC_X4` when it has been built, so the three-quarter turn
    views arrive as ~720px figures instead of ~180px ones.  The matte is keyed at
    whatever resolution the pixels came at -- the thresholds are luma values, not
    lengths -- so the silhouette is anti-aliased at the higher sample count rather
    than being an upscale of one that was already quantised.

    **The head is deliberately held back from full reconstruction.**  The figure is
    ~180px, so the head is only about 30px and the eyes two or three pixels across.
    Real-ESRGAN cannot recover a face from that; it invents one, and the invention
    is worse than the blur -- at full strength the eyes come out as dark hollow
    sockets and the mouth as a protruding smear, which reads as a different and
    rather ghoulish man for the length of the turn.  Rendering the same frame at
    0, 0.35, 0.6 and 1.0 showed 0.35 is where the hair, beard edge and collar
    sharpen up while the face still reads as a face.  Clothing has no such limit,
    so below the shoulders the reconstruction runs at full strength -- that is
    where it recovers the crest badge, the buttons and the shoe detail.
    """
    lo = _key(_sheet_rgb(SRC), fig, 1)
    if SRC_X4.exists():
        _announce(True)
        hi = _key(_sheet_rgb(SRC_X4), fig, X4_SCALE)
        up = np.asarray(Image.fromarray(lo.astype(np.uint8), "RGBA").resize(
            (hi.shape[1], hi.shape[0]), Image.Resampling.LANCZOS)).astype(np.float32)
        y = np.arange(hi.shape[0], dtype=np.float32)[:, None, None] / hi.shape[0]
        ramp = (np.clip((y - HEAD_SR_BAND[0]) / (HEAD_SR_BAND[1] - HEAD_SR_BAND[0]),
                        0.0, 1.0) * (1.0 - HEAD_SR_WEIGHT) + HEAD_SR_WEIGHT)
        rgba = up * (1.0 - ramp) + hi * ramp
        k = X4_SCALE
    else:
        _announce(False)
        rgba, k = lo, 1

    rgba = np.clip(rgba, 0, 255).astype(np.uint8)
    if pad:
        rgba = np.pad(rgba, ((pad * k, pad * k), (pad * k, pad * k), (0, 0)))
    return Image.fromarray(rgba, "RGBA")


def foot_span(fig, art=None, band=14):
    """Left and right extent of whatever is touching the ground, in sheet columns.

    Used to read the stride off the art: across the cycle the contact foot must
    stay put in world space, so its position in the frame is what the character's
    root motion has to cancel.
    """
    art = figure_mask() if art is None else art
    sub = art[fig["y1"] - band + 1:fig["y1"] + 1, fig["x0"]:fig["x1"] + 1]
    xs = np.nonzero(sub.any(axis=0))[0]
    return int(fig["x0"] + xs[0]), int(fig["x0"] + xs[-1])


def main():
    figs = figures()
    art = figure_mask()
    print(f"source {SRC.name}\n")
    for name, items in figs.items():
        h = [f["y1"] - f["y0"] + 1 for f in items]
        w = [f["x1"] - f["x0"] + 1 for f in items]
        g = {f["y1"] for f in items}
        print(f"{name:11s} n={len(items):2d} h={min(h)}..{max(h)} w={min(w)}..{max(w)} "
              f"ground={sorted(g)}")

    print("\nfoot spans (ground contact), width, and its span across the cycle:")
    for name in ("walkRight", "walkLeft"):
        spans = [foot_span(f, art) for f in figs[name]]
        widths = [b - a + 1 for a, b in spans]
        rel = [(a - f["cell"][0], b - f["cell"][0])
               for (a, b), f in zip(spans, figs[name])]
        print(f"  {name}: widths={widths}")
        print(f"    left edge rel. to cell: {[a for a, _ in rel]}")

    OUT.mkdir(exist_ok=True)
    for name, items in figs.items():
        tiles = [cutout(f) for f in items]
        tw, th = max(t.width for t in tiles), max(t.height for t in tiles)
        sheet = Image.new("RGBA", (tw * len(tiles), th), (24, 24, 28, 255))
        for i, t in enumerate(tiles):
            sheet.alpha_composite(t, (i * tw + (tw - t.width) // 2, th - t.height))
        sheet.save(OUT / f"cut-{name}.png")
    print(f"\nwrote cut-*.png to {OUT}")


if __name__ == "__main__":
    main()
