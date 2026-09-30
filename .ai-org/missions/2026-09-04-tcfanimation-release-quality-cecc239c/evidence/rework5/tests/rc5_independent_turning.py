from __future__ import annotations

import hashlib
import json
import sys
from enum import Enum
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


SCRIPT = Path(__file__).resolve()
REPOSITORY = SCRIPT.parents[6]
PROJECT = REPOSITORY / "TCFAnimation"
MISSION = SCRIPT.parents[3]
sys.path.insert(0, str(PROJECT / "FrameExtraction"))

import extract_directional_turns  # noqa: E402
import validate_release  # noqa: E402


MOVING_LIMITS = {
    "head": 6.0,
    "torso": 14.0,
    "shoulder": 10.0,
    "waist": 10.0,
    "baseline": 16.0,
    "height": 18.0,
}


class BoundaryKind(Enum):
    STATIONARY = "stationary"
    TURNING = "turning"
    WALKING = "walking"


def require(condition: bool, message: str) -> None:
    if not condition:
        raise RuntimeError(message)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest().upper()


def rgba_pixel_hash(path: Path) -> str:
    with Image.open(path) as image:
        pixels = np.asarray(image.convert("RGBA"))
    return hashlib.sha256(pixels.tobytes()).hexdigest().upper()


def old_label_based_disposition(
    label: str,
    values: dict[str, float],
) -> tuple[bool, str]:
    for key in ("head", "torso"):
        if values[key] > MOVING_LIMITS[key]:
            return False, key
    if "walk" in label.casefold():
        for key in ("shoulder", "waist"):
            if values[key] > MOVING_LIMITS[key]:
                return False, key
    for key in ("baseline", "height"):
        if values[key] > MOVING_LIMITS[key]:
            return False, key
    return True, "pass"


def explicit_kind_disposition(
    kind: BoundaryKind,
    values: dict[str, float],
) -> tuple[bool, str]:
    if kind is BoundaryKind.STATIONARY:
        raise RuntimeError("This focused control requires a moving boundary.")
    for key in ("head", "torso", "shoulder", "waist", "baseline", "height"):
        if values[key] > MOVING_LIMITS[key]:
            return False, key
    return True, "pass"


def load_bgra(relative_path: str) -> np.ndarray:
    frame = cv2.imread(
        str(PROJECT / relative_path),
        cv2.IMREAD_UNCHANGED,
    )
    require(frame is not None, f"Cannot decode {relative_path}.")
    require(frame.shape == (864, 512, 4), f"Unexpected shape: {relative_path}")
    return frame


def turning_mutation(before: np.ndarray, after: np.ndarray) -> np.ndarray:
    mutated = after.copy()
    rgb = mutated[:, :, :3]
    visible = np.max(rgb, axis=2) > 0
    hsv = cv2.cvtColor(rgb, cv2.COLOR_BGR2HSV)
    vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & visible
    )
    rows = np.indices(vest.shape)[0]
    mutated[vest & (rows < 335), :3] = (128, 128, 128)
    before_metrics = validate_release.visible_continuity_metrics(before)
    after_metrics = validate_release.visible_continuity_metrics(mutated)
    shoulder = abs(
        after_metrics["shoulder_y"] - before_metrics["shoulder_y"]
    )
    require(shoulder == 41, f"Mutation shoulder delta drifted: {shoulder}")
    return mutated


def compare_to_rc4_baseline() -> dict[str, object]:
    baseline_path = MISSION / "evidence" / "rework4" / "tests" / "release-after.json"
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    changed: list[str] = []
    details: dict[str, object] = {}
    for relative_path, expected in baseline["files"].items():
        current_path = PROJECT / relative_path
        current: dict[str, object] = {
            "bytes": current_path.stat().st_size,
            "sha256": sha256(current_path),
        }
        if relative_path.startswith("Frames/"):
            current["pixelSha256"] = rgba_pixel_hash(current_path)
        if current != expected:
            changed.append(relative_path)
            details[relative_path] = {
                "before": expected,
                "after": current,
            }
    require(
        changed == ["Frames/RightTurn/turn_1.png"],
        f"Unexpected RC4 baseline changes: {changed}",
    )
    return {"changed": changed, "details": details}


def main() -> None:
    failing_path = (
        MISSION / "evidence" / "rework5" / "developer" / "failing-before.json"
    )
    prior_path = (
        MISSION
        / "evidence"
        / "rework4"
        / "tests"
        / "independent-continuity.json"
    )
    failing = json.loads(failing_path.read_text(encoding="utf-8"))
    prior = json.loads(prior_path.read_text(encoding="utf-8"))
    prior_row = next(
        row
        for row in prior["boundaries"]
        if row["label"] == "Right_turn1_to_turn2"
    )
    recorded = failing["boundary"]
    require(recorded["shoulder"] == 16.0, "Failing shoulder record changed.")
    require(recorded["waist"] == 6.0, "Failing waist record changed.")
    require(recorded["passedByPriorGate"] is True, "Prior pass record changed.")
    require(prior_row["shoulder"] == 16.0, "Canonical prior shoulder changed.")
    require(prior_row["passed"] is True, "Canonical prior disposition changed.")

    prior_values = {
        key: float(recorded[key])
        for key in ("head", "torso", "shoulder", "waist", "baseline", "height")
    }
    old_result = old_label_based_disposition(recorded["label"], prior_values)
    new_result = explicit_kind_disposition(
        BoundaryKind.TURNING,
        prior_values,
    )
    require(old_result == (True, "pass"), f"Old bypass not reproduced: {old_result}")
    require(new_result == (False, "shoulder"), f"Explicit kind failed: {new_result}")
    print(
        "RC5_FAILING_BEFORE_PASS boundary=Right_turn1_to_turn2 "
        "shoulder=16 waist=6 old_label_gate=accepted "
        "explicit_turning_gate=rejected reason=shoulder"
    )

    source = (PROJECT / "FrameExtraction" / "validate_release.py").read_text(
        encoding="utf-8"
    )
    require(
        '"walk" in label.casefold()' not in source
        and "'walk' in label.casefold()" not in source,
        "Production validator still contains label-based walk gating.",
    )
    require(
        "class BoundaryKind(Enum):" in source
        and "kind: BoundaryKind" in source,
        "Production validator lacks validator-owned boundary kinds.",
    )

    boundaries = validate_release.continuity_boundaries()
    counts = {
        kind.value: sum(1 for boundary in boundaries if boundary.kind is kind)
        for kind in validate_release.BoundaryKind
    }
    require(
        counts == {"stationary": 27, "turning": 6, "walking": 12},
        f"Production boundary class counts changed: {counts}",
    )

    frames = {
        relative_path: load_bgra(relative_path)
        for relative_path in validate_release.EXPECTED_FRAME_PATHS
    }
    moving_maxima = {key: 0.0 for key in MOVING_LIMITS}
    right_rows: dict[str, dict[str, float]] = {}
    passed = 0
    for boundary in boundaries:
        measured = validate_release.validate_continuity_edge(
            boundary.label,
            frames[boundary.from_path],
            frames[boundary.to_path],
            boundary.kind,
        )
        passed += 1
        if boundary.kind is not validate_release.BoundaryKind.STATIONARY:
            for key in moving_maxima:
                moving_maxima[key] = max(moving_maxima[key], measured[key])
        if boundary.label.startswith("Right_") and "walk" not in boundary.label:
            right_rows[boundary.label] = {
                key: measured[key] for key in MOVING_LIMITS
            }
    for key, maximum in moving_maxima.items():
        require(
            maximum <= MOVING_LIMITS[key],
            f"Moving maximum exceeds limit: {key}={maximum}",
        )
    current = right_rows["Right_turn1_to_turn2"]
    require(
        current["shoulder"] == 10.0 and current["waist"] == 0.0,
        f"Current right transition metrics changed: {current}",
    )
    print(
        "RC5_BOUNDARY_MATRIX_PASS total=45 stationary=27 turning=6 walking=12 "
        f"head_max={moving_maxima['head']:.6f} "
        f"torso_max={moving_maxima['torso']:.6f} "
        f"shoulder_max={moving_maxima['shoulder']:.0f} "
        f"waist_max={moving_maxima['waist']:.0f} "
        f"baseline_max={moving_maxima['baseline']:.0f} "
        f"height_max={moving_maxima['height']:.0f}"
    )
    print(
        "RC5_RIGHT_TRANSITION_PASS boundary=Right_turn1_to_turn2 "
        f"head={current['head']:.6f} torso={current['torso']:.6f} "
        f"shoulder={current['shoulder']:.0f} waist={current['waist']:.0f} "
        f"baseline={current['baseline']:.0f} height={current['height']:.0f}"
    )

    before = frames["Frames/RightTurn/turn_1.png"]
    after = frames["Frames/RightTurn/turn_2.png"]
    mutation = turning_mutation(before, after)
    rejected: list[str] = []
    for label, kind, expected_kind in (
        (
            "diagnostic_walk_text_must_not_change_turning_kind",
            validate_release.BoundaryKind.TURNING,
            "turning",
        ),
        (
            "diagnostic_without_walk_must_not_change_walking_kind",
            validate_release.BoundaryKind.WALKING,
            "walking",
        ),
    ):
        try:
            validate_release.validate_continuity_edge(
                label,
                before,
                mutation,
                kind,
            )
        except RuntimeError as exception:
            message = str(exception)
            require(
                f"Moving {expected_kind} boundary" in message
                and "shoulder/waist landmark continuity" in message,
                f"Wrong rejection for {label}: {message}",
            )
            rejected.append(kind.value)
        else:
            raise RuntimeError(f"Production validator accepted {label}.")
    require(rejected == ["turning", "walking"], f"Negative results: {rejected}")
    print(
        "RC5_CLASSIFICATION_BYPASS_NEGATIVE_PASS mutation_shoulder=41 "
        "turning_with_walk_label=rejected walking_without_walk_label=rejected"
    )

    require(
        extract_directional_turns.RIGHT_CONFIG.final_translations
        == ((0, 0), (0, -6), (2, 0)),
        "Right-turn integer translations changed.",
    )
    baseline_delta = compare_to_rc4_baseline()
    changed_detail = baseline_delta["details"]["Frames/RightTurn/turn_1.png"]
    print(
        "RC5_EXPECTED_ASSET_DELTA_PASS changed=1 "
        "path=Frames/RightTurn/turn_1.png "
        f"before_sha256={changed_detail['before']['sha256']} "
        f"after_sha256={changed_detail['after']['sha256']} "
        "translation=(0,-6)"
    )

    output = SCRIPT.parent / "turning-validation.json"
    output.write_text(
        json.dumps(
            {
                "failingBefore": {
                    "boundary": recorded["label"],
                    "shoulder": recorded["shoulder"],
                    "waist": recorded["waist"],
                    "oldLabelGate": "accepted",
                    "explicitTurningGate": "rejected",
                },
                "boundaryCounts": counts,
                "movingMaxima": {
                    key: round(value, 6)
                    for key, value in moving_maxima.items()
                },
                "rightDirectional": right_rows,
                "classificationNegativeKinds": rejected,
                "rightTranslations": [
                    list(item)
                    for item in extract_directional_turns.RIGHT_CONFIG.final_translations
                ],
                "baselineDelta": baseline_delta,
                "passedBoundaries": passed,
            },
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    print(f"RC5_INDEPENDENT_TURNING_PASS checks=7 output={output}")


if __name__ == "__main__":
    main()
