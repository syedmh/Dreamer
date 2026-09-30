from __future__ import annotations

import importlib.util
import json
from pathlib import Path

import numpy as np
from PIL import Image


SCRIPT = Path(__file__).resolve()
GENERATOR = (
    SCRIPT.parents[2] / "rework2" / "developer" / "generate_t3_t4_evidence.py"
)


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def file_names(path: Path) -> list[str]:
    return sorted(item.name for item in path.iterdir() if item.is_file())


def main() -> None:
    spec = importlib.util.spec_from_file_location(
        "canonical_visual_generator",
        GENERATOR,
    )
    require(spec is not None and spec.loader is not None, "Cannot load generator.")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    after = GENERATOR.parent / "after"
    expected_groups = sorted(f"{group}.png" for group in module.GROUPS)
    for directory in (
        "contact-sheets-native",
        "contact-sheets-2x",
        "contact-sheets-feet",
    ):
        require(
            file_names(after / directory) == expected_groups,
            f"{directory} inventory mismatch.",
        )

    frame_rows = json.loads(
        (after / "frame-metrics.json").read_text(encoding="utf-8")
    )
    boundary_rows = json.loads(
        (after / "boundary-metrics.json").read_text(encoding="utf-8")
    )
    summary = json.loads(
        (after / "visual-summary.json").read_text(encoding="utf-8")
    )
    expected_frame_paths = [
        f"Frames/{group}/{name}"
        for group, names in module.GROUPS.items()
        for name in names
    ]
    require(
        len(frame_rows) == 33
        and [row["path"] for row in frame_rows] == expected_frame_paths,
        "Frame metrics mismatch.",
    )
    require(
        len(boundary_rows) == 45
        and all(bool(row["pass"]) for row in boundary_rows),
        "Boundary metrics mismatch.",
    )
    stationary = [
        row for row in boundary_rows if row["classification"] == "stationary"
    ]
    turning = [
        row for row in boundary_rows if row["classification"] == "turning"
    ]
    walking = [
        row for row in boundary_rows if row["classification"] == "walking"
    ]
    require(
        (len(stationary), len(turning), len(walking)) == (27, 6, 12),
        "Boundary classification counts mismatch.",
    )
    for row in turning + walking:
        require(
            "shoulder_delta" in row
            and "waist_delta" in row
            and int(row["shoulder_delta"]) <= 10
            and int(row["waist_delta"]) <= 10,
            f"Moving landmark metrics mismatch: {row['boundary']}",
        )

    expected_boundary_files = [
        f"{index:02d}-{row['boundary'].replace('/', '-')}.png"
        for index, row in enumerate(boundary_rows)
    ]
    require(
        file_names(after / "boundaries") == expected_boundary_files,
        "Boundary sheet inventory mismatch.",
    )
    expected_aggregate = [
        "clap.png",
        "cross-release.png",
        "front-handoff.png",
        "left-directional.png",
        "right-directional.png",
    ]
    require(
        file_names(after / "boundary-sheets") == expected_aggregate,
        "Aggregate boundary sheet inventory mismatch.",
    )

    require(
        summary["frames"] == 33 and summary["boundaries"] == 45,
        "Visual summary counts mismatch.",
    )
    for key in (
        "all_boundaries_pass",
        "all_bounds_inside_canvas",
        "all_floor_negatives_black",
        "front_pixels_identical",
        "front_png_bytes_identical",
        "shared_plate_exact",
    ):
        require(summary[key] is True, f"Visual summary {key} is not true.")
    require(
        all(row["mismatch_pixels"] == 0 for row in summary["walk_mirrors"]),
        "Walk mirror mismatch.",
    )

    seam = module.EXPECTED_STATIONARY_PLATE_SEAM_Y
    require(seam == 640, f"Independent seam is {seam}.")
    production_seams = {
        "extract_cross_arm": module.extract_cross_arm.STATIONARY_PLATE_SEAM_Y,
        "validate_release": module.validate_release.STATIONARY_PLATE_SEAM_Y,
    }
    require(
        all(value == seam for value in production_seams.values()),
        f"Production seam mismatch: {production_seams}",
    )

    def load(relative_path: str) -> np.ndarray:
        with Image.open(module.PROJECT / relative_path) as image:
            return np.asarray(image.convert("RGBA")).copy()

    canonical = load("Frames/RightTurn/turn_0.png")
    shared_paths = [
        "Frames/CrossArm/cross_02.png",
        *(
            f"Frames/CrossArmRelease/release_{index:02d}.png"
            for index in range(6)
        ),
    ]
    canonical_rgb = canonical[seam:, :, :3]
    canonical_visible = np.max(canonical_rgb, axis=2) > 0
    for relative_path in shared_paths:
        frame_rgb = load(relative_path)[seam:, :, :3]
        require(
            np.array_equal(frame_rgb, canonical_rgb),
            f"Row-640 RGB mismatch: {relative_path}",
        )
        require(
            np.array_equal(
                np.max(frame_rgb, axis=2) > 0,
                canonical_visible,
            ),
            f"Row-640 visibility mismatch: {relative_path}",
        )

    results = {
        "seamY": seam,
        "productionSeams": production_seams,
        "frames": len(frame_rows),
        "boundaries": len(boundary_rows),
        "boundaryClasses": {
            "stationary": len(stationary),
            "turning": len(turning),
            "walking": len(walking),
        },
        "contactSheets": {"native": 7, "zoom2x": 7, "feet": 7},
        "individualBoundarySheets": len(expected_boundary_files),
        "aggregateBoundarySheets": len(expected_aggregate),
        "plateConsumers": len(shared_paths),
        "allChecksPassed": True,
    }
    output = SCRIPT.parent / "visual-evidence-verification.json"
    output.write_text(
        json.dumps(results, indent=2) + "\n",
        encoding="utf-8",
    )
    print(
        "VISUAL_EVIDENCE_VERIFY_PASS seam_y=640 frames=33 boundaries=45 "
        "stationary=27 turning=6 walking=12 "
        "contact_sheets=21 individual_boundary_sheets=45 "
        "aggregate_boundary_sheets=5 plate_consumers=7"
    )


if __name__ == "__main__":
    main()
