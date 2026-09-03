"""Optical-flow in-betweening for the authored walk and turn art.

The art in ``Frames/`` is six walk poses per stride.  Played straight that is
about three frames a second at the game's walking speed, which reads as a
stutter on a large screen, so the six authored poses are treated as keyframes
and the frames between them are synthesised.

A straight cross-fade is not an option: it was tried on earlier art and dissolves
one shoe through another instead of moving it.  Here each pair is registered with
DIS optical flow in both directions, and the intermediate frame is built by
warping *both* neighbours to the intermediate geometry and only then blending.
Because both sides are warped onto the same pose before mixing, matching
features land on top of each other and there is nothing to ghost.

Everything is warped premultiplied.  Warping straight colour would drag black
background into the silhouette edge, and warping alpha separately from colour
would tear the two apart at exactly the boundary where it shows.
"""

from __future__ import annotations

import cv2
import numpy as np

_FLOW = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
_FLOW.setUseSpatialPropagation(True)
_FLOW.setFinestScale(0)
_FLOW.setPatchSize(8)
_FLOW.setPatchStride(3)
_FLOW.setGradientDescentIterations(48)
_FLOW.setVariationalRefinementIterations(12)


def _grey(rgba):
    """Luminance over black, which is what the renderer composited against."""
    a = rgba[..., 3].astype(np.float32) / 255.0
    lum = rgba[..., :3].astype(np.float32) @ np.array([0.299, 0.587, 0.114], np.float32)
    # Fold alpha in so the silhouette edge is a strong gradient the flow can lock to.
    return np.clip(lum * a, 0, 255).astype(np.uint8)


def flow(a, b):
    return _FLOW.calc(_grey(a), _grey(b), None)


def _warp(rgba, fx, fy):
    pre = rgba.astype(np.float32)
    pre[..., :3] *= pre[..., 3:4] / 255.0
    out = cv2.remap(pre, fx, fy, cv2.INTER_CUBIC, borderMode=cv2.BORDER_CONSTANT,
                    borderValue=(0.0, 0.0, 0.0, 0.0))
    return np.clip(out, 0, None)


def between(a, b, t, fab=None, fba=None):
    """The frame t of the way from a to b, with both sides warped onto it first."""
    if t <= 0.0:
        return a.copy()
    if t >= 1.0:
        return b.copy()
    fab = flow(a, b) if fab is None else fab
    fba = flow(b, a) if fba is None else fba

    h, w = a.shape[:2]
    gx, gy = np.meshgrid(np.arange(w, dtype=np.float32), np.arange(h, dtype=np.float32))

    # A pixel sitting at q in the intermediate frame came from about q - t*flow in a,
    # and from about q - (1-t)*flow in b.
    wa = _warp(a, gx - t * fab[..., 0], gy - t * fab[..., 1])
    wb = _warp(b, gx - (1.0 - t) * fba[..., 0], gy - (1.0 - t) * fba[..., 1])

    mix = (1.0 - t) * wa + t * wb
    alpha = mix[..., 3:4]
    colour = np.divide(mix[..., :3], np.maximum(alpha, 1e-3) / 255.0,
                       out=np.zeros_like(mix[..., :3]), where=alpha > 1.0)
    out = np.concatenate([colour, alpha], axis=2)
    return np.clip(out, 0, 255).astype(np.uint8)


def resample(keys, count, loop=True):
    """Stretch len(keys) authored poses to count frames of even spacing."""
    n = len(keys)
    span = n if loop else n - 1
    out = []
    for i in range(count):
        u = i * span / count if loop else i * span / (count - 1)
        lo = int(np.floor(u)) % n if loop else min(int(np.floor(u)), n - 1)
        t = u - np.floor(u)
        if t < 1e-6:
            out.append(keys[lo].copy())
            continue
        hi = (lo + 1) % n if loop else min(lo + 1, n - 1)
        out.append(between(keys[lo], keys[hi], float(t)))
    return out
