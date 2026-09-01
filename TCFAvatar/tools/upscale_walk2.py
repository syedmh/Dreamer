"""Super-resolve `Walking2.png` so the turn frames stop being the soft link.

The turn is the only part of the character that cannot be drawn from good art.
`Walking.png` holds 326px profile figures and the stand is a 959px photograph, but
the three-quarter views between them exist only in `Walking2.png`, whose figures
are **180px tall**.  Baked to a 650px character that is a 3.6x enlargement, and it
shows: the head is ~30px in the source, so the eyes arrive as dark smudges and the
face reads as a different, gaunter man for the eight frames of the turn.

Sharpening cannot fix that.  Rendering one turn frame at 85%, 40% and 0%
intermediate unsharp produced three indistinguishable images -- the softness is
missing information, not lost contrast.  So this reconstructs the information
instead, with Real-ESRGAN x4.

Use the **general** `RealESRGAN_x4plus` weights, not the `_anime_6B` ones.  The
anime variant is trained on cel art and treats this stylised 3D render as line
work: it flattened the face into a cartoon, moved the moustache, and wiped both
the damask weave on the waistcoat and the crest badge on the pocket.  The general
model keeps the identity and the fabric while roughly doubling edge detail
(mean |dx| 7.8 -> 14.0 on a turn figure).

Output is `art/Walking2-x4.png`, a plain 4x version of the pinned sheet with the
same layout.  `walk2_sheet` finds figures on the original and reads pixels from
this one, so segmentation, captions and cell geometry are untouched -- only the
sample count under each figure changes.  The pinned art itself is never modified.

    python tools/upscale_walk2.py [weights.pth]

Takes several minutes on CPU and is cached; delete the output to force a redo.
"""

from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "Walking2.png"
TARGET = ROOT / "art" / "Walking2-x4.png"
WEIGHTS = ROOT / ".cache" / "RealESRGAN_x4plus.pth"

SCALE = 4
# The sheet is 1536x1024; whole-image inference would allocate a 6144x4096 feature
# stack per layer.  Tiles keep peak memory near a few hundred MB, and the overlap
# is discarded so no seam can land inside a figure.
TILE = 256
OVERLAP = 32


def _build_model(torch, nn, nb=6):
    """RRDBNet -- the architecture the Real-ESRGAN x4 weights were trained as.
    `nb` is the number of RRDB blocks, inferred from the checkpoint: 6 for the
    anime variant, 23 for the general one.  Written out here rather than pulled
    from `basicsr`, whose import chain breaks against current torchvision over a
    moved functional module."""

    class ResidualDenseBlock(nn.Module):
        def __init__(self, nf=64, gc=32):
            super().__init__()
            self.conv1 = nn.Conv2d(nf, gc, 3, 1, 1)
            self.conv2 = nn.Conv2d(nf + gc, gc, 3, 1, 1)
            self.conv3 = nn.Conv2d(nf + 2 * gc, gc, 3, 1, 1)
            self.conv4 = nn.Conv2d(nf + 3 * gc, gc, 3, 1, 1)
            self.conv5 = nn.Conv2d(nf + 4 * gc, nf, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, inplace=True)

        def forward(self, x):
            x1 = self.lrelu(self.conv1(x))
            x2 = self.lrelu(self.conv2(torch.cat((x, x1), 1)))
            x3 = self.lrelu(self.conv3(torch.cat((x, x1, x2), 1)))
            x4 = self.lrelu(self.conv4(torch.cat((x, x1, x2, x3), 1)))
            x5 = self.conv5(torch.cat((x, x1, x2, x3, x4), 1))
            return x5 * 0.2 + x

    class RRDB(nn.Module):
        def __init__(self, nf, gc=32):
            super().__init__()
            self.rdb1 = ResidualDenseBlock(nf, gc)
            self.rdb2 = ResidualDenseBlock(nf, gc)
            self.rdb3 = ResidualDenseBlock(nf, gc)

        def forward(self, x):
            return self.rdb3(self.rdb2(self.rdb1(x))) * 0.2 + x

    class RRDBNet(nn.Module):
        def __init__(self, in_ch=3, out_ch=3, nf=64, nb=6, gc=32):
            super().__init__()
            self.conv_first = nn.Conv2d(in_ch, nf, 3, 1, 1)
            self.body = nn.Sequential(*[RRDB(nf, gc) for _ in range(nb)])
            self.conv_body = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_up1 = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_up2 = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_hr = nn.Conv2d(nf, nf, 3, 1, 1)
            self.conv_last = nn.Conv2d(nf, out_ch, 3, 1, 1)
            self.lrelu = nn.LeakyReLU(0.2, inplace=True)

        def forward(self, x):
            feat = self.conv_first(x)
            feat = feat + self.conv_body(self.body(feat))
            feat = self.lrelu(self.conv_up1(
                nn.functional.interpolate(feat, scale_factor=2, mode="nearest")))
            feat = self.lrelu(self.conv_up2(
                nn.functional.interpolate(feat, scale_factor=2, mode="nearest")))
            return self.conv_last(self.lrelu(self.conv_hr(feat)))

    return RRDBNet(nb=nb)


def load_model(weights):
    """The network with its weights, sized to whatever checkpoint was handed over."""
    import re

    import torch
    from torch import nn

    blob = torch.load(weights, map_location="cpu", weights_only=True)
    state = blob.get("params_ema") or blob.get("params") or blob
    nb = max((int(m.group(1)) for k in state
              for m in [re.match(r"body\.(\d+)\.", k)] if m), default=5) + 1
    model = _build_model(torch, nn, nb=nb)
    model.load_state_dict(state, strict=True)
    model.eval()
    return model


def _infer_tiled(torch, model, rgb):
    """Run the network over the sheet in overlapping tiles, keeping only each
    tile's interior so a boundary never falls inside a figure."""
    h, w, _ = rgb.shape
    out = np.zeros((h * SCALE, w * SCALE, 3), dtype=np.float32)

    ys = list(range(0, h, TILE))
    xs = list(range(0, w, TILE))
    total = len(ys) * len(xs)
    done = 0

    for y in ys:
        for x in xs:
            y0, y1 = max(0, y - OVERLAP), min(h, y + TILE + OVERLAP)
            x0, x1 = max(0, x - OVERLAP), min(w, x + TILE + OVERLAP)
            tile = rgb[y0:y1, x0:x1]

            inp = torch.from_numpy(tile.transpose(2, 0, 1)[None])
            with torch.no_grad():
                res = model(inp)[0].clamp(0, 1).numpy().transpose(1, 2, 0)

            # Trim the overlap back off, in output pixels.
            ty0, tx0 = (y - y0) * SCALE, (x - x0) * SCALE
            keep_h = min(TILE, h - y) * SCALE
            keep_w = min(TILE, w - x) * SCALE
            out[y * SCALE: y * SCALE + keep_h,
                x * SCALE: x * SCALE + keep_w] = res[ty0: ty0 + keep_h,
                                                     tx0: tx0 + keep_w]
            done += 1
            print(f"  tile {done}/{total}", end="\r", flush=True)
    print()
    return out


def main():
    weights = Path(sys.argv[1]) if len(sys.argv) > 1 else WEIGHTS
    if TARGET.exists():
        print(f"{TARGET.relative_to(ROOT)} already exists -- delete it to rebuild")
        return

    if not weights.exists():
        raise SystemExit(
            f"missing {weights}\n"
            "download the Real-ESRGAN x4 weights from the project's releases")

    import torch

    model = load_model(weights)
    print(f"model {weights.name}")

    src = Image.open(SOURCE).convert("RGB")
    rgb = np.asarray(src, dtype=np.float32) / 255.0
    print(f"source {src.size} -> {src.width * SCALE}x{src.height * SCALE}")

    out = _infer_tiled(torch, model, rgb)

    TARGET.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray((out * 255.0 + 0.5).astype(np.uint8), "RGB").save(TARGET)
    print(f"wrote {TARGET.relative_to(ROOT)}")


if __name__ == "__main__":
    sys.exit(main())
