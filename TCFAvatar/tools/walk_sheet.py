"""Read the hero rig out of ``Walking.png``.

``Walking.png`` is the side-profile art supplied for this character.  It is laid out as a
"WALK RIGHT" row and a "WALK LEFT" row of figures over pure black.  It is *not* a usable
walk cycle -- every figure in a row is a variation on the same mid-stride pose, with no
passing pose and no leg alternation -- but each row does contain a clean neutral standing
figure, and that is what this module is for: it extracts one true profile figure per
direction to be used as rig art.

Three things about the sheet are measured rather than assumed:

* **The keying is trivial.**  The art is composited over black, background luminance is
  <= 1 and interior luminance is >= 24, so ``alpha = lum / 20`` clipped to [0, 1] is an
  exact soft key.  Colour is un-premultiplied against black, and rim colour is taken from
  the nearest fully-interior pixel so the amplification on a near-zero alpha cannot
  produce fringe.

* **The printed "01..16" labels are wrong.**  Their pitch shrinks from 104px to 72px and
  drifts off the figures; the rows actually hold 14 and 15 figures.  They are ignored.

* **Figures overlap at the shoes**, so column gaps cannot segment them.  Torsos never
  overlap, so components are seeded in the top 45% of the row and every ink pixel is then
  assigned to its nearest seed.  The seam lands midway between two torsos, well outside
  either figure's feet.
"""
import numpy as np
from PIL import Image
from scipy import ndimage
from pathlib import Path

SHEET = Path(__file__).resolve().parents[1] / "Walking.png"

# Row content bands, measured off the sheet.
ROW_BANDS = {"right": (108, 433), "left": (581, 905)}

# The neutral figure chosen out of each row: legs together and straight, arm hanging,
# one clean shoe silhouette.  These are rig sources, not animation frames.
RIG_INDEX = {"right": 0, "left": 14}

# Column at which the near shoe separates from the far one, read off each rig source.
# The two shoes touch, so this is a pinned measurement like RIG_INDEX rather than a
# heuristic: near shoe is everything on the leading side of this column.
FOOT_CUT = {"right": 31, "left": 44}

# Direction of travel in source x.
FORWARD = {"right": 1.0, "left": -1.0}

_ALPHA_KNEE = 20.0      # luminance at which the soft key saturates
_SEED_BAND = 0.45       # fraction of row height in which torsos are guaranteed disjoint
_SEED_MIN = 1500        # px; smaller components are labels and speckle


def _key(rgb):
    """Soft luminance key plus un-premultiply, with rim colour from the interior."""
    lum = rgb.max(axis=2)
    alpha = np.clip(lum / _ALPHA_KNEE, 0.0, 1.0)
    interior = alpha > 0.85
    if interior.any():
        _, index = ndimage.distance_transform_edt(~interior, return_indices=True)
        colour = rgb[index[0], index[1]]
    else:
        colour = rgb
    # Inside the silhouette the observed colour is already correct; only the soft rim,
    # where dividing by a small alpha would explode, borrows from its nearest neighbour.
    solid = alpha > 0.6
    colour = np.where(solid[..., None], rgb, colour)
    return colour, alpha


def extract_row(name):
    """Every figure in one labelled row, left to right, as RGBA uint8 arrays."""
    y0, y1 = ROW_BANDS[name]
    rgb = np.asarray(Image.open(SHEET).convert("RGB")).astype(np.float32)[y0:y1 + 1]
    colour, alpha = _key(rgb)
    ink = alpha > 0.15

    band = np.zeros_like(ink)
    band[:int(ink.shape[0] * _SEED_BAND)] = True
    labels, count = ndimage.label(ink & band, np.ones((3, 3)))
    sizes = ndimage.sum(ink & band, labels, range(1, count + 1))
    keep = [i + 1 for i, size in enumerate(sizes) if size > _SEED_MIN]
    keep.sort(key=lambda i: float(np.nonzero(labels == i)[1].mean()))

    seeds = np.zeros(ink.shape, np.int32)
    for rank, index in enumerate(keep, 1):
        seeds[labels == index] = rank
    _, nearest = ndimage.distance_transform_edt(seeds == 0, return_indices=True)
    owner = np.where(ink, seeds[nearest[0], nearest[1]], 0)

    figures = []
    for rank in range(1, len(keep) + 1):
        mine = np.where(owner == rank, alpha, 0.0)
        rows = np.nonzero(mine.max(axis=1) > 0.15)[0]
        cols = np.nonzero(mine.max(axis=0) > 0.15)[0]
        box = (rows[0], rows[-1] + 1, cols[0], cols[-1] + 1)
        rgba = np.concatenate([colour, (mine * 255.0)[..., None]], axis=2)
        figures.append(np.clip(rgba[box[0]:box[1], box[2]:box[3]], 0, 255).astype(np.uint8))
    return figures


# ---------------------------------------------------------------------------
# Landmarks.
# ---------------------------------------------------------------------------
def _runs(row):
    out, start = [], None
    for index, value in enumerate(np.append(row, False)):
        if value and start is None:
            start = index
        elif not value and start is not None:
            out.append((start, index - 1))
            start = None
    return out


def _largest(mask):
    labels, count = ndimage.label(mask, np.ones((3, 3)))
    if count <= 1:
        return mask
    sizes = ndimage.sum(mask, labels, range(1, count + 1))
    return labels == (1 + int(np.argmax(sizes)))


def _palette(rgba):
    colour = rgba[..., :3].astype(np.float32)
    solid = rgba[..., 3] > 128
    red, green, blue = colour[..., 0], colour[..., 1], colour[..., 2]
    high, low = colour.max(axis=2), colour.min(axis=2)
    saturation = (high - low) / np.maximum(high, 1.0)
    return {
        "solid": solid,
        # The waistcoat is the only strongly green thing on the figure, which makes it a
        # reliable marker for where the torso is and where the sleeve is not.
        "green": solid & (green > red + 8) & (green > blue + 8),
        "skin": solid & (red > green + 18) & (green > blue + 6) & (high > 110) & (saturation > 0.18),
        "dark": solid & (high < 95),
    }


def measure(rgba, forward, foot_cut_x):
    """Rig landmarks for one profile figure, in that figure's own pixels."""
    height, width, _ = rgba.shape
    parts = _palette(rgba)
    solid, green, skin, dark = parts["solid"], parts["green"], parts["skin"], parts["dark"]

    rows_with_green = np.nonzero(green.sum(axis=1) > 6)[0]
    shoulder_y, waist_y = int(rows_with_green[0]), int(rows_with_green[-1])

    widths = np.zeros(height, dtype=np.float32)
    for y in range(height):
        cols = np.nonzero(solid[y])[0]
        widths[y] = (cols[-1] - cols[0] + 1) if cols.size else 0.0

    # The kurta flares to its widest just above its hem, then the silhouette drops to
    # trouser width.  That step is the hem.
    search_hi = waist_y + int(0.62 * (height - waist_y))
    skirt_y = waist_y + int(np.argmax(widths[waist_y:search_hi]))
    threshold = 0.80 * widths[skirt_y]
    hem_y = next((y for y in range(skirt_y, height) if widths[y] < threshold), skirt_y + 8)

    # Both shoes are present and touching; ``foot_cut_x`` splits them.
    shoe_rows = np.nonzero(dark[hem_y:].sum(axis=1) > 8)[0]
    foot_top = hem_y + int(shoe_rows[0])

    xs = np.arange(width)[None, :]
    near_foot = solid & ((xs - foot_cut_x) * forward >= 0)
    near_foot[:foot_top] = False
    near_foot = _largest(near_foot)
    near_rows = np.nonzero(near_foot.any(axis=1))[0]
    ankle_y = int(near_rows[0])
    ground_y = int(near_rows[-1])

    # Sleeve: on the trailing side of the waistcoat, ending in the bare hand.
    cut = np.zeros(height, dtype=np.float32)
    last = None
    for y in range(height):
        runs = _runs(green[y])
        if runs:
            span = max(runs, key=lambda r: r[1] - r[0])
            if span[1] - span[0] >= 8:
                last = (span[0] - 1) if forward > 0 else (span[1] + 1)
        cut[y] = last if last is not None else (0 if forward > 0 else width - 1)

    hand = skin.copy()
    hand[:waist_y - 4] = False
    hand[int(height * 0.72):] = False
    hand = _largest(hand) if hand.any() else hand
    hand_rows = np.nonzero(hand.any(axis=1))[0]
    hand_cols = np.nonzero(hand.any(axis=0))[0]
    hand_bottom = int(hand_rows[-1]) + 3
    # Below the waistcoat there is no colour contrast, so the sleeve boundary is held at
    # whatever still admits the whole hand.
    below = slice(waist_y + 1, height)
    edge = float(hand_cols[-1] + 2) if forward > 0 else float(hand_cols[0] - 2)
    cut[below] = max(cut[waist_y], edge) if forward > 0 else min(cut[waist_y], edge)

    torso_band = solid[shoulder_y:waist_y]
    axis_x = float(np.nonzero(torso_band)[1].mean())

    leg_cols = np.nonzero(solid[ankle_y - 6:ankle_y].any(axis=0))[0]
    leg_ankle_x = float((leg_cols[0] + leg_cols[-1]) * 0.5)
    foot_anchor_cols = np.nonzero(near_foot[ankle_y:ankle_y + 6].any(axis=0))[0]
    foot_anchor_x = float((foot_anchor_cols[0] + foot_anchor_cols[-1]) * 0.5)

    sleeve_cols = np.nonzero((solid & ((xs - cut[:, None]) * forward <= 0))[shoulder_y + 6])[0]

    return {
        "head_top": 0,
        "shoulder_y": shoulder_y,
        "waist_y": waist_y,
        "hem_y": int(hem_y),
        "ankle_y": ankle_y,
        "ground_y": ground_y,
        "foot_cut_x": foot_cut_x,
        "arm_cut": cut,
        "axis_x": axis_x,
        "leg_ankle_x": leg_ankle_x,
        "foot_anchor_x": foot_anchor_x,
        "shoulder_pt": (float((sleeve_cols[0] + sleeve_cols[-1]) * 0.5), float(shoulder_y + 10)),
        "hand_pt": (float(hand_cols.mean()), float(hand_rows.mean())),
        "hand_bottom": hand_bottom,
        "height": ground_y + 1,
    }


def rig_source(direction):
    """The chosen neutral figure for one direction, cropped to head-top .. sole."""
    figure = extract_row(direction)[RIG_INDEX[direction]]
    landmarks = measure(figure, FORWARD[direction], FOOT_CUT[direction])
    return figure, landmarks


if __name__ == "__main__":
    for name in ("right", "left"):
        rgba, lm = rig_source(name)
        print(f"{name}: {rgba.shape[1]}x{rgba.shape[0]}")
        for key in ("shoulder_y", "waist_y", "hem_y", "ankle_y", "ground_y", "height",
                    "foot_cut_x", "axis_x", "leg_ankle_x", "foot_anchor_x",
                    "shoulder_pt", "hand_pt", "hand_bottom"):
            print(f"   {key:14s} {lm[key]}")
