"""Keying and alignment for the authored art in ``Frames/``.

The supplied art is six walk poses and three turn poses per direction, rendered
at 512x864 over solid black with no alpha and no baked contact shadow (verified:
every pixel below the feet is exactly 0, so nothing has to be separated from a
shadow).

Keying off luminance alone does not work here.  The shoes and the hair are very
dark -- the near shoe's median is 63 and its darkest solid interior sits under
10 -- so a plain ``alpha = lum / knee`` ramp eats holes straight through them.
The background is instead found by flood-filling black *inward from the border*:
a black pixel that the border can reach is background, a black pixel enclosed by
the figure is shadow inside a shoe and stays opaque.  Only the one-pixel shell
where the two meet keeps a soft ramp, so edges stay anti-aliased without the
interior ever going translucent.
"""

from __future__ import annotations

import functools
import pathlib

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = pathlib.Path(__file__).resolve().parent.parent
FRAMES = ROOT / "Frames"

WALK_DIRS = {"right": "RightWalk", "left": "LeftWalk"}
TURN_DIRS = {"right": "RightTurn", "left": "LeftTurn"}
WALK_COUNT = 6
TURN_COUNT = 3

_BG_FLOOR = 6.0     # luminance at or below which a border-connected pixel is background
_EDGE_KNEE = 26.0   # luminance at which the anti-aliased rim reaches full opacity


def _largest(mask, keep_fraction=0.002):
    """The figure: the biggest component plus any sizeable detached fragment.

    Taking only the biggest component drops pieces the anti-aliased edge has
    pinched off -- a shoe tip, a fingertip -- which on this art costs a few
    hundred lit pixels including some at full brightness.  Anything above a small
    fraction of the main body is kept.
    """
    labels, count = ndimage.label(mask, np.ones((3, 3)))
    if count <= 1:
        return mask
    sizes = ndimage.sum(mask, labels, range(1, count + 1))
    floor = sizes.max() * keep_fraction
    keep = [i + 1 for i, size in enumerate(sizes) if size >= floor]
    return np.isin(labels, keep)


def key(rgb):
    """Return (colour float32, alpha float32) for one frame shot over black."""
    rgb = rgb.astype(np.float32)
    lum = rgb.max(axis=2)

    # Background is black that the border can reach.  Enclosed black is figure.
    dark = lum <= _BG_FLOOR
    labels, count = ndimage.label(dark, np.ones((3, 3)))
    if count:
        edge = np.concatenate([labels[0], labels[-1], labels[:, 0], labels[:, -1]])
        outside = np.unique(edge)
        background = np.isin(labels, outside[outside > 0])
    else:
        background = np.zeros(lum.shape, bool)

    figure = _largest(~background)
    figure = ndimage.binary_fill_holes(figure)

    # Solid interior: everything more than one pixel in from the background.
    interior = ndimage.binary_erosion(figure, np.ones((3, 3)), iterations=2)
    rim = figure & ~interior

    alpha = np.zeros(lum.shape, np.float32)
    alpha[interior] = 1.0
    alpha[rim] = np.clip(lum[rim] / _EDGE_KNEE, 0.35, 1.0)

    # Un-premultiply the rim by borrowing colour from the nearest solid pixel;
    # dividing by a small alpha there would explode into fringe speckle.
    if interior.any():
        _, index = ndimage.distance_transform_edt(~interior, return_indices=True)
        borrowed = rgb[index[0], index[1]]
        colour = np.where(interior[..., None], rgb, borrowed)
    else:
        colour = rgb
    return colour, alpha


def _rgba(path):
    rgb = np.asarray(Image.open(path).convert("RGB"))
    colour, alpha = key(rgb)
    out = np.concatenate([colour, (alpha * 255.0)[..., None]], axis=2)
    return np.clip(out, 0, 255).astype(np.uint8)


@functools.lru_cache(maxsize=None)
def walk(direction):
    """The six authored walk poses, keyed, in cycle order."""
    folder = FRAMES / WALK_DIRS[direction]
    return tuple(_rgba(folder / f"walk_{i:02d}.png") for i in range(WALK_COUNT))


@functools.lru_cache(maxsize=None)
def turn(direction):
    """The three authored turn poses, keyed: front, three-quarter, profile."""
    folder = FRAMES / TURN_DIRS[direction]
    return tuple(_rgba(folder / f"turn_{i}.png") for i in range(TURN_COUNT))


def ground(rgba, floor=24):
    """Lowest opaque row -- the plane the planted foot rests on."""
    rows = np.nonzero((rgba[..., 3] > floor).any(axis=1))[0]
    return int(rows[-1])


def bbox(rgba, floor=24):
    mask = rgba[..., 3] > floor
    rows = np.nonzero(mask.any(axis=1))[0]
    cols = np.nonzero(mask.any(axis=0))[0]
    return int(cols[0]), int(rows[0]), int(cols[-1]) + 1, int(rows[-1]) + 1
