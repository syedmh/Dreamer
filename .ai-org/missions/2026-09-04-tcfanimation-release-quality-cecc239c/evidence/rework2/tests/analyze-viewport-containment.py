from __future__ import annotations

import json
from pathlib import Path

import numpy as np
from PIL import Image


ROOT = Path(__file__).resolve().parent / "viewport-containment"
records = json.loads((ROOT / "matrix-input.json").read_text(encoding="utf-8"))
failures: list[str] = []

for record in records:
    path = Path(record["path"])
    pixels = np.asarray(Image.open(path).convert("RGB"))
    height, width = pixels.shape[:2]
    visible = np.any(pixels != 0, axis=2)
    ys, xs = np.where(visible)
    if len(xs) == 0:
        failures.append(f"{record['name']}: no rendered content")
        bbox = None
    else:
        bbox = [int(xs.min()), int(ys.min()), int(xs.max()), int(ys.max())]

    border_counts = {
        "top": int(visible[0].sum()),
        "bottom": int(visible[-1].sum()),
        "left": int(visible[:, 0].sum()),
        "right": int(visible[:, -1].sum()),
    }
    expected = (record["expectedWidth"], record["expectedHeight"])
    if (width, height) != expected:
        failures.append(
            f"{record['name']}: expected {expected}, got {(width, height)}"
        )
    if any(border_counts.values()):
        failures.append(
            f"{record['name']}: rendered content touches border {border_counts}"
        )
    record.update(
        {
            "width": width,
            "height": height,
            "bbox": bbox,
            "borderNonblack": border_counts,
            "sha256": __import__("hashlib").sha256(path.read_bytes()).hexdigest().upper(),
            "passed": (
                (width, height) == expected
                and bbox is not None
                and not any(border_counts.values())
            ),
        }
    )
    print(
        f"VIEWPORT_CASE name={record['name']} size={width}x{height} "
        f"bbox={bbox} border={border_counts} pass={record['passed']}"
    )

summary = {
    "passed": not failures,
    "caseCount": len(records),
    "failures": failures,
    "records": records,
}
(ROOT / "viewport-containment-results.json").write_text(
    json.dumps(summary, indent=2),
    encoding="utf-8",
)
print(
    f"VIEWPORT_CONTAINMENT_RESULT cases={len(records)} "
    f"failed={len(failures)} pass={not failures}"
)
raise SystemExit(0 if not failures else 1)
