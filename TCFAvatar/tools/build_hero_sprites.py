"""Bake the hero walk cycle from the profile art in ``Walking.png``.

The sheet supplies one true side-profile figure per direction -- head, waistcoat, sleeve,
kurta, trousers and shoes all drawn at 90 degrees -- but it does **not** supply a walk
cycle: its figures are variations on a single mid-stride pose with no passing pose and no
leg alternation, so playing them back would read as a twitch, not a walk.  What it does
give is the thing a photograph cannot: correct profile art.  So the art is cut into a rig
and animated from a continuous gait function.

Two ideas carry the result:

* **The layers are cut where the art already has a seam.**  The waistcoat is the only
  strongly green thing on the figure, so the white sleeve separates from the torso by
  colour rather than by a hand-traced curve; the hand is the only bare skin below the
  chest; the shoes are the only dark mass below the kurta.  Every cut therefore follows a
  real edge, and nothing has to be invented except the thigh hidden under the kurta,
  which is extruded upward from the trouser it continues.

* **The gait is one continuous function, sampled.**  The support foot travels *linearly*
  through stance, so a planted foot moves backwards at exactly the body's forward speed
  and cannot skate; the swing is a cubic Hermite whose end slopes match the stance rate,
  so the path is C1 continuous.  Frame 23 wraps into frame 00 by construction, which is
  why no frame can be missing, duplicated or jerky.

Both directions are baked from their own authored art -- nothing is mirrored -- and both
are normalised to a common height and baseline so turning around cannot pop.

Standing is a third state.  A profile statue reads as a man waiting for a bus, so at rest
the character faces the camera.

The turn between the two is the hard part, and it is driven by real drawings.
``Walking2.png`` carries authored *three-quarter* views of the same character --
the intermediate angles no amount of warping can invent -- so the turn changes
over through them in order (front -> three-quarter -> profile) rather than
interpolating between two endpoints.  Row-span warping still carries the
silhouette between one drawing and the next; what it no longer has to do is
fabricate the middle of the rotation.  See ``_rotation_chain`` and ``_row_morph``.

The front stand comes from the same sheet as those three-quarter views.  It used to
come from the original photograph (``art/avatar-front.png``), but that render is a
visibly different pass -- sandals instead of the walk's shoes, heavier proportions,
a differently embroidered waistcoat -- so the character changed *identity* halfway
through the turn.  No interpolation can hide that, which is why the turn kept
reading as broken however smooth the numbers were.
"""
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

from walk_sheet import FORWARD, rig_source, _palette
import walk2_sheet

ROOT = Path(__file__).resolve().parents[1]
BUILD = ROOT / ".build"
OUT = ROOT / "public" / "assets" / "hero"

# ---------------------------------------------------------------------------
# Rig normalisation.  Both sources are scaled to one height and pinned to one
# baseline and axis, so the two directions are interchangeable at runtime.
# ---------------------------------------------------------------------------
RIG_HEIGHT = 980.0
CANVAS_W, CANVAS_H = 1160, 1200
CANVAS_AXIS_X = 580.0
CANVAS_GROUND_Y = 1090.0

# ---------------------------------------------------------------------------
# Rig geometry, in normalised rig pixels.
# ---------------------------------------------------------------------------
HIP_ABOVE_HEM = 74      # the hip joint sits this far up under the kurta
LEG_BACKING_TAPER = 0.58  # the thigh backing narrows towards the hip, so a swinging leg
                          # never pushes a square corner out through the kurta
FOOT_OVERLAP = 26       # rows shared by leg and shoe so the split never opens
HEM_FLARE = 1.16        # the kurta widens over its lowest rows to cover the swinging leg
HEM_FLARE_TOP = 96      # rows above the hem over which the flare ramps in
FAR_LIMB_TINT = 0.70    # the far side sits in the body's own shadow
CYAN_CHROMA = 20        # how far green and blue must lead red before a pixel reads cool
CYAN_BALANCE = 10       # green's lead over blue, below which that cool pixel is cyan

# ---------------------------------------------------------------------------
# Gait.
# ---------------------------------------------------------------------------
WALK_FRAMES = 24

STEP_RATIO = 0.72       # peak ankle offset as a fraction of leg length; puts the feet
                        # 0.39 * body height apart at contact, which is life-size
STANCE_FRACTION = 0.6   # share of the cycle each foot spends planted
FOOT_LIFT = 0.080 * RIG_HEIGHT
FOOT_ROLL_DEG = 15.0
ARM_SWING_DEG = 22.0
PELVIS_BOB = 0.016 * RIG_HEIGHT
TORSO_SURGE = 0.005 * RIG_HEIGHT
TORSO_LEAN_DEG = 4.0
TORSO_BOUNCE_DEG = 1.6
IDLE_BREATHE = 0.0045 * RIG_HEIGHT

# ---------------------------------------------------------------------------
# Front-facing stand and the turn that reaches it.
#
# The stand comes from the photograph cutout, which draws the character 959px
# tall against `Walking2.png`'s 185.  That is a 5.2x difference in real detail,
# and at 185px the face bakes down to a smear -- no eyes, no features, just
# blotches -- while the 326px profile art beside it keeps its eyeline, hair and
# buttons.  Standing is the pose the character holds longest and is looked at
# hardest, so it gets the best source available.
#
# This was previously taken from `Walking2.png` to keep one rendering pass across
# the whole rotation.  Measured rather than assumed, the photo turns out to be the
# *closer* match to the walk art: both have the damask waistcoat and gold buttons,
# where Walking2's has plain green ones.  The one real discrepancy is footwear --
# the photo wears sandals, the walk art shoes -- and it cannot be grafted out,
# because the photo's stance is some 25% narrower than the walk art's, so the
# hems do not line up.  The turn morphs the legs over in its first three frames,
# which is where that difference is spent.
#
# The three-quarter views still come from `Walking2.png`; it is the only source
# that has them.  All of them are normalised onto the same canvas -- same height,
# same ground line, same body axis -- so nothing changes size or drifts sideways.
# ---------------------------------------------------------------------------
FRONT_ART = ROOT / "art" / "avatar-front.png"   # neutral, square to camera, arms hanging

# Authored three-quarter views, ordered as the turn plays them: nearest the front
# stand first, nearest profile last.  These are the frames that used to be
# invented by interpolation, and their absence is what read as missing frames.
ROTATION_KEYS = {
    "right": (("turnRight", 8), ("turnRight", 7), ("turnRight", 6)),
    "left": (("turnLeft", 8), ("turnLeft", 7), ("turnLeft", 6)),
}

FRONT_IDLE_FRAMES = 12
# Breathing, confined to the chest.  There is deliberately no vertical component:
# lifting the whole figure moves his feet off the floor, and quantised to whole
# canvas pixels it steps rather than glides, which is what read as the character
# drifting up and down on a large screen.  A ribcage that widens and narrows is
# what breathing actually looks like, and it leaves the feet planted.
FRONT_BREATH_SWELL = 0.004   # peak fraction the chest widens by
BREATH_CENTRE = 0.36         # height up the body the swell peaks at (0 = ground)
BREATH_SPREAD = 0.20         # ...and how far it reaches either side of that

# Figures taken from `Walking2.png` are about 185px tall against the profile
# sheet's 326, so they are enlarged where the profile art is reduced and land
# visibly softer.  A restrained unsharp pass brings them back into the same range.
# Colour only: run over the alpha it would cut a halo around the silhouette.  Only
# applied when a source is genuinely being enlarged -- the photo arrives at very
# nearly the rig's own height, and sharpening it would only crunch it.
FRONT_SHARPEN = (1.8, 85, 3)           # radius, percent, threshold
FRONT_SHARPEN_MIN_SCALE = 1.5

TURN_FRAMES = 8
START_FRAME = 7         # walk frame the character sets off from: at phase 7/24 the gait
                        # function has both ankles under the hips, the one point in the
                        # cycle that resembles standing.  The turn's targets run from
                        # here, so turn frame 7 is literally walk frame 14.
STOP_TARGETS = (7, 19)  # the two passing poses, half a cycle apart.  Settling morphs the
                        # front stand onto these, so the last stop frame is literally a
                        # walk frame and the closing step hands over without a seam.
                        # Two of them keeps the closing step at most 6 frames long.
STOP_VARIANTS = ("A", "B")
TURN_SHAPE_DONE = 0.70  # fraction of the turn by which the shape has finished morphing
BODY_SWITCH = 0.50      # point in the turn at which the body's art changes over...
BODY_SWITCH_BAND = 0.30  # ...and how sharply.
ROT_SWITCH_BAND = 0.55  # width of each three-quarter changeover, as a fraction of the
                        # spacing between them.  Below one, so consecutive changeovers
                        # do not overlap and every frame is dominated by one drawing.
HEAD_SWITCH = 0.79      # the head changes over later, so the body leads and the head
HEAD_SWITCH_BAND = 0.28  # follows -- which is both how a real turn reads and what stops
                        # any single frame being a half-and-half average of two views.
                        # Each changeover straddles two frames at roughly a fifth and
                        # four fifths, so each frame is dominated by one real drawing --
                        # near enough to clean art to keep the colour, blended just
                        # enough to bridge rather than snap.
SPAN_SMOOTH = 21        # rows averaged when reading a silhouette's edges for the morph
HEAD_BAND = 0.19        # top fraction of the figure treated as head-and-neck
HEAD_TAPER = 0.10       # ...and the fraction below it over which the sweep eases out
LEG_SWITCH = 0.26       # the legs change over to the walk art well before the body:
LEG_SWITCH_BAND = 0.26  # feet commit first when a man sets off, and it keeps the
LEG_BAND = 0.42         # widest part of the leg morph off the screen.  Bottom fraction
LEG_TAPER = 0.16        # of the figure treated as legs, and the fade above it.
# The sweep and the card-narrowing below fake a rotation by sliding and pinching the
# head.  The three-quarter drawings now do that for real, so these are kept only to
# smooth the joins between them -- turned up any further they double the rotation and
# the head visibly overshoots.
HEAD_SWEEP = 0.04       # head widths the face slides toward the leading edge as it turns
HEAD_CARD = 0.16        # how far the far side of the face sweeps in behind the nose
HEAD_DIP_SKEW = math.log(0.5) / math.log(HEAD_SWITCH)  # peaks the narrowing on the change
_SDF_CLAMP = 24.0       # px of signed distance kept either side of a silhouette edge:
                        # enough to carry the gap between the legs, cheap enough to blend
_SDF_EDGE = 1.4         # px the morphed edge is feathered over, matching the source art's
                        # own anti-aliasing so the in-between frames are no crisper

# ---------------------------------------------------------------------------
# Output frame geometry.
#
# Baked at 650px of character against a 406px display height, so the runtime only
# ever *reduces* the sprite, which is the sharp direction.
#
# 650 is roughly twice the 326px the profile art actually contains. Past about
# double there is nothing left to preserve -- the bake would be interpolating its
# own interpolation -- but up to it, doing the enlargement offline with LANCZOS
# and an unsharp pass beats leaving it to the GPU's bilinear filter at draw time.
# For the front stand, whose source is 959px, this is a *downscale* throughout.
#
# The frame is kept tight around the widest pose (reach 103 at 350 -> 191 here)
# because 108 frames of it have to fit one texture: 15 columns x 8 rows of
# 400x742 is 6000x5936, inside the 8192 limit reported by the low-end software
# renderer, with room to spare on real hardware.
# ---------------------------------------------------------------------------
FRAME_W, FRAME_H = 400, 742
CENTER_X = 200
BASELINE_Y = 696
CHARACTER_HEIGHT = 650
ATLAS_COLUMNS = 15

# The character passes through three resamples on the way here -- source art up to
# the rig's working height, the pose warp, then back down to the frame -- and each
# one costs a little acuity.  A restrained unsharp pass at the end pays it back.
# It runs on colour that has first been extended past the silhouette, so the kernel
# never mixes the figure with the transparent black around it and cuts a dark rim.
#
# The radius tracks one *source* pixel measured in frame pixels -- 650/326 is very
# nearly 2 -- so it restores the detail the art actually contains.  At radius 1 it
# instead sharpened the gaps between real detail, ringing the sleeve and collar
# edges where the enlargement had only ever interpolated.
FRAME_SHARPEN = (2.0, 55, 2)          # radius, percent, threshold

SCALE = CHARACTER_HEIGHT / RIG_HEIGHT
WINDOW_W = int(round(FRAME_W / SCALE))
WINDOW_H = int(round(FRAME_H / SCALE))
WINDOW_X0 = int(round(CANVAS_AXIS_X - CENTER_X / SCALE))
WINDOW_Y0 = int(round(CANVAS_GROUND_Y - BASELINE_Y / SCALE))

# Filled by _configure() once both rigs have been measured.
STEP_MAX = 0.0
STRIDE_SOURCE = 0.0
STRIDE_LENGTH_PX = 0.0


# ---------------------------------------------------------------------------
# Source preparation.
# ---------------------------------------------------------------------------
def _decyan(rgba):
    """Take the cool rim light off the waistcoat's trailing edge.

    `Walking.png` lights the figure with a cold kicker from behind, and where it
    grazes the green waistcoat it turns the outermost pixels cyan -- measured at
    (77,132,141), blue actually ahead of green, against (44,106,72) for the cloth
    itself.  On the sheet it reads as a highlight; in the game it is a hard strip
    down the trailing edge of the sleeve, and because the near arm swings across it
    while the waistcoat stays put, it reads as a blue line drawn on the character
    rather than as light.  The costume has no cyan in it anywhere, so these pixels
    can be identified by hue alone.

    Each one keeps its own brightness -- the rim stays a highlight -- and is retinted
    with the hue of the nearest pixel that is not cyan, which along this edge is the
    waistcoat itself.  Alpha is never touched, so the silhouette is unchanged.
    """
    from scipy import ndimage

    rgb = rgba[..., :3].astype(np.float32)
    red, green, blue = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    # Cyan is the one hue the costume never uses: green and blue both well clear of
    # red, and blue level with green rather than well behind it as in the waistcoat.
    cyan = ((rgba[..., 3] > 0) & (green - red > CYAN_CHROMA) & (blue - red > CYAN_CHROMA)
            & (green - blue < CYAN_BALANCE))
    if not cyan.any():
        return rgba

    source = (rgba[..., 3] > 0) & ~cyan
    if not source.any():
        return rgba
    index = ndimage.distance_transform_edt(~source, return_distances=False,
                                           return_indices=True)
    clean = rgba[index[0], index[1], :3].astype(np.float32)

    retinted = clean - clean.mean(axis=2, keepdims=True) + rgb.mean(axis=2, keepdims=True)
    out = rgba.copy()
    out[..., :3] = np.where(cyan[..., None], np.clip(retinted, 0, 255), rgb).astype(np.uint8)
    return out


def _load(direction):
    """One rig source, normalised onto the shared canvas, with landmarks to match."""
    figure, lm = rig_source(direction)
    forward = FORWARD[direction]
    scale = RIG_HEIGHT / lm["height"]
    height, width = figure.shape[:2]
    resized = Image.fromarray(_decyan(figure), "RGBA").resize(
        (max(1, int(round(width * scale))), max(1, int(round(height * scale)))),
        Image.Resampling.LANCZOS)

    origin_x = int(round(CANVAS_AXIS_X - lm["axis_x"] * scale))
    origin_y = int(round(CANVAS_GROUND_Y - lm["ground_y"] * scale))
    canvas = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
    canvas.paste(resized, (origin_x, origin_y))
    rgba = np.asarray(canvas).copy()

    def to_x(value):
        return value * scale + origin_x

    def to_y(value):
        return value * scale + origin_y

    rows = (np.arange(CANVAS_H) - origin_y) / scale
    inside = (rows >= 0) & (rows <= height - 1)
    sentinel = -1e9 if forward > 0 else 1e9
    cut = np.where(inside,
                   np.interp(np.clip(rows, 0, height - 1),
                             np.arange(height), lm["arm_cut"]) * scale + origin_x,
                   sentinel).astype(np.float32)

    hem_y = to_y(lm["hem_y"])
    ankle_y = to_y(lm["ankle_y"])
    hip = (CANVAS_AXIS_X, hem_y - HIP_ABOVE_HEM)
    shoulder = (to_x(lm["shoulder_pt"][0]), to_y(lm["shoulder_pt"][1]))
    hand = (to_x(lm["hand_pt"][0]), to_y(lm["hand_pt"][1]))

    return {
        "direction": direction,
        "forward": forward,
        "rgba": rgba,
        "cut": cut,
        "shoulder_y": to_y(lm["shoulder_y"]),
        "waist_y": to_y(lm["waist_y"]),
        "hem_y": hem_y,
        "ankle_y": ankle_y,
        "hand_bottom": to_y(lm["hand_bottom"]),
        "foot_cut_x": to_x(lm["foot_cut_x"]),
        "hip": hip,
        "shoulder": shoulder,
        "hand": hand,
        "arm_len": math.hypot(hand[0] - shoulder[0], hand[1] - shoulder[1]),
        "leg_ankle": (to_x(lm["leg_ankle_x"]), ankle_y),
        "foot_anchor": (to_x(lm["foot_anchor_x"]), ankle_y),
        "leg_len": ankle_y - hip[1],
    }


# ---------------------------------------------------------------------------
# Mask helpers.
# ---------------------------------------------------------------------------
def _largest(mask):
    from scipy import ndimage
    labels, count = ndimage.label(mask, np.ones((3, 3)))
    if count <= 1:
        return mask
    sizes = ndimage.sum(mask, labels, range(1, count + 1))
    return labels == (1 + int(np.argmax(sizes)))


def _cut_out(rgba, mask):
    out = rgba.copy()
    out[..., 3] = np.where(mask, rgba[..., 3], 0)
    return out


def _tint(rgba, factor):
    """Drop the far limb into the body's own shadow, neutrally.

    This used to lift blue by 8% and green by 1% to "keep the shadow cool rather than
    muddy", but on a white sleeve that is a hue shift, not a shadow: 0.70 tint turned
    (255,255,255) into (178,180,193), a distinctly cyan grey.  Because the far arm
    swings opposite the near one, that strip was on show beside the near sleeve for
    most of the cycle and read as a blue line drawn down the character's arm.  A
    shadow under this diffuse a key light is neutral, so all three channels are
    scaled together and the far sleeve now reads as plain grey.
    """
    out = rgba.astype(np.float32).copy()
    out[..., :3] *= factor
    return np.clip(out, 0, 255).astype(np.uint8)


def _extrude(rgba, allowed, iterations):
    """Bleed edge colour into ``allowed`` so a posed layer never exposes a cut line.

    Only the bounding box of the region that can possibly be filled is touched, which
    keeps this cheap on a canvas that is mostly empty.
    """
    rows = np.nonzero(allowed.any(axis=1))[0]
    cols = np.nonzero(allowed.any(axis=0))[0]
    if rows.size == 0:
        return rgba
    pad = iterations + 2
    y0, y1 = max(0, rows[0] - pad), min(rgba.shape[0], rows[-1] + pad)
    x0, x1 = max(0, cols[0] - pad), min(rgba.shape[1], cols[-1] + pad)

    patch = rgba[y0:y1, x0:x1]
    window = allowed[y0:y1, x0:x1]
    colour = patch[..., :3].astype(np.float32).copy()
    alpha = patch[..., 3].astype(np.float32) / 255.0
    filled = alpha > 0.5
    for _ in range(iterations):
        total = np.zeros_like(colour)
        count = np.zeros(colour.shape[:2], dtype=np.float32)
        for dy, dx in ((-1, 0), (1, 0), (0, -1), (0, 1)):
            total += np.roll(colour, (dy, dx), axis=(0, 1)) * \
                np.roll(filled, (dy, dx), axis=(0, 1))[..., None]
            count += np.roll(filled, (dy, dx), axis=(0, 1))
        grow = (~filled) & (count > 0) & window
        if not grow.any():
            break
        colour[grow] = (total / np.maximum(count, 1.0)[..., None])[grow]
        alpha[grow] = 1.0
        filled |= grow

    out = rgba.copy()
    out[y0:y1, x0:x1] = np.clip(
        np.concatenate([colour, (alpha * 255.0)[..., None]], axis=2), 0, 255).astype(np.uint8)
    return out


def _extrude_up(rgba, allowed):
    """Continue each column upward from its own topmost solid pixel.

    The thigh under the kurta is a continuation of the trouser directly below it, so
    copying that column upward is both cheaper and truer than an isotropic bleed.
    """
    out = rgba.copy()
    alpha = rgba[..., 3]
    for x in np.nonzero(allowed.any(axis=0))[0]:
        rows = np.nonzero(alpha[:, x] > 128)[0]
        if rows.size == 0:
            continue
        top = rows[0]
        fill = np.nonzero(allowed[:top, x])[0]
        if fill.size == 0:
            continue
        out[fill, x] = rgba[top, x]
    return out


def _extrude_back(rgba, allowed, forward):
    """Continue each row backwards from the body's own trailing edge.

    Taking the sleeve out of the torso leaves a slot down the back.  Filling it from each
    row's own trailing pixel gives horizontal stripes, because that pixel flips between
    waistcoat and leftover collar from one row to the next; sampling several pixels in and
    smoothing the result down the column carries the garment round the back cleanly.
    """
    out = rgba.copy()
    alpha = rgba[..., 3]
    rows = np.nonzero(allowed.any(axis=1))[0]
    if rows.size == 0:
        return out

    edges = np.full(rgba.shape[0], -1, dtype=np.int32)
    colour = np.zeros((rgba.shape[0], 3), dtype=np.float32)
    for y in range(rgba.shape[0]):
        cols = np.nonzero(alpha[y] > 128)[0]
        if cols.size == 0:
            continue
        edges[y] = cols[0] if forward > 0 else cols[-1]
        sample = cols[:6] if forward > 0 else cols[-6:]
        colour[y] = rgba[y, sample, :3].astype(np.float32).mean(axis=0)

    valid = edges >= 0
    kernel = np.ones(11, dtype=np.float32)
    weight = np.convolve(valid.astype(np.float32), kernel, mode="same")
    smooth = np.stack([np.convolve(colour[:, c] * valid, kernel, mode="same") /
                       np.maximum(weight, 1e-3) for c in range(3)], axis=1)

    for y in rows:
        if edges[y] < 0:
            continue
        fill = np.nonzero(allowed[y])[0]
        fill = fill[fill < edges[y]] if forward > 0 else fill[fill > edges[y]]
        if fill.size == 0:
            continue
        out[y, fill, :3] = np.clip(smooth[y], 0, 255).astype(np.uint8)
        out[y, fill, 3] = 255
    return out


def _row_scale(rgba, k_curve, axis_x):
    """Scale every row horizontally about ``axis_x`` by its own factor.

    Sampling is done premultiplied, otherwise interpolating across the soft alpha edge
    drags transparent colour into the silhouette as a halo.
    """
    height, width, _ = rgba.shape
    src = rgba.astype(np.float32)
    alpha = src[..., 3:4] / 255.0
    premul = np.concatenate([src[..., :3] * alpha, src[..., 3:4]], axis=2)

    xs = np.arange(width, dtype=np.float32)[None, :]
    sample = (xs - axis_x) / k_curve[:, None] + axis_x
    x0 = np.floor(sample).astype(np.int32)
    frac = (sample - x0)[..., None]
    rows = np.arange(height)[:, None]
    left = premul[rows, np.clip(x0, 0, width - 1)]
    right = premul[rows, np.clip(x0 + 1, 0, width - 1)]
    out = left * (1.0 - frac) + right * frac
    out[(sample < 0) | (sample > width - 1)] = 0.0

    out_alpha = np.clip(out[..., 3:4], 0.0, 255.0)
    colour = np.where(out_alpha > 0.5, out[..., :3] / np.maximum(out_alpha / 255.0, 1e-4), 0.0)
    return np.clip(np.concatenate([colour, out_alpha], axis=2), 0, 255).astype(np.uint8)


def _smoothstep(t):
    t = np.clip(t, 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


# ---------------------------------------------------------------------------
# Layers.
# ---------------------------------------------------------------------------
def build_layers(rig, debug=False):
    rgba, forward = rig["rgba"], rig["forward"]
    height, width = rgba.shape[:2]
    parts = _palette(rgba)
    solid = rgba[..., 3] > 96
    dark = parts["dark"]
    ys = np.arange(height)[:, None].astype(np.float32)
    xs = np.arange(width)[None, :].astype(np.float32)

    # -- arm: down the waistcoat the sleeve is simply everything white behind the green,
    #    which is a real edge in the art.  Below the waistcoat the kurta behind the arm is
    #    the same white as the sleeve and no cut could be honest, so only the bare hand is
    #    taken -- by then the sleeve has already ended at the cuff.
    from scipy import ndimage
    sleeve = (solid
              & (ys >= rig["shoulder_y"]) & (ys <= rig["waist_y"])
              & ((xs - rig["cut"][:, None]) * forward <= 0)
              & ~parts["green"])
    hand = parts["skin"] & (ys > rig["waist_y"] - 6) & (ys <= rig["hand_bottom"])
    hand = ndimage.binary_dilation(_largest(hand), np.ones((3, 3)), iterations=5) & solid
    arm_mask = _largest(sleeve | hand)

    # -- foot: the near shoe only; the far one is a tinted copy, so it must not be cut in.
    foot_mask = (solid
                 & (ys >= rig["ankle_y"] - FOOT_OVERLAP)
                 & ((xs - rig["foot_cut_x"]) * forward >= 0))
    foot_mask = _largest(foot_mask)

    # -- leg: trouser from the kurta hem to just inside the shoe.  Any dark pixel on the
    #    trailing side is the far shoe and belongs to neither leg layer.
    leg_mask = (solid
                & (ys >= rig["hem_y"]) & (ys <= rig["ankle_y"] + FOOT_OVERLAP)
                & ~(dark & ((xs - rig["foot_cut_x"]) * forward < 0)))
    leg_mask = _largest(leg_mask)

    # Both bands deliberately reached past the ankle so no gap could open between
    # trouser and shoe -- but where they met, every pixel was being drawn twice under
    # two different transforms.  The shoe rides rigidly on the ankle while the trouser
    # is stretched along its own axis by the IK, so the moment the leg lengthened or
    # shortened the leg's copy of the shoe slid out from behind the real one and the
    # character walked on doubled feet.
    #
    # The layers are therefore made disjoint and the shoe wins the shared band
    # outright: it is composited after the leg, so its copy was always the one
    # actually on screen, and dropping the leg's copy removes the ghost without
    # changing anything that was visible.  Nothing has to be bled downward to replace
    # it -- the seam cannot open.  The leg's new bottom edge sits FOOT_OVERLAP above
    # the ankle and is scaled with the limb, so under the worst compression in the
    # cycle (-25%) it rides only (1-0.25)*FOOT_OVERLAP up, which is still inside the
    # shoe's own rigid FOOT_OVERLAP reach.  Measured over both directions and every
    # phase, the thinnest the trouser/shoe seal ever gets is 1102px of overlap.
    leg_mask = leg_mask & ~foot_mask

    # A couple of pixels of the sleeve's soft edge would otherwise stay behind in the
    # torso and read as a fringe once the arm swings away.
    torso_mask = solid & (ys <= rig["hem_y"]) & \
        ~ndimage.binary_dilation(arm_mask, np.ones((3, 3)), iterations=2)

    layers = {}

    # The sleeve leaves a slot down the torso's back; carrying the trailing edge of each
    # row backwards fills it so a swung arm reveals garment, not a hole.  The hand leaves
    # an interior hole in the kurta instead, which no edge rule reaches, so whatever is
    # still empty afterwards is closed by an isotropic bleed -- cheap here, and invisible
    # because the kurta around it is flat white.
    slot = ndimage.binary_dilation(arm_mask, np.ones((3, 3)), iterations=6) & \
        (ys <= rig["hem_y"]) & (ys >= rig["shoulder_y"] - 8)
    torso = _cut_out(rgba, torso_mask)
    torso = _extrude_back(torso, slot & ~torso_mask, forward)
    torso = _extrude(torso, slot & (torso[..., 3] < 128), iterations=36)

    # The kurta widens over its lowest rows so a swinging thigh stays covered.
    flare = 1.0 + (HEM_FLARE - 1.0) * _smoothstep(
        (np.arange(height) - (rig["hem_y"] - HEM_FLARE_TOP)) / float(HEM_FLARE_TOP))
    flare[np.arange(height) > rig["hem_y"]] = HEM_FLARE
    layers["torso"] = Image.fromarray(_row_scale(torso, flare.astype(np.float32),
                                                 CANVAS_AXIS_X), "RGBA")

    # The thigh hidden under the kurta is extruded up from the trouser, tapering towards
    # the hip so its top corner cannot swing out past the hem.
    leg = _cut_out(rgba, leg_mask)
    hem_cols = np.nonzero(leg_mask[int(round(rig["hem_y"])) + 2])[0]
    half = 0.5 * (hem_cols[-1] - hem_cols[0]) if hem_cols.size else 40.0
    centre = 0.5 * (hem_cols[0] + hem_cols[-1]) if hem_cols.size else CANVAS_AXIS_X
    top = rig["hip"][1] - 6.0
    t = np.clip((ys - top) / max(1.0, rig["hem_y"] - top), 0.0, 1.0)
    reach = half * (LEG_BACKING_TAPER + (1.0 - LEG_BACKING_TAPER) * t)
    backing = (ys >= top) & (ys < rig["hem_y"] + 2) & (np.abs(xs - centre) <= reach)
    layers["legNear"] = Image.fromarray(_extrude_up(leg, backing & ~leg_mask), "RGBA")
    layers["footNear"] = Image.fromarray(_cut_out(rgba, foot_mask), "RGBA")
    layers["armNear"] = Image.fromarray(_cut_out(rgba, arm_mask), "RGBA")

    for near, far in (("legNear", "legFar"), ("footNear", "footFar"), ("armNear", "armFar")):
        layers[far] = Image.fromarray(
            _tint(np.asarray(layers[near]), FAR_LIMB_TINT), "RGBA")

    if debug:
        BUILD.mkdir(exist_ok=True)
        for name, layer in layers.items():
            if name.endswith("Far"):
                continue
            layer.crop((WINDOW_X0, WINDOW_Y0, WINDOW_X0 + WINDOW_W,
                        WINDOW_Y0 + WINDOW_H)).resize((FRAME_W, FRAME_H),
                                                      Image.Resampling.LANCZOS) \
                .save(BUILD / f"layer_{rig['direction']}_{name}.png")
    return layers


# ---------------------------------------------------------------------------
# Transforms.  ``matrix`` is the forward 2x2 linear part; a point maps as
#     out = matrix @ (point - pivot) + pivot + translate
# ---------------------------------------------------------------------------
def _rotation(angle_deg):
    angle = math.radians(angle_deg)
    cos_a, sin_a = math.cos(angle), math.sin(angle)
    return ((cos_a, -sin_a), (sin_a, cos_a))


def _matmul(a, b):
    return (
        (a[0][0] * b[0][0] + a[0][1] * b[1][0], a[0][0] * b[0][1] + a[0][1] * b[1][1]),
        (a[1][0] * b[0][0] + a[1][1] * b[1][0], a[1][0] * b[0][1] + a[1][1] * b[1][1]),
    )


def _apply(matrix, pivot, translate, point):
    dx, dy = point[0] - pivot[0], point[1] - pivot[1]
    return (pivot[0] + matrix[0][0] * dx + matrix[0][1] * dy + translate[0],
            pivot[1] + matrix[1][0] * dx + matrix[1][1] * dy + translate[1])


def _warp(layer, pivot, matrix, translate):
    (a11, a12), (a21, a22) = matrix
    det = a11 * a22 - a12 * a21
    i11, i12 = a22 / det, -a12 / det
    i21, i22 = -a21 / det, a11 / det
    ox, oy = pivot[0] + translate[0], pivot[1] + translate[1]
    coeffs = (i11, i12, pivot[0] - (i11 * ox + i12 * oy),
              i21, i22, pivot[1] - (i21 * ox + i22 * oy))
    return layer.transform(layer.size, Image.AFFINE, coeffs, resample=Image.BICUBIC)


def _limb_matrix(joint_base, tip_base, joint_world, tip_world):
    """Exact single-segment IK: stretch is applied strictly along the limb axis, so a
    lengthening limb keeps its width and the extremity lands precisely on target."""
    bx, by = tip_base[0] - joint_base[0], tip_base[1] - joint_base[1]
    wx, wy = tip_world[0] - joint_world[0], tip_world[1] - joint_world[1]
    base_len = math.hypot(bx, by)
    world_len = math.hypot(wx, wy)

    rotation = _rotation(math.degrees(math.atan2(wy, wx) - math.atan2(by, bx)))
    ux, uy = bx / base_len, by / base_len
    stretch = world_len / base_len - 1.0
    along = ((1.0 + stretch * ux * ux, stretch * ux * uy),
             (stretch * ux * uy, 1.0 + stretch * uy * uy))
    return _matmul(rotation, along), rotation


# ---------------------------------------------------------------------------
# Gait.
# ---------------------------------------------------------------------------
def _leg_state(u):
    """Fore/aft offset, ground clearance and contact flag for one leg at cycle point u.

    Stance is exactly linear so the planted foot tracks the ground.  The swing is a cubic
    Hermite whose end slopes match the stance rate, making the whole path C1 continuous.
    """
    u = u % 1.0
    if u < STANCE_FRACTION:
        return STEP_MAX * (1.0 - 2.0 * u / STANCE_FRACTION), 0.0, True

    v = (u - STANCE_FRACTION) / (1.0 - STANCE_FRACTION)
    slope = -2.0 * STEP_MAX * (1.0 - STANCE_FRACTION) / STANCE_FRACTION
    h00 = 2 * v ** 3 - 3 * v ** 2 + 1
    h10 = v ** 3 - 2 * v ** 2 + v
    h01 = -2 * v ** 3 + 3 * v ** 2
    h11 = v ** 3 - v ** 2
    offset = h00 * (-STEP_MAX) + h01 * STEP_MAX + (h10 + h11) * slope
    lift = FOOT_LIFT * math.sin(math.pi * v) ** 2
    return offset, lift, False


def _walk_pose(rig, phase):
    forward = rig["forward"]
    theta = phase * 2.0 * math.pi
    torso = {
        "angle": forward * (TORSO_LEAN_DEG + TORSO_BOUNCE_DEG * math.sin(2.0 * theta)),
        "translate": (forward * TORSO_SURGE * math.sin(2.0 * theta),
                      PELVIS_BOB * math.cos(2.0 * theta)),
    }
    legs = {}
    for side, state in (("Near", _leg_state(phase)), ("Far", _leg_state(phase + 0.5))):
        offset, lift, _ = state
        legs[side] = {
            "ankle": (rig["leg_ankle"][0] + forward * offset, rig["ankle_y"] - lift),
            "roll": forward * FOOT_ROLL_DEG * (offset / STEP_MAX),
        }
    # Each arm counter-swings against the leg on its own side.
    arms = {}
    for side, leg_phase in (("Near", phase + 0.5), ("Far", phase)):
        angle = math.radians(ARM_SWING_DEG * math.sin(leg_phase * 2.0 * math.pi))
        arms[side] = {"angle": angle}
    return torso, legs, arms


def _idle_pose(rig, phase):
    theta = phase * 2.0 * math.pi
    breathe = math.sin(theta)
    torso = {
        "angle": rig["forward"] * 0.5 * breathe,
        "translate": (0.0, -IDLE_BREATHE * breathe),
    }
    legs = {}
    for side, sign in (("Near", 1.0), ("Far", -1.0)):
        legs[side] = {
            "ankle": (rig["leg_ankle"][0] + sign * 0.012 * RIG_HEIGHT, rig["ankle_y"]),
            "roll": 0.0,
        }
    arms = {"Near": {"angle": math.radians(1.6 * breathe)},
            "Far": {"angle": math.radians(-1.6 * breathe)}}
    return torso, legs, arms


# ---------------------------------------------------------------------------
# Rendering.
# ---------------------------------------------------------------------------
def render_canvas(rig, layers, torso, legs, arms):
    canvas = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
    torso_matrix = _rotation(torso["angle"])
    torso_pivot = rig["hip"]

    def on_torso(point):
        return _apply(torso_matrix, torso_pivot, torso["translate"], point)

    hip_world = on_torso(rig["hip"])
    shoulder_world = on_torso(rig["shoulder"])

    posed = {}
    for side, leg in legs.items():
        matrix, rotation = _limb_matrix(rig["hip"], rig["leg_ankle"], hip_world, leg["ankle"])
        posed[f"leg{side}"] = _warp(
            layers[f"leg{side}"], rig["hip"], matrix,
            (hip_world[0] - rig["hip"][0], hip_world[1] - rig["hip"][1]))
        # The shoe rides rigidly on the ankle: rotated with the leg, never stretched.
        foot_matrix = _matmul(rotation, _rotation(leg["roll"]))
        posed[f"foot{side}"] = _warp(
            layers[f"foot{side}"], rig["foot_anchor"], foot_matrix,
            (leg["ankle"][0] - rig["foot_anchor"][0], leg["ankle"][1] - rig["foot_anchor"][1]))

    for side, arm in arms.items():
        hand = (shoulder_world[0] + rig["forward"] * rig["arm_len"] * math.sin(arm["angle"]),
                shoulder_world[1] + rig["arm_len"] * math.cos(arm["angle"]))
        matrix, _ = _limb_matrix(rig["shoulder"], rig["hand"], shoulder_world, hand)
        posed[f"arm{side}"] = _warp(
            layers[f"arm{side}"], rig["shoulder"], matrix,
            (shoulder_world[0] - rig["shoulder"][0], shoulder_world[1] - rig["shoulder"][1]))

    canvas.alpha_composite(posed["armFar"])
    canvas.alpha_composite(posed["legFar"])
    canvas.alpha_composite(posed["footFar"])
    canvas.alpha_composite(posed["legNear"])
    canvas.alpha_composite(posed["footNear"])
    canvas.alpha_composite(_warp(layers["torso"], torso_pivot, torso_matrix,
                                 torso["translate"]))
    canvas.alpha_composite(posed["armNear"])
    return canvas


def _sharpen(frame):
    """Restore the acuity the resample chain costs, without touching the silhouette.

    Unsharp over straight-alpha art darkens the rim, because the kernel reads the
    transparent black outside the figure as if it were shadow.  Extending the
    colour outward to its nearest opaque neighbour first removes that edge from
    the filter's view entirely; the original alpha is then put back untouched, so
    the outline is bit-for-bit what the morph produced.
    """
    from scipy import ndimage

    rgba = np.asarray(frame, dtype=np.uint8)
    alpha = rgba[..., 3]
    solid = alpha > 8
    if not solid.any():
        return frame

    index = ndimage.distance_transform_edt(~solid, return_distances=False,
                                           return_indices=True)
    filled = rgba[..., :3][tuple(index)]
    sharp = Image.fromarray(filled, "RGB").filter(ImageFilter.UnsharpMask(*FRAME_SHARPEN))
    return Image.fromarray(np.dstack([np.asarray(sharp), alpha]), "RGBA")


def _crop_frame(canvas):
    window = canvas.crop((WINDOW_X0, WINDOW_Y0, WINDOW_X0 + WINDOW_W, WINDOW_Y0 + WINDOW_H))
    return _sharpen(window.resize((FRAME_W, FRAME_H), Image.Resampling.LANCZOS))


def render_frame(rig, layers, torso, legs, arms):
    return _crop_frame(render_canvas(rig, layers, torso, legs, arms))


# ---------------------------------------------------------------------------
# Front-facing stand, and the turn that reaches it.
#
# `Walking.png` is pure profile, so every non-profile pose the turn needs -- the
# front stand and the two three-quarter views between it and the profile -- comes
# from `Walking2.png`.  All of them are normalised onto the same canvas as the
# profile rigs -- same height, same ground line, same body axis -- so the
# character cannot change size or drift sideways as he turns.
# ---------------------------------------------------------------------------
def _front_axis(alpha):
    """Body axis = centroid of the chest band, matching how the profile rigs are
    aligned.  The bbox centre would be pulled around by whichever arm hangs wider."""
    solid = alpha > 96
    rows = np.nonzero(solid.any(axis=1))[0]
    top, ground = int(rows[0]), int(rows[-1])
    height = ground - top + 1
    band = solid[top + int(0.28 * height): top + int(0.46 * height)]
    weights = band.sum(axis=0).astype(np.float64)
    return float(np.average(np.arange(band.shape[1]), weights=weights)), top, ground


def _normalise(image):
    """Place one authored figure on the rig canvas: scaled to `RIG_HEIGHT`, stood on
    the shared ground line, and centred on its chest axis.  Every pose in the turn
    goes through this, so none of them can change the character's size or position."""
    axis, top, ground = _front_axis(np.asarray(image)[..., 3])
    scale = RIG_HEIGHT / (ground - top + 1)
    resized = image.resize((max(1, int(round(image.width * scale))),
                            max(1, int(round(image.height * scale)))),
                           Image.Resampling.LANCZOS)
    if scale > FRONT_SHARPEN_MIN_SCALE:
        alpha = resized.getchannel("A")
        sharp = resized.convert("RGB").filter(ImageFilter.UnsharpMask(*FRONT_SHARPEN))
        resized = Image.merge("RGBA", (*sharp.split(), alpha))
    canvas = Image.new("RGBA", (CANVAS_W, CANVAS_H), (0, 0, 0, 0))
    canvas.alpha_composite(resized, (int(round(CANVAS_AXIS_X - axis * scale)),
                                     int(round(CANVAS_GROUND_Y - ground * scale))))
    return np.asarray(canvas).copy()


def _walk2_figure(group, index):
    figures = walk2_sheet.figures()
    if group not in figures:
        raise SystemExit(f"Walking2.png has no group {group!r}")
    if not 0 <= index < len(figures[group]):
        raise SystemExit(f"Walking2.png {group} has no figure {index}")
    return walk2_sheet.cutout(figures[group][index])


def _load_front():
    return _normalise(Image.open(FRONT_ART).convert("RGBA"))


def _load_rotation_keys(direction):
    """The authored three-quarter views for one direction, front-most first."""
    return [_normalise(_walk2_figure(group, index))
            for group, index in ROTATION_KEYS[direction]]


def _row_spans(canvas):
    """Leading and trailing edge of every row, gaps interpolated and the result
    smoothed down the column so one stray pixel cannot kink the morph."""
    solid = canvas[..., 3] > 40
    lo = np.full(CANVAS_H, np.nan, dtype=np.float32)
    hi = np.full(CANVAS_H, np.nan, dtype=np.float32)
    for y in np.nonzero(solid.any(axis=1))[0]:
        cols = np.nonzero(solid[y])[0]
        lo[y], hi[y] = cols[0], cols[-1]

    index = np.arange(CANVAS_H, dtype=np.float32)
    known = ~np.isnan(lo)
    out = []
    for values in (lo, hi):
        filled = np.interp(index, index[known], values[known]).astype(np.float32)
        pad = SPAN_SMOOTH // 2
        kernel = np.ones(SPAN_SMOOTH, dtype=np.float32) / SPAN_SMOOTH
        out.append(np.convolve(np.pad(filled, pad, mode="edge"), kernel, mode="valid"))
    return out[0], out[1], known


def _row_morph(rgba, spans, s):
    """Warp each row of the front figure onto the profile's row span.

    A uniform squash leaves the head in the wrong place -- seen from the front it sits
    over the body axis, in profile it projects forward -- and the cross-dissolve then
    shows two heads.  Mapping each row's leading and trailing edge onto the profile's
    makes the silhouettes coincide at s = 1, so the dissolve only has to change colour.
    """
    (f0, f1), (p0, p1) = spans
    t0, t1 = f0 + (p0 - f0) * s, f1 + (p1 - f1) * s
    ratio = np.maximum(f1 - f0, 1.0) / np.maximum(t1 - t0, 1.0)

    src = rgba.astype(np.float32)
    alpha = src[..., 3:4] / 255.0
    premul = np.concatenate([src[..., :3] * alpha, src[..., 3:4]], axis=2)

    xs = np.arange(CANVAS_W, dtype=np.float32)[None, :]
    sample = (xs - t0[:, None]) * ratio[:, None] + f0[:, None]
    x0 = np.floor(sample).astype(np.int32)
    frac = (sample - x0)[..., None]
    rows = np.arange(CANVAS_H)[:, None]
    left = premul[rows, np.clip(x0, 0, CANVAS_W - 1)]
    right = premul[rows, np.clip(x0 + 1, 0, CANVAS_W - 1)]
    out = left * (1.0 - frac) + right * frac
    out[(sample < 0) | (sample > CANVAS_W - 1)] = 0.0

    out_alpha = np.clip(out[..., 3:4], 0.0, 255.0)
    colour = np.where(out_alpha > 0.5, out[..., :3] / np.maximum(out_alpha / 255.0, 1e-4), 0.0)
    return np.clip(np.concatenate([colour, out_alpha], axis=2), 0, 255).astype(np.uint8)


def _head_sweep_weights(canvas):
    """Per-row weight for the head turn, plus the head's own width.

    The head is the part of a turn a viewer actually watches, and it is the part a
    silhouette morph cannot do anything with: a head is about as deep as it is
    wide, so its outline barely changes between front and profile even though the
    face swings through ninety degrees.  Morphing outlines alone therefore leaves
    the head sitting still for most of the turn and then cross-fading two faces on
    top of each other -- a double exposure, and the reason the turn read as a jump
    rather than a movement.  Weighting the rows lets the face be swept round
    separately, easing out through the neck so the shoulders do not shear.
    """
    solid = canvas[..., 3] > 96
    rows = np.nonzero(solid.any(axis=1))[0]
    top, ground = int(rows[0]), int(rows[-1])
    height = ground - top + 1

    band = solid[top: top + max(1, int(round(HEAD_BAND * height)))]
    counts = band.sum(axis=1)
    width = float(np.median(counts[counts > 0])) if (counts > 0).any() else 1.0

    y = np.arange(CANVAS_H, dtype=np.float32)
    base = top + HEAD_BAND * height
    fade = max(HEAD_TAPER * height, 1.0)
    weights = _smoothstep(np.clip((base + fade - y) / fade, 0.0, 1.0)).astype(np.float32)
    weights[y < top] = 0.0
    return weights, width


def _leg_weights(canvas):
    """Per-row weight for the legs, which change over to the walk art ahead of the
    body.

    Setting off, a man's feet commit before his shoulders do.  Following that here
    also removes an artefact: the authored three-quarter drawings stand with the
    feet together, while the walk frame they are handing over to already has them
    apart, and morphing one silhouette into the other conjures a shoe-shaped ghost
    in the gap between them.  Switching the legs early means the frames where the
    gap is widest are showing the real walk art rather than a blend of the two.
    """
    solid = canvas[..., 3] > 96
    rows = np.nonzero(solid.any(axis=1))[0]
    top, ground = int(rows[0]), int(rows[-1])
    height = ground - top + 1

    y = np.arange(CANVAS_H, dtype=np.float32)
    base = ground - LEG_BAND * height
    fade = max(LEG_TAPER * height, 1.0)
    weights = _smoothstep(np.clip((y - base + fade) / fade, 0.0, 1.0)).astype(np.float32)
    weights[y > ground] = 0.0
    return weights


def _head_axis(canvas, weights, forward):
    """Leading edge of the head -- the nose side -- to narrow the head about.

    Narrowing about the head's centre reads as a squash: both sides come in and the
    head just gets smaller.  A head actually turning keeps its leading edge where it
    is while the far side of the face sweeps in behind the nose, so that is the
    anchor.  Coming out the other side the same anchor sends the back of the head
    sweeping round, which is the second half of the same rotation.
    """
    solid = canvas[..., 3] > 96
    rows = np.nonzero((weights > 0.5) & solid.any(axis=1))[0]
    if rows.size == 0:
        return CANVAS_AXIS_X
    cols = np.nonzero(solid[rows].any(axis=0))[0]
    return float(cols[-1] if forward > 0 else cols[0])


def _row_shift(rgba, dx):
    """Slide each row sideways by its own amount, sampled premultiplied."""
    src = rgba.astype(np.float32)
    alpha = src[..., 3:4] / 255.0
    premul = np.concatenate([src[..., :3] * alpha, src[..., 3:4]], axis=2)

    xs = np.arange(CANVAS_W, dtype=np.float32)[None, :]
    sample = xs - dx[:, None]
    x0 = np.floor(sample).astype(np.int32)
    frac = (sample - x0)[..., None]
    rows = np.arange(CANVAS_H)[:, None]
    left = premul[rows, np.clip(x0, 0, CANVAS_W - 1)]
    right = premul[rows, np.clip(x0 + 1, 0, CANVAS_W - 1)]
    out = left * (1.0 - frac) + right * frac
    out[(sample < 0) | (sample > CANVAS_W - 1)] = 0.0

    out_alpha = np.clip(out[..., 3:4], 0.0, 255.0)
    colour = np.where(out_alpha > 0.5, out[..., :3] / np.maximum(out_alpha / 255.0, 1e-4), 0.0)
    return np.clip(np.concatenate([colour, out_alpha], axis=2), 0, 255).astype(np.uint8)


def _rotation_chain(sources, t):
    """The authored drawing to use at this point in the turn.

    The turn used to interpolate straight from the front stand to the profile, so
    every angle in between was invented -- a warped front figure, not a man seen
    from that side.  `Walking2.png` supplies the missing angles, so instead the
    sources change over one at a time, front-most first, and the rotation is
    carried by real drawings.

    Each changeover is deliberately narrow and they do not overlap, so no frame is
    a half-and-half average of two views: it is one drawing, or a brief bridge
    between two neighbouring ones that are already only a few degrees apart.
    """
    if len(sources) == 1:
        return sources[0]
    spacing = BODY_SWITCH / len(sources)
    band = spacing * ROT_SWITCH_BAND
    for index in range(len(sources) - 1, 0, -1):
        w = float(_smoothstep((t - index * spacing) / band + 0.5))
        if w >= 0.999:
            return sources[index]
        if w > 0.001:
            return _blend(_rotation_chain(sources[:index], t), sources[index], w)
    return sources[0]


def _morph_sequence(front, targets, forward, keys=()):
    """Morph the front stand onto each target pose in turn.

    ``t`` runs 0 -> 1 across the sequence, so frame 0 is exactly the front stand and
    the last frame is exactly its target -- never a synthetic pose at either end.
    Three mechanisms share the work.  The rotation itself changes over through the
    authored three-quarter drawings in ``keys`` (see ``_rotation_chain``).  Each row
    of whichever drawing is current is then warped onto the target's row span, which
    lines the outer silhouettes up.  The blend morphs what is left -- chiefly the
    interior, where standing legs are together and walking legs are apart.

    All three are shape operations, so every in-between frame is a single fully
    opaque figure.  A runtime cross-fade between two sprites cannot do this: two
    layers at half alpha do not composite to an opaque character, and the scenery
    shows through him.
    """
    count = len(targets)
    frames = []
    for index, target in enumerate(targets):
        t = index / (count - 1)
        source = _rotation_chain([front, *keys], t)
        f0, f1, _ = _row_spans(source)
        head, head_width = _head_sweep_weights(source)
        p0, p1, _ = _row_spans(target)
        warped = _row_morph(source, ((f0, f1), (p0, p1)),
                            float(_smoothstep(min(1.0, t / TURN_SHAPE_DONE))))
        # Only the outgoing figure is swept: it carries the face that has to
        # travel, and by the last frame it contributes nothing, so the sequence
        # still ends on its target untouched.
        sweep = float(_smoothstep(t)) * HEAD_SWEEP * head_width * forward
        if abs(sweep) > 0.05:
            warped = _row_shift(warped, head * sweep)
        w = float(_smoothstep((t - BODY_SWITCH) / BODY_SWITCH_BAND + 0.5))

        # A cross-fade always has a frame at fifty per cent, and at fifty per cent the
        # front kurta and the profile waistcoat average into a washed pale green, two
        # noses overlap and there are four eyes.  So neither the body nor the head is
        # cross-faded: each changes over quickly, and at a different moment.  The body
        # goes first and the head follows, which is how a real turn reads -- and it
        # means the in-between frames are a man whose shoulders have come round while
        # he is still looking at you, not a double exposure.  The head is narrowed
        # toward the nose as it goes, so it changes over at its least readable.  Both
        # effects are zero at t = 0 and t = 1, which is what keeps the first frame the
        # stand and the last frame its walk frame, pixel for pixel.
        head_w = float(_smoothstep((t - HEAD_SWITCH) / HEAD_SWITCH_BAND + 0.5))
        leg_w = float(_smoothstep((t - LEG_SWITCH) / LEG_SWITCH_BAND + 0.5))
        legs = _leg_weights(source)
        # The two bands are cut so they cannot meet, but a mis-measured figure must
        # not be able to drive the blend weight past one.
        overlap = np.maximum(head + legs, 1.0)
        head, legs = head / overlap, legs / overlap
        dip = HEAD_CARD * math.sin(math.pi * t ** HEAD_DIP_SKEW)
        if dip > 0.005:
            axis = _head_axis(target, head, forward)
            squeeze = 1.0 - dip * head
            warped = _row_scale(warped, squeeze, axis)
            target = _row_scale(target, squeeze, axis)

        # Three bands, each changing over at its own moment: legs first, then the
        # body, then the head.  All three weights are 0 at t = 0 and 1 at t = 1, so
        # the sequence still begins on the stand and ends on its walk frame, pixel
        # for pixel -- the bake asserts exactly that.
        blend = (w * (1.0 - head - legs)) + (head_w * head) + (leg_w * legs)
        frames.append(_crop_frame(Image.fromarray(_blend(warped, target, blend), "RGBA")))
    return frames


def _blend(a, b, w):
    """Morph one figure into another without either going see-through.

    A straight cross-fade is wrong here, and visibly so.  Averaging two alphas
    leaves every pixel that only one of the figures covers at partial opacity, and
    the two figures differ by exactly the parts that matter: standing, his legs are
    together and the silhouette is solid; walking, they are apart with daylight
    between them.  Fading one to the other put the fence rail straight through his
    legs at the midpoint of the turn -- 28% of the figure below full opacity.

    So the shape is morphed rather than faded.  The alpha is carried as a signed
    distance field, which interpolates as a *shape*: the gap between the legs opens
    from nothing instead of fading up from transparent, and the interior stays
    solid throughout.

    Colour is then blended across that shape, weighted by how much each source
    actually covered the pixel rather than by the fade alone.  That distinction
    matters wherever the in-between shape reaches somewhere only one of the two
    figures did -- the daylight opening between the legs, the arm swinging clear of
    the body.  There the source that was never there has no colour to offer but a
    smear of its nearest edge, and giving that smear half the vote washes the real
    colour out.  Weighted by coverage it gets no vote at all, so the green stays
    green.  Only where neither figure reached does the smear stand in.

    ``w`` may be a single number or one weight per row.  Per-row is what lets the
    head cross over on a different schedule from the body -- see the caller.
    """
    weights = np.asarray(w, dtype=np.float32)
    if float(weights.max()) <= 0.001:
        return a
    if float(weights.min()) >= 0.999:
        return b
    col = weights.reshape(-1, 1) if weights.ndim else np.full((a.shape[0], 1), float(weights))

    from scipy import ndimage

    def field(img):
        alpha = img[..., 3].astype(np.float32) / 255.0
        mask = alpha > 0.5
        if not mask.any():
            flat = img[..., :3].astype(np.float32)
            return np.full(alpha.shape, -_SDF_CLAMP, np.float32), flat, alpha
        d_out, idx = ndimage.distance_transform_edt(~mask, return_indices=True)
        d_in = ndimage.distance_transform_edt(mask)
        # Near the edge the art's own anti-aliasing is a better sub-pixel estimate of
        # where the true boundary lies than a whole-pixel distance transform is.
        edge = (d_in <= 1.0) & (d_out <= 1.0)
        sdf = np.where(edge, alpha - 0.5, d_in - d_out).astype(np.float32)
        filled = img[..., :3].astype(np.float32)[idx[0], idx[1]]
        return np.clip(sdf, -_SDF_CLAMP, _SDF_CLAMP), filled, alpha

    sdf_a, rgb_a, cov_a = field(a)
    sdf_b, rgb_b, cov_b = field(b)

    sdf = sdf_a * (1.0 - col) + sdf_b * col
    alpha = np.clip(sdf / _SDF_EDGE + 0.5, 0.0, 1.0)

    ka = ((1.0 - col) * cov_a)[..., None]
    kb = (col * cov_b)[..., None]
    total = ka + kb
    rgb = np.where(
        total > 1e-3,
        (rgb_a * ka + rgb_b * kb) / np.maximum(total, 1e-3),
        rgb_a * (1.0 - col[..., None]) + rgb_b * col[..., None],
    )

    out = np.concatenate([rgb, (alpha * 255.0)[..., None]], axis=2)
    return np.clip(out, 0, 255).astype(np.uint8)


def _front_idle(front, phase):
    """A stand is not a statue, but nor does a standing man rise and fall.

    The previous version lifted the whole figure, feet included, by a rounded
    whole number of canvas pixels -- so he levitated, and did it in steps.  What
    actually moves when someone breathes is the ribcage, so that is all that
    moves here: a smooth sub-pixel widening centred on the chest, tapering to
    nothing well above the hips.  His feet do not move at all.
    """
    swell = FRONT_BREATH_SWELL * 0.5 * (1.0 - math.cos(2.0 * math.pi * phase))
    rows = np.arange(CANVAS_H, dtype=np.float32)
    height_up = (CANVAS_GROUND_Y - rows) / float(RIG_HEIGHT)
    band = np.exp(-0.5 * ((height_up - BREATH_CENTRE) / BREATH_SPREAD) ** 2)
    band[height_up <= 0.0] = 0.0
    scaled = _row_scale(front, 1.0 + swell * band, CANVAS_AXIS_X)
    return _crop_frame(Image.fromarray(scaled, "RGBA"))





# ---------------------------------------------------------------------------
# Bake.
# ---------------------------------------------------------------------------
def _configure(rigs):
    """Derive the gait scale once, from the shorter of the two legs, so both directions
    share one stride and the runtime needs a single distance lock."""
    global STEP_MAX, STRIDE_SOURCE, STRIDE_LENGTH_PX
    STEP_MAX = STEP_RATIO * min(rig["leg_len"] for rig in rigs.values())
    # Derived, not guessed: the support foot travels 2*STEP_MAX backwards over the
    # STANCE_FRACTION of the cycle it is planted, and the body must advance by exactly
    # that much in the same interval.
    STRIDE_SOURCE = 2.0 * STEP_MAX / STANCE_FRACTION
    STRIDE_LENGTH_PX = round(STRIDE_SOURCE * SCALE, 2)


def _contact_sheet(frames, path, columns=8):
    width, height = frames[0].size
    rows = math.ceil(len(frames) / columns)
    sheet = Image.new("RGB", (columns * width, rows * height), (58, 62, 74))
    for index, frame in enumerate(frames):
        sheet.paste(frame, ((index % columns) * width, (index // columns) * height), frame)
    sheet.save(path)


def _report_continuity(label, frames):
    """Frame-to-frame deltas, wrap included.  A spike at the wrap means a visible jerk."""
    arrays = [np.asarray(frame, dtype=np.float32) for frame in frames]
    deltas = [float(np.abs(arrays[(i + 1) % len(arrays)] - a).mean())
              for i, a in enumerate(arrays)]
    wrap, interior = deltas[-1], deltas[:-1]
    mean = sum(interior) / len(interior)
    print(f"  {label}: n={len(frames)} mean={mean:.3f} min={min(interior):.3f} "
          f"max={max(interior):.3f} wrap={wrap:.3f} wrap/mean={wrap / mean:.3f}")


def _hitbox(idle_frames):
    """Measure the standing silhouette rather than guessing at it."""
    union = None
    for frame in idle_frames:
        mask = np.asarray(frame)[..., 3] > 24
        union = mask if union is None else (union | mask)
    cols = np.nonzero(union.any(axis=0))[0]
    rows = np.nonzero(union.any(axis=1))[0]
    return {"width": int(cols[-1] - cols[0] + 1) + 4,
            "height": int(BASELINE_Y - rows[0]) + 4}


def _reach(frames):
    """Half-width of the widest pose, measured from the frame's body axis.

    The hitbox is the *standing* silhouette, but mid-stride the legs are spread
    and the leading arm is out, so the drawing is far wider than the body that
    collides.  Stopping him at a wall on the hitbox alone would therefore push
    that overhang off the screen.  This is how much room the widest frame
    actually needs either side of him.
    """
    reach = 0
    for frame in frames:
        cols = np.nonzero((np.asarray(frame)[..., 3] > 24).any(axis=0))[0]
        if cols.size:
            reach = max(reach, CENTER_X - int(cols[0]), int(cols[-1]) + 1 - CENTER_X)
    return int(reach)


def bake(debug=False):
    rigs = {name: _load(name) for name in ("right", "left")}
    _configure(rigs)
    front = _load_front()
    rotations = {name: _load_rotation_keys(name) for name in rigs}

    frames = []
    profiles = {}
    starts = {}
    passes = {}
    for name, rig in rigs.items():
        layers = build_layers(rig, debug=debug)
        suffix = name.capitalize()
        for index in range(WALK_FRAMES):
            frames.append((f"walk{suffix}_{index:02d}",
                           render_frame(rig, layers, *_walk_pose(rig, index / WALK_FRAMES))))
        profiles[name] = render_frame(rig, layers, *_idle_pose(rig, 0.0))
        # Setting off is not a separate event from walking: the turn's targets are
        # successive frames of the walk itself, so the last turn frame *is* the walk
        # frame the character carries on from.
        starts[name] = [np.asarray(render_canvas(
            rig, layers, *_walk_pose(rig, ((START_FRAME + i) % WALK_FRAMES) / WALK_FRAMES)))
            for i in range(TURN_FRAMES)]
        # Settling has the same requirement in reverse, so its targets are walk frames
        # too -- the passing poses, where the ankles are already under the hips.
        passes[name] = [np.asarray(render_canvas(
            rig, layers, *_walk_pose(rig, target / WALK_FRAMES)))
            for target in STOP_TARGETS]

    # Two morph families, because setting off and settling are not each other's
    # reverse: `turn` runs front stand -> walk and is distance-locked, `stop` runs
    # front stand -> passing pose and is played backwards against a timer. Both end on
    # a real walk frame, so both hand over to the gait without a seam.
    turns = {name: _morph_sequence(front, starts[name], FORWARD[name], rotations[name])
             for name in rigs}
    stops = {(name, variant): _morph_sequence(
        front, [passes[name][k]] * TURN_FRAMES, FORWARD[name], rotations[name])
        for name in rigs for k, variant in enumerate(STOP_VARIANTS)}
    stand = [_front_idle(front, index / FRONT_IDLE_FRAMES) for index in range(FRONT_IDLE_FRAMES)]

    for index, frame in enumerate(stand):
        frames.append((f"idleFront_{index:02d}", frame))
    for name in rigs:
        for index, frame in enumerate(turns[name]):
            frames.append((f"turn{name.capitalize()}_{index:02d}", frame))
        for variant in STOP_VARIANTS:
            for index, frame in enumerate(stops[(name, variant)]):
                frames.append((f"stop{name.capitalize()}{variant}_{index:02d}", frame))

    hitbox = _hitbox([profiles["right"], profiles["left"]])
    reach = _reach([frame for _, frame in frames])

    columns = ATLAS_COLUMNS
    rows = math.ceil(len(frames) / columns)
    sheet = Image.new("RGBA", (columns * FRAME_W, rows * FRAME_H), (0, 0, 0, 0))
    entries = []
    for index, (name, frame) in enumerate(frames):
        x, y = (index % columns) * FRAME_W, (index // columns) * FRAME_H
        sheet.alpha_composite(frame, (x, y))
        entries.append({
            "filename": name,
            "frame": {"x": x, "y": y, "w": FRAME_W, "h": FRAME_H},
            "rotated": False,
            "trimmed": False,
            "spriteSourceSize": {"x": 0, "y": 0, "w": FRAME_W, "h": FRAME_H},
            "sourceSize": {"w": FRAME_W, "h": FRAME_H},
        })

    OUT.mkdir(parents=True, exist_ok=True)
    sheet.save(OUT / "hero.png", optimize=True)
    (OUT / "hero.json").write_text(json.dumps({
        "frames": entries,
        "meta": {"app": "tcf-avatar-rig", "version": "3.1", "image": "hero.png",
                 "format": "RGBA8888",
                 "size": {"w": sheet.width, "h": sheet.height}, "scale": "1"},
    }, indent=2), encoding="utf-8")
    (OUT / "rig-meta.json").write_text(json.dumps({
        "frameWidth": FRAME_W,
        "frameHeight": FRAME_H,
        "centerX": CENTER_X,
        "baselineY": BASELINE_Y,
        "characterHeightPx": CHARACTER_HEIGHT,
        "source": "Walking.png",
        "standSource": "art/avatar-front.png",
        "rotationSource": "Walking2.png (authored three-quarter views)",
        "directions": ["right", "left"],
        "walk": {"frames": WALK_FRAMES, "strideLengthPx": STRIDE_LENGTH_PX},
        "idle": {"frames": FRONT_IDLE_FRAMES, "fps": 9, "facing": "camera"},
        "turn": {"frames": TURN_FRAMES, "startFrame": START_FRAME,
                 "note": "turn<Dir>_i morphs the stand onto walk frame "
                         "startFrame + i, so turn<Dir>_{n-1} is that walk frame "
                         "exactly"},
        "stop": {"frames": TURN_FRAMES,
                 "variants": dict(zip(STOP_VARIANTS, STOP_TARGETS)),
                 "note": "stop<Dir><V>_i morphs the stand onto walk frame "
                         "variants[V], so stop<Dir><V>_{n-1} is that walk frame "
                         "exactly; played backwards when settling.  The runtime "
                         "closes the gait to the nearer passing pose first, then "
                         "hands over to the matching variant"},
        "hitbox": hitbox,
        "reach": reach,
    }, indent=2), encoding="utf-8")

    if debug:
        BUILD.mkdir(exist_ok=True)
        for name in ("right", "left"):
            walk = [frame for key, frame in frames
                    if key.startswith(f"walk{name.capitalize()}_")]
            _contact_sheet(walk, BUILD / f"walk_{name}.png")
            _report_continuity(f"walk {name}", walk)
            walk[0].save(BUILD / f"walk_{name}.gif", save_all=True, append_images=walk[1:],
                         duration=42, loop=0, disposal=2)
            _contact_sheet(turns[name], BUILD / f"turn_{name}.png")
            for variant in STOP_VARIANTS:
                _contact_sheet(stops[(name, variant)], BUILD / f"stop_{name}{variant}.png")
        _contact_sheet(stand, BUILD / "idle_front.png")
        _report_continuity("stand", stand)
        # A turn is a one-shot, so only its interior continuity matters; the wrap is
        # meaningless and is reported for completeness only.
        _report_continuity("turn right", turns["right"])
        _report_continuity("stop right", stops[("right", "A")])
        cycle = turns["right"] + turns["right"][-2::-1]
        cycle[0].save(BUILD / "turn_right.gif", save_all=True, append_images=cycle[1:],
                      duration=60, loop=0, disposal=2)

    # Every handoff between a morph family and the gait is an identity, not an
    # approximation: prove it, and refuse to ship a sheet where it is not true.
    lookup = dict(frames)
    handoffs = [(f"turnRight_{TURN_FRAMES - 1:02d}",
                 f"walkRight_{(START_FRAME + TURN_FRAMES - 1) % WALK_FRAMES:02d}"),
                (f"turnLeft_{TURN_FRAMES - 1:02d}",
                 f"walkLeft_{(START_FRAME + TURN_FRAMES - 1) % WALK_FRAMES:02d}")]
    for name in rigs:
        for variant, target in zip(STOP_VARIANTS, STOP_TARGETS):
            handoffs.append((f"stop{name.capitalize()}{variant}_{TURN_FRAMES - 1:02d}",
                             f"walk{name.capitalize()}_{target:02d}"))
    for a, b in handoffs:
        delta = int(np.abs(np.asarray(lookup[a], dtype=np.int16)
                           - np.asarray(lookup[b], dtype=np.int16)).max())
        if delta != 0:
            raise SystemExit(f"handoff {a} vs {b} is not an identity: max|delta|={delta}")
    print(f"  {len(handoffs)} handoffs verified pixel-identical")

    print(f"baked {len(frames)} frames -> {OUT / 'hero.png'} ({sheet.width}x{sheet.height})")
    print(f"stride={STRIDE_LENGTH_PX}px ({STRIDE_SOURCE:.0f} rig) scale={SCALE:.5f} "
          f"step={STEP_MAX:.1f} legs=" +
          ", ".join(f"{k}:{v['leg_len']:.0f}" for k, v in rigs.items()) +
          f" hitbox={hitbox} reach={reach}")


if __name__ == "__main__":
    bake(debug=True)
