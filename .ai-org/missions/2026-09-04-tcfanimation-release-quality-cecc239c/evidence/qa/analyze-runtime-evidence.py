from __future__ import annotations

import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


ROOT = Path(
    r"C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions"
    r"\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa"
)
RUNTIME = ROOT / "runtime"
OUT = ROOT / "runtime-contact-sheets"

SETS = {
    "directional-entry": "directional-left-entry-*.png",
    "directional-reversal": "directional-reversal-to-left-*.png",
    "directional-boundaries": "directional-*.png",
    "clap-sequence": "clap-step-*.png",
    "clap-interruption": "clap-*.png",
    "cross-entry": "cross-entry-*.png",
    "cross-release": "cross-release-*.png",
    "cross-lock-recovery": "cross-*.png",
    "dialogue": "dialogue-*.png",
    "fullscreen": "fullscreen-*.png",
    "speed": "speed-*.png",
    "w-noop": "w-noop-*.png",
}


def tile(path: Path) -> Image.Image:
    image = Image.open(path).convert("RGB")
    image.thumbnail((480, 300), Image.Resampling.LANCZOS)
    canvas = Image.new("RGB", (500, 340), (45, 45, 45))
    canvas.paste(image, ((500 - image.width) // 2, 34))
    ImageDraw.Draw(canvas).text(
        (8, 10), path.stem, fill="white", font=ImageFont.load_default()
    )
    return canvas


def make_sheet(paths: list[Path], path: Path, columns: int = 3) -> None:
    tiles = [tile(item) for item in paths]
    rows = (len(tiles) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * 500, rows * 340), (80, 80, 80))
    for index, item in enumerate(tiles):
        sheet.paste(item, ((index % columns) * 500, (index // columns) * 340))
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)


def pixel_delta(left: Path, right: Path) -> dict[str, object]:
    a = np.asarray(Image.open(left).convert("RGB"))
    b = np.asarray(Image.open(right).convert("RGB"))
    if a.shape != b.shape:
        return {"same_shape": False, "left_shape": a.shape, "right_shape": b.shape}
    changed = np.any(a != b, axis=2)
    return {
        "same_shape": True,
        "changed_pixels": int(changed.sum()),
        "max_channel_delta": int(
            np.abs(a.astype(np.int16) - b.astype(np.int16)).max()
        ),
        "left_sha256": hashlib.sha256(left.read_bytes()).hexdigest().upper(),
        "right_sha256": hashlib.sha256(right.read_bytes()).hexdigest().upper(),
    }


def main() -> None:
    results: dict[str, object] = {}
    for name, pattern in SETS.items():
        paths = sorted(RUNTIME.glob(pattern))
        if paths:
            make_sheet(paths, OUT / f"{name}.png")
            results[name] = [path.name for path in paths]

    results["w_noop_delta"] = pixel_delta(
        RUNTIME / "w-noop-before.png", RUNTIME / "w-noop-after.png"
    )
    results["runtime_png_count"] = len(list(RUNTIME.glob("*.png")))
    (ROOT / "runtime-evidence-summary.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8"
    )
    print(
        "RUNTIME_EVIDENCE_ANALYZED "
        f"screenshots={results['runtime_png_count']} "
        f"w_changed={results['w_noop_delta'].get('changed_pixels')}"
    )


if __name__ == "__main__":
    main()
