from __future__ import annotations

import argparse
import hashlib
import json
from dataclasses import dataclass
from enum import Enum
from pathlib import Path

import cv2
import numpy as np
from PIL import Image


SCRIPT = Path(__file__).resolve()
REPOSITORY = SCRIPT.parents[6]
PROJECT = REPOSITORY / "TCFAnimation"

SEAM_Y = 640
BLEND_TOP_Y = 600
STATIONARY_LIMITS = {
    "head": 4.0,
    "torso": 4.0,
    "baseline": 2.0,
    "silhouette": 0.12,
}
MOVING_LIMITS = {
    "head": 6.0,
    "torso": 14.0,
    "shoulder": 10.0,
    "waist": 10.0,
    "baseline": 16.0,
    "height": 18.0,
}

GROUPS = {
    "LeftTurn": tuple(f"turn_{index}.png" for index in range(3)),
    "RightTurn": tuple(f"turn_{index}.png" for index in range(3)),
    "LeftWalk": tuple(f"walk_{index:02d}.png" for index in range(6)),
    "RightWalk": tuple(f"walk_{index:02d}.png" for index in range(6)),
    "Clap": tuple(f"clap_{index:02d}.png" for index in range(6)),
    "CrossArm": tuple(f"cross_{index:02d}.png" for index in range(3)),
    "CrossArmRelease": tuple(
        f"release_{index:02d}.png" for index in range(6)
    ),
}
FRAME_PATHS = tuple(
    f"Frames/{group}/{name}"
    for group, names in GROUPS.items()
    for name in names
)

SOURCE_PATHS = (
    "LTurning.png",
    "RTurning.png",
    "RWalking2.png",
    "Clapping2.png",
    "CrossArm3.png",
    "CrossArm4.png",
)

PROTECTED_USER_ASSETS = (
    "Avatar.jpg",
    "Avatar.jpg.import",
    "Clapping.png",
    "Clapping.png.import",
    "CrossArm.png",
    "CrossArm.png.import",
    "CrossArm2.png",
    "CrossArm2.png.import",
    "WalkRightSheet.png",
    "WalkRightSheet.png.import",
    "Waving.png",
    "Waving.png.import",
    "Waving2.png",
    "Waving2.png.import",
    "Waving3.png",
    "Waving3.png.import",
    "Waving4.png",
    "Waving4.png.import",
)


class BoundaryKind(Enum):
    STATIONARY = "stationary"
    TURNING = "turning"
    WALKING = "walking"


@dataclass(frozen=True)
class Boundary:
    label: str
    before_path: str
    after_path: str
    kind: BoundaryKind


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(1024 * 1024):
            digest.update(chunk)
    return digest.hexdigest().upper()


def load(relative_path: str) -> np.ndarray:
    with Image.open(PROJECT / relative_path) as image:
        return np.asarray(image.convert("RGBA")).copy()


def pixel_hash(frame: np.ndarray) -> str:
    return hashlib.sha256(frame.tobytes()).hexdigest().upper()


def snapshot(paths: tuple[str, ...]) -> dict[str, dict[str, object]]:
    result: dict[str, dict[str, object]] = {}
    for relative_path in paths:
        path = PROJECT / relative_path
        if not path.is_file():
            result[relative_path] = {"missing": True}
            continue
        entry: dict[str, object] = {
            "bytes": path.stat().st_size,
            "sha256": sha256(path),
        }
        if relative_path in FRAME_PATHS:
            entry["pixelSha256"] = pixel_hash(load(relative_path))
        result[relative_path] = entry
    return result


def write_snapshot(kind: str, output: Path) -> None:
    if kind == "release":
        paths = SOURCE_PATHS + FRAME_PATHS
    elif kind == "protected":
        paths = PROTECTED_USER_ASSETS
    elif kind == "tree":
        paths = tuple(
            path.relative_to(PROJECT).as_posix()
            for path in sorted(PROJECT.rglob("*"))
            if path.is_file()
        )
    else:
        raise RuntimeError(f"Unsupported snapshot kind: {kind}")
    payload = {"kind": kind, "files": snapshot(paths)}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    missing = sum(1 for value in payload["files"].values() if value.get("missing"))
    print(
        f"RC3_SNAPSHOT kind={kind} files={len(paths)} missing={missing} "
        f"path={output}"
    )


def compare_snapshots(before: Path, after: Path) -> None:
    left = json.loads(before.read_text(encoding="utf-8"))
    right = json.loads(after.read_text(encoding="utf-8"))
    if left["kind"] != right["kind"]:
        raise RuntimeError("Snapshot kinds differ.")
    left_files = left["files"]
    right_files = right["files"]
    changed = [
        path
        for path in sorted(set(left_files) | set(right_files))
        if left_files.get(path) != right_files.get(path)
    ]
    if changed:
        raise RuntimeError(f"Snapshot drift: {changed}")
    print(
        f"RC3_SNAPSHOT_COMPARE kind={left['kind']} "
        f"files={len(left_files)} changed=0"
    )


def continuity_boundaries() -> tuple[Boundary, ...]:
    boundaries: list[Boundary] = []
    for side in ("Left", "Right"):
        boundaries.extend(
                (
                    Boundary(
                    f"{side}_front_to_turn1",
                    f"Frames/{side}Turn/turn_0.png",
                    f"Frames/{side}Turn/turn_1.png",
                    BoundaryKind.TURNING,
                ),
                Boundary(
                    f"{side}_turn1_to_turn2",
                    f"Frames/{side}Turn/turn_1.png",
                    f"Frames/{side}Turn/turn_2.png",
                    BoundaryKind.TURNING,
                ),
                Boundary(
                    f"{side}_turn2_to_walk0",
                    f"Frames/{side}Turn/turn_2.png",
                    f"Frames/{side}Walk/walk_00.png",
                    BoundaryKind.TURNING,
                ),
            )
        )
        for index in range(6):
            boundaries.append(
                Boundary(
                    f"{side}_walk{index:02d}_to_{(index + 1) % 6:02d}",
                    f"Frames/{side}Walk/walk_{index:02d}.png",
                    f"Frames/{side}Walk/walk_{(index + 1) % 6:02d}.png",
                    BoundaryKind.WALKING,
                )
            )

    boundaries.append(
        Boundary(
            "front_handoff_left_to_right",
            "Frames/LeftTurn/turn_0.png",
            "Frames/RightTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    clap_sequence = (0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0)
    boundaries.append(
        Boundary(
            "front_to_clap00",
            "Frames/LeftTurn/turn_0.png",
            "Frames/Clap/clap_00.png",
            BoundaryKind.STATIONARY,
        )
    )
    for index, (left, right) in enumerate(
        zip(clap_sequence, clap_sequence[1:])
    ):
        boundaries.append(
            Boundary(
                f"clap_step_{index:02d}_{left}_to_{right}",
                f"Frames/Clap/clap_{left:02d}.png",
                f"Frames/Clap/clap_{right:02d}.png",
                BoundaryKind.STATIONARY,
            )
        )
    boundaries.append(
        Boundary(
            "clap_final_to_front",
            "Frames/Clap/clap_00.png",
            "Frames/LeftTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    boundaries.extend(
        (
            Boundary(
                "front_to_cross00",
                "Frames/LeftTurn/turn_0.png",
                "Frames/CrossArm/cross_00.png",
                BoundaryKind.STATIONARY,
            ),
            Boundary(
                "cross00_to_cross01",
                "Frames/CrossArm/cross_00.png",
                "Frames/CrossArm/cross_01.png",
                BoundaryKind.STATIONARY,
            ),
            Boundary(
                "cross01_to_cross02",
                "Frames/CrossArm/cross_01.png",
                "Frames/CrossArm/cross_02.png",
                BoundaryKind.STATIONARY,
            ),
            Boundary(
                "cross02_to_release00",
                "Frames/CrossArm/cross_02.png",
                "Frames/CrossArmRelease/release_00.png",
                BoundaryKind.STATIONARY,
            ),
        )
    )
    for index in range(5):
        boundaries.append(
            Boundary(
                f"release{index:02d}_to_{index + 1:02d}",
                f"Frames/CrossArmRelease/release_{index:02d}.png",
                f"Frames/CrossArmRelease/release_{index + 1:02d}.png",
                BoundaryKind.STATIONARY,
            )
        )
    boundaries.append(
        Boundary(
            "release05_to_front",
            "Frames/CrossArmRelease/release_05.png",
            "Frames/LeftTurn/turn_0.png",
            BoundaryKind.STATIONARY,
        )
    )
    if len(boundaries) != 45:
        raise RuntimeError(f"Independent boundary count is {len(boundaries)}.")
    return tuple(boundaries)


def metrics(frame: np.ndarray) -> dict[str, float]:
    rgb = frame[:, :, :3]
    visible = np.max(rgb, axis=2) > 0
    ys, _ = np.where(visible)
    if not ys.size:
        raise RuntimeError("Frame has no visible pixels.")
    top = int(ys.min())
    bottom = int(ys.max())
    height = bottom - top + 1
    rows = np.indices(visible.shape)[0]
    head = visible & (rows <= top + round(height * 0.20))
    torso = visible & (
        (rows >= top + round(height * 0.30))
        & (rows <= top + round(height * 0.66))
    )
    head_y, head_x = np.where(head)
    torso_y, torso_x = np.where(torso)

    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & visible
    )
    vest_y, _ = np.where(vest)
    if not vest_y.size:
        raise RuntimeError("Frame has no independently measured vest pixels.")
    return {
        "head_x": float(head_x.mean()),
        "head_y": float(head_y.mean()),
        "torso_x": float(torso_x.mean()),
        "torso_y": float(torso_y.mean()),
        "shoulder_y": float(np.percentile(vest_y, 5, method="nearest")),
        "waist_y": float(np.percentile(vest_y, 95, method="nearest")),
        "baseline_y": float(bottom),
        "height": float(height),
    }


def silhouette_delta(before: np.ndarray, after: np.ndarray) -> float:
    before_mask = (
        np.max(before[BLEND_TOP_Y:SEAM_Y, :, :3], axis=2) > 0
    )
    after_mask = np.max(after[BLEND_TOP_Y:SEAM_Y, :, :3], axis=2) > 0
    union = int(np.count_nonzero(before_mask | after_mask))
    if union == 0:
        return 0.0
    return int(np.count_nonzero(before_mask ^ after_mask)) / union


def deltas(before: np.ndarray, after: np.ndarray) -> dict[str, float]:
    left = metrics(before)
    right = metrics(after)
    return {
        "head": max(
            abs(right["head_x"] - left["head_x"]),
            abs(right["head_y"] - left["head_y"]),
        ),
        "torso": max(
            abs(right["torso_x"] - left["torso_x"]),
            abs(right["torso_y"] - left["torso_y"]),
        ),
        "shoulder": abs(right["shoulder_y"] - left["shoulder_y"]),
        "waist": abs(right["waist_y"] - left["waist_y"]),
        "baseline": abs(right["baseline_y"] - left["baseline_y"]),
        "height": abs(right["height"] - left["height"]),
        "silhouette": silhouette_delta(before, after),
    }


def disposition(
    values: dict[str, float],
    kind: BoundaryKind,
) -> tuple[bool, str]:
    if kind is BoundaryKind.STATIONARY:
        for key in ("head", "torso", "baseline", "silhouette"):
            if values[key] > STATIONARY_LIMITS[key]:
                return False, key
        return True, "pass"
    keys = ["head", "torso", "shoulder", "waist", "baseline", "height"]
    for key in keys:
        if values[key] > MOVING_LIMITS[key]:
            return False, key
    return True, "pass"


def shift_frame(frame: np.ndarray, delta_x: int, delta_y: int) -> np.ndarray:
    height, width = frame.shape[:2]
    shifted = np.zeros_like(frame)
    shifted[:, :, 3] = 255
    sx0 = max(0, -delta_x)
    sx1 = min(width, width - delta_x)
    sy0 = max(0, -delta_y)
    sy1 = min(height, height - delta_y)
    tx0 = sx0 + delta_x
    tx1 = sx1 + delta_x
    ty0 = sy0 + delta_y
    ty1 = sy1 + delta_y
    shifted[ty0:ty1, tx0:tx1, :3] = frame[sy0:sy1, sx0:sx1, :3]
    return shifted


def shift_band_x(
    frame: np.ndarray,
    top: int,
    bottom: int,
    delta_x: int,
) -> np.ndarray:
    shifted = frame.copy()
    shifted[top:bottom, :, :3] = 0
    shifted[top:bottom, delta_x:, :3] = frame[
        top:bottom, :-delta_x, :3
    ]
    return shifted


def shift_lower_y(frame: np.ndarray, top: int, delta_y: int) -> np.ndarray:
    shifted = frame.copy()
    shifted[top:, :, :3] = 0
    shifted[top + delta_y :, :, :3] = frame[top:-delta_y, :, :3]
    return shifted


def require_black(frame: np.ndarray, rect: tuple[int, int, int, int]) -> None:
    x0, y0, x1, y1 = rect
    if np.any(frame[y0:y1, x0:x1, :3]):
        raise RuntimeError("lower_silhouette_exclusion")


def require_dark_anchor(
    frame: np.ndarray,
    rect: tuple[int, int, int, int],
) -> None:
    x0, y0, x1, y1 = rect
    pixels = frame[y0:y1, x0:x1, :3]
    visible = np.max(pixels, axis=2) > 0
    if not np.any(visible) or int(np.max(pixels[visible])) > 96:
        raise RuntimeError("lost_anatomy")


def assert_rejected(name: str, expected: str, action) -> None:
    try:
        action()
    except RuntimeError as exception:
        if expected not in str(exception):
            raise RuntimeError(
                f"{name} failed for wrong reason: {exception}"
            ) from exception
        print(
            f"RC3_INDEPENDENT_ADVERSARIAL name={name} "
            f"rejected=true reason={expected}"
        )
        return
    raise RuntimeError(f"{name} was accepted.")


def validate_plate(frames: dict[str, np.ndarray]) -> None:
    canonical = frames["Frames/RightTurn/turn_0.png"]
    consumers = (
        "Frames/CrossArm/cross_02.png",
        *(
            f"Frames/CrossArmRelease/release_{index:02d}.png"
            for index in range(6)
        ),
    )
    canonical_rgb = canonical[SEAM_Y:, :, :3]
    canonical_visible = np.max(canonical_rgb, axis=2) > 0
    for path in consumers:
        rgb = frames[path][SEAM_Y:, :, :3]
        visible = np.max(rgb, axis=2) > 0
        if not np.array_equal(rgb, canonical_rgb):
            raise RuntimeError(f"row-640 RGB mismatch: {path}")
        if not np.array_equal(visible, canonical_visible):
            raise RuntimeError(f"row-640 visibility mismatch: {path}")
    print(
        "RC3_INDEPENDENT_PLATE seam_y=640 consumers=7 "
        "rgb_exact=7/7 visibility_exact=7/7"
    )


def validate_threshold_boundaries() -> int:
    checks = 0
    epsilon = 0.000001
    base = {
        "head": 0.0,
        "torso": 0.0,
        "shoulder": 0.0,
        "waist": 0.0,
        "baseline": 0.0,
        "height": 0.0,
        "silhouette": 0.0,
    }
    for key, limit in STATIONARY_LIMITS.items():
        exact = dict(base)
        exact[key] = limit
        if disposition(exact, BoundaryKind.STATIONARY) != (True, "pass"):
            raise RuntimeError(f"Stationary exact threshold rejected: {key}")
        checks += 1
        over = dict(base)
        over[key] = limit + epsilon
        if disposition(over, BoundaryKind.STATIONARY) != (False, key):
            raise RuntimeError(f"Stationary over threshold accepted: {key}")
        checks += 1
    for key, limit in MOVING_LIMITS.items():
        exact = dict(base)
        exact[key] = limit
        if disposition(exact, BoundaryKind.TURNING) != (True, "pass"):
            raise RuntimeError(f"Moving exact threshold rejected: {key}")
        checks += 1
        over = dict(base)
        over[key] = limit + epsilon
        if disposition(over, BoundaryKind.TURNING) != (False, key):
            raise RuntimeError(f"Moving over threshold accepted: {key}")
        checks += 1
    print(
        f"RC3_INDEPENDENT_THRESHOLDS cases={checks} "
        "exact_inclusive=true epsilon_over_rejected=true"
    )
    return checks


def validate_adversarial(frames: dict[str, np.ndarray]) -> None:
    attached = frames["Frames/LeftTurn/turn_1.png"].copy()
    attached[820:822, 195:207, :3] = (18, 18, 18)
    assert_rejected(
        "shoe_attached_dark_reflection_strip",
        "lower_silhouette_exclusion",
        lambda: require_black(attached, (191, 820, 258, 845)),
    )

    bridged = frames["Frames/LeftTurn/turn_1.png"].copy()
    bridged[820:824, 191:196, :3] = (48, 18, 42)
    bridged[820, 189:191, :3] = (48, 18, 42)
    assert_rejected(
        "one_pixel_bridged_cool_fragment",
        "lower_silhouette_exclusion",
        lambda: require_black(bridged, (191, 820, 258, 845)),
    )

    deleted = frames["Frames/Clap/clap_00.png"].copy()
    deleted[790:793, 206:209, :3] = 0
    assert_rejected(
        "dark_shoe_anchor_deletion",
        "lost_anatomy",
        lambda: require_dark_anchor(deleted, (206, 790, 209, 793)),
    )

    canonical = frames["Frames/RightTurn/turn_0.png"]
    plate = frames["Frames/CrossArm/cross_02.png"].copy()
    plate[SEAM_Y, 256, 0] ^= 1

    def plate_check() -> None:
        if not np.array_equal(
            plate[SEAM_Y:, :, :3],
            canonical[SEAM_Y:, :, :3],
        ):
            raise RuntimeError("plate_mismatch")

    assert_rejected(
        "shared_plate_pixel_mutation",
        "plate_mismatch",
        plate_check,
    )

    stationary_before = frames["Frames/LeftTurn/turn_0.png"]
    stationary_after = shift_frame(
        frames["Frames/RightTurn/turn_0.png"], 5, 0
    )

    def stationary_check() -> None:
        passed, reason = disposition(
            deltas(stationary_before, stationary_after),
            BoundaryKind.STATIONARY,
        )
        if not passed:
            raise RuntimeError(f"stationary_continuity:{reason}")

    assert_rejected(
        "stationary_shift_5px",
        "stationary_continuity:head",
        stationary_check,
    )

    walk_before = frames["Frames/RightWalk/walk_00.png"]
    torso_after = shift_band_x(
        frames["Frames/RightWalk/walk_01.png"], 320, 640, 15
    )

    def torso_check() -> None:
        passed, reason = disposition(
            deltas(walk_before, torso_after),
            BoundaryKind.WALKING,
        )
        if not passed:
            raise RuntimeError(f"walking_torso_continuity:{reason}")

    assert_rejected(
        "walking_torso_shift_15px",
        "walking_torso_continuity:torso",
        torso_check,
    )

    turning_before = frames["Frames/RightTurn/turn_1.png"]
    turning_after = frames["Frames/RightTurn/turn_2.png"].copy()
    rgb = turning_after[:, :, :3]
    visible = np.max(rgb, axis=2) > 0
    hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
    vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & visible
    )
    rows = np.indices(vest.shape)[0]
    turning_after[vest & (rows < 335), :3] = (128, 128, 128)

    def turning_check() -> None:
        values = deltas(turning_before, turning_after)
        if values["shoulder"] != 41:
            raise RuntimeError(
                f"turning_landmark_calibration:{values['shoulder']}"
            )
        passed, reason = disposition(values, BoundaryKind.TURNING)
        if not passed:
            raise RuntimeError(f"turning_landmark_continuity:{reason}")

    assert_rejected(
        "turning_landmark_shift_41px",
        "turning_landmark_continuity:shoulder",
        turning_check,
    )

    baseline_after = shift_lower_y(
        frames["Frames/RightWalk/walk_01.png"], 700, 17
    )

    def baseline_check() -> None:
        passed, reason = disposition(
            deltas(walk_before, baseline_after),
            BoundaryKind.WALKING,
        )
        if not passed:
            raise RuntimeError(f"walking_baseline_continuity:{reason}")

    assert_rejected(
        "walking_shoe_baseline_shift_17px",
        "walking_baseline_continuity:baseline",
        baseline_check,
    )


def validate_continuity(output: Path | None) -> None:
    frames = {path: load(path) for path in FRAME_PATHS}
    validate_plate(frames)
    rows: list[dict[str, object]] = []
    for boundary in continuity_boundaries():
        values = deltas(
            frames[boundary.before_path],
            frames[boundary.after_path],
        )
        passed, reason = disposition(values, boundary.kind)
        rows.append(
            {
                "label": boundary.label,
                "classification": boundary.kind.value,
                **{key: round(value, 6) for key, value in values.items()},
                "passed": passed,
                "reason": reason,
            }
        )
        if not passed:
            raise RuntimeError(
                "Independent boundary failed: "
                f"{boundary.label} reason={reason}"
            )
    stationary = [row for row in rows if row["classification"] == "stationary"]
    moving = [row for row in rows if row["classification"] != "stationary"]
    print(
        "RC3_INDEPENDENT_BOUNDARIES total=45 passed=45 failed=0 "
        f"stationary={len(stationary)} moving={len(moving)}"
    )
    threshold_cases = validate_threshold_boundaries()
    validate_adversarial(frames)
    if output is not None:
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(
            json.dumps(
                {
                    "seamY": SEAM_Y,
                    "plateConsumers": 7,
                    "boundaries": rows,
                    "thresholdCases": threshold_cases,
                    "adversarialCases": 8,
                },
                indent=2,
            ),
            encoding="utf-8",
        )
    print(
        "RC3_INDEPENDENT_CONTINUITY_PASS plate=7 boundaries=45 "
        "threshold_cases=20 adversarial=8"
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)
    snapshot_parser = subparsers.add_parser("snapshot")
    snapshot_parser.add_argument(
        "--kind", choices=("release", "protected", "tree"), required=True
    )
    snapshot_parser.add_argument("--output", type=Path, required=True)
    compare_parser = subparsers.add_parser("compare")
    compare_parser.add_argument("--before", type=Path, required=True)
    compare_parser.add_argument("--after", type=Path, required=True)
    continuity_parser = subparsers.add_parser("continuity")
    continuity_parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.command == "snapshot":
        write_snapshot(args.kind, args.output)
    elif args.command == "compare":
        compare_snapshots(args.before, args.after)
    else:
        validate_continuity(args.output)


if __name__ == "__main__":
    main()
