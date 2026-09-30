from __future__ import annotations

import argparse
import csv
import hashlib
import json
import sys
from enum import Enum
from pathlib import Path
from typing import NamedTuple

import cv2
import numpy as np
from PIL import Image, ImageDraw, ImageFont


SCRIPT = Path(__file__).resolve()
EVIDENCE_ROOT = SCRIPT.parent
REPOSITORY = SCRIPT.parents[6]
PROJECT = REPOSITORY / "TCFAnimation"
sys.path.insert(0, str(PROJECT / "FrameExtraction"))

import extract_cross_arm  # noqa: E402
import validate_release  # noqa: E402


GROUPS = validate_release.FRAME_GROUPS
FONT = ImageFont.load_default()
EXPECTED_STATIONARY_PLATE_SEAM_Y = 640
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


class BoundaryKind(Enum):
    STATIONARY = "stationary"
    TURNING = "turning"
    WALKING = "walking"


class Boundary(NamedTuple):
    label: str
    from_path: str
    to_path: str
    kind: BoundaryKind


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def load(relative_path: str) -> Image.Image:
    return Image.open(PROJECT / relative_path).convert("RGBA")


def visible_mask(image: Image.Image) -> np.ndarray:
    pixels = np.asarray(image)
    return np.any(pixels[:, :, :3] != 0, axis=2)


def frame_metrics(relative_path: str) -> dict[str, object]:
    image = load(relative_path)
    pixels = np.asarray(image)
    visible = visible_mask(image)
    ys, xs = np.where(visible)
    left = int(xs.min())
    right = int(xs.max())
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
    hsv = cv2.cvtColor(pixels[:, :, :3], cv2.COLOR_RGB2HSV)
    vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & visible
    )
    vest_y, _ = np.where(vest)
    if not vest_y.size:
        raise RuntimeError(
            f"{relative_path} has no independently measured vest pixels."
        )
    return {
        "path": relative_path,
        "sha256": sha256(PROJECT / relative_path),
        "width": image.width,
        "height": image.height,
        "alpha_min": int(pixels[:, :, 3].min()),
        "alpha_max": int(pixels[:, :, 3].max()),
        "nonblack_pixels": int(np.count_nonzero(visible)),
        "bbox_left": left,
        "bbox_top": top,
        "bbox_right": right,
        "bbox_bottom": bottom,
        "bbox_height": height,
        "head_x": round(float(head_x.mean()), 2),
        "head_y": round(float(head_y.mean()), 2),
        "torso_x": round(float(torso_x.mean()), 2),
        "torso_y": round(float(torso_y.mean()), 2),
        "shoulder_y": int(np.percentile(vest_y, 5, method="nearest")),
        "waist_y": int(np.percentile(vest_y, 95, method="nearest")),
        "baseline_y": bottom,
    }


def label_tile(image: Image.Image, text: str) -> Image.Image:
    tile = Image.new("RGB", (image.width, image.height + 34), (34, 34, 34))
    tile.paste(image.convert("RGB"), (0, 34))
    ImageDraw.Draw(tile).text((7, 10), text, fill=(255, 255, 255), font=FONT)
    return tile


def save_sheet(tiles: list[Image.Image], columns: int, path: Path) -> None:
    rows = (len(tiles) + columns - 1) // columns
    width = max(tile.width for tile in tiles)
    height = max(tile.height for tile in tiles)
    sheet = Image.new("RGB", (columns * width, rows * height), (64, 64, 64))
    for index, tile in enumerate(tiles):
        sheet.paste(tile, ((index % columns) * width, (index // columns) * height))
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.save(path)


def annotated_frame(
    image: Image.Image,
    metrics: dict[str, object],
) -> Image.Image:
    result = image.copy()
    draw = ImageDraw.Draw(result)
    draw.rectangle(
        (
            int(metrics["bbox_left"]),
            int(metrics["bbox_top"]),
            int(metrics["bbox_right"]),
            int(metrics["bbox_bottom"]),
        ),
        outline=(255, 0, 255, 255),
        width=2,
    )
    for x_key, y_key, color in (
        ("head_x", "head_y", (255, 255, 0, 255)),
        ("torso_x", "torso_y", (0, 255, 255, 255)),
    ):
        x = float(metrics[x_key])
        y = float(metrics[y_key])
        draw.line((x - 8, y, x + 8, y), fill=color, width=2)
        draw.line((x, y - 8, x, y + 8), fill=color, width=2)
    return result


def boundary_row(
    boundary: Boundary,
    metrics: dict[str, dict[str, object]],
) -> dict[str, object]:
    before = metrics[boundary.from_path]
    after = metrics[boundary.to_path]
    row = {
        "boundary": boundary.label,
        "classification": boundary.kind.value,
        "from": boundary.from_path,
        "to": boundary.to_path,
        "head_dx": round(float(after["head_x"]) - float(before["head_x"]), 2),
        "head_dy": round(float(after["head_y"]) - float(before["head_y"]), 2),
        "torso_dx": round(
            float(after["torso_x"]) - float(before["torso_x"]),
            2,
        ),
        "torso_dy": round(
            float(after["torso_y"]) - float(before["torso_y"]),
            2,
        ),
        "shoulder_delta": abs(
            int(after["shoulder_y"]) - int(before["shoulder_y"])
        ),
        "waist_delta": abs(
            int(after["waist_y"]) - int(before["waist_y"])
        ),
        "baseline_delta": abs(
            int(after["baseline_y"]) - int(before["baseline_y"])
        ),
        "height_delta": abs(
            int(after["bbox_height"]) - int(before["bbox_height"])
        ),
    }
    from_rgb = np.asarray(load(boundary.from_path))[:, :, :3].astype(
        np.int16
    )
    to_rgb = np.asarray(load(boundary.to_path))[:, :, :3].astype(np.int16)
    row["lower_band_difference"] = round(
        float(np.mean(np.abs(from_rgb[700:] - to_rgb[700:]))) / 255.0,
        6,
    )
    head = max(abs(float(row["head_dx"])), abs(float(row["head_dy"])))
    torso = max(abs(float(row["torso_dx"])), abs(float(row["torso_dy"])))
    if boundary.kind is BoundaryKind.STATIONARY:
        passed = (
            head <= STATIONARY_LIMITS["head"]
            and torso <= STATIONARY_LIMITS["torso"]
            and int(row["baseline_delta"]) <= STATIONARY_LIMITS["baseline"]
            and float(row["lower_band_difference"])
            <= STATIONARY_LIMITS["silhouette"]
        )
    else:
        passed = (
            head <= MOVING_LIMITS["head"]
            and torso <= MOVING_LIMITS["torso"]
            and int(row["shoulder_delta"]) <= MOVING_LIMITS["shoulder"]
            and int(row["waist_delta"]) <= MOVING_LIMITS["waist"]
            and int(row["baseline_delta"]) <= MOVING_LIMITS["baseline"]
            and int(row["height_delta"]) <= MOVING_LIMITS["height"]
        )
    row["pass"] = passed
    return row


def boundary_sheet(
    row: dict[str, object],
    path: Path,
) -> None:
    before = load(str(row["from"])).convert("RGB")
    after = load(str(row["to"])).convert("RGB")
    canvas = Image.new("RGB", (1024, 936), (35, 35, 35))
    canvas.paste(before, (0, 72))
    canvas.paste(after, (512, 72))
    draw = ImageDraw.Draw(canvas)
    draw.text((8, 8), str(row["boundary"]), fill=(255, 255, 255), font=FONT)
    draw.text((8, 28), f"FROM {row['from']}", fill=(220, 220, 220), font=FONT)
    draw.text((520, 28), f"TO {row['to']}", fill=(220, 220, 220), font=FONT)
    draw.text(
        (8, 48),
        (
            f"class={row['classification']} pass={row['pass']} "
            f"head=({row['head_dx']},{row['head_dy']}) "
            f"torso=({row['torso_dx']},{row['torso_dy']}) "
            f"shoulder={row['shoulder_delta']} "
            f"waist={row['waist_delta']} "
            f"baseline={row['baseline_delta']} "
            f"height={row['height_delta']} "
            f"lower={row['lower_band_difference']}"
        ),
        fill=(160, 255, 160) if row["pass"] else (255, 120, 120),
        font=FONT,
    )
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def write_csv(path: Path, rows: list[dict[str, object]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
        writer.writeheader()
        writer.writerows(rows)


def write_hash_snapshot(path: Path) -> None:
    payload = {
        "sources": {
            name: sha256(PROJECT / name)
            for name in validate_release.SOURCE_CONTRACTS
        },
        "frames": {
            relative_path: sha256(PROJECT / relative_path)
            for relative_path in validate_release.EXPECTED_FRAME_PATHS
        },
    }
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")
    print(
        f"HASH_SNAPSHOT={path} sources={len(payload['sources'])} "
        f"frames={len(payload['frames'])}"
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
        raise RuntimeError(
            f"Independent evidence boundary count is {len(boundaries)}."
        )
    return tuple(boundaries)


def generate() -> None:
    production_seams = {
        "extract_cross_arm": extract_cross_arm.STATIONARY_PLATE_SEAM_Y,
        "validate_release": validate_release.STATIONARY_PLATE_SEAM_Y,
    }
    mismatches = {
        name: value
        for name, value in production_seams.items()
        if value != EXPECTED_STATIONARY_PLATE_SEAM_Y
    }
    if mismatches:
        raise RuntimeError(
            "Production stationary plate seam does not match independent "
            f"evidence contract {EXPECTED_STATIONARY_PLATE_SEAM_Y}: "
            f"{mismatches}"
        )

    after = EVIDENCE_ROOT / "after"
    rows: list[dict[str, object]] = []
    metrics: dict[str, dict[str, object]] = {}
    for group, names in GROUPS.items():
        native: list[Image.Image] = []
        zoom: list[Image.Image] = []
        feet: list[Image.Image] = []
        for name in names:
            relative_path = f"Frames/{group}/{name}"
            image = load(relative_path)
            row = frame_metrics(relative_path)
            rows.append(row)
            metrics[relative_path] = row
            annotated = annotated_frame(image, row)
            native.append(label_tile(annotated, relative_path))
            mask = visible_mask(image)
            ys, xs = np.where(mask)
            x0 = max(0, int(xs.min()) - 8)
            y0 = max(0, int(ys.min()) - 8)
            x1 = min(512, int(xs.max()) + 9)
            y1 = min(864, int(ys.max()) + 9)
            crop = image.crop((x0, y0, x1, y1)).resize(
                ((x1 - x0) * 2, (y1 - y0) * 2),
                Image.Resampling.NEAREST,
            )
            zoom.append(label_tile(crop, f"{relative_path} 2x"))
            foot = image.crop((0, 650, 512, 864)).resize(
                (1024, 428),
                Image.Resampling.NEAREST,
            )
            feet.append(label_tile(foot, f"{relative_path} feet 2x"))
        save_sheet(native, 3, after / "contact-sheets-native" / f"{group}.png")
        save_sheet(zoom, 2, after / "contact-sheets-2x" / f"{group}.png")
        save_sheet(feet, 2, after / "contact-sheets-feet" / f"{group}.png")

    (after / "frame-metrics.json").write_text(
        json.dumps(rows, indent=2),
        encoding="utf-8",
    )
    write_csv(after / "frame-metrics.csv", rows)

    boundary_rows = [
        boundary_row(boundary, metrics)
        for boundary in continuity_boundaries()
    ]
    (after / "boundary-metrics.json").write_text(
        json.dumps(boundary_rows, indent=2),
        encoding="utf-8",
    )
    write_csv(after / "boundary-metrics.csv", boundary_rows)
    for index, row in enumerate(boundary_rows):
        safe = str(row["boundary"]).replace("/", "-")
        boundary_sheet(
            row,
            after / "boundaries" / f"{index:02d}-{safe}.png",
        )

    group_ranges = (
        ("left-directional", 0, 9),
        ("right-directional", 9, 18),
        ("front-handoff", 18, 19),
        ("clap", 19, 35),
        ("cross-release", 35, 45),
    )
    for name, start, end in group_ranges:
        tiles = [
            Image.open(
                after
                / "boundaries"
                / f"{index:02d}-{boundary_rows[index]['boundary']}.png"
            ).convert("RGB")
            for index in range(start, end)
        ]
        save_sheet(tiles, 1, after / "boundary-sheets" / f"{name}.png")

    negative_results = []
    for relative_path in validate_release.EXPECTED_FRAME_PATHS:
        frame = np.asarray(load(relative_path))
        reference = validate_release.lower_reference_for(relative_path)
        for rect in reference.floor_negative_rects:
            x0, y0, x1, y1 = rect
            count = int(
                np.count_nonzero(
                    np.any(frame[y0:y1, x0:x1, :3] != 0, axis=2)
                )
            )
            negative_results.append(
                {
                    "path": relative_path,
                    "rect": list(rect),
                    "nonblack_pixels": count,
                    "pass": count == 0,
                }
            )
    (after / "artifact-negative-results.json").write_text(
        json.dumps(negative_results, indent=2),
        encoding="utf-8",
    )

    left_front = PROJECT / "Frames" / "LeftTurn" / "turn_0.png"
    right_front = PROJECT / "Frames" / "RightTurn" / "turn_0.png"
    canonical = np.asarray(load("Frames/RightTurn/turn_0.png"))
    shared_paths = (
        "Frames/CrossArm/cross_02.png",
        *(f"Frames/CrossArmRelease/release_{index:02d}.png" for index in range(6)),
    )
    mirrors = []
    for index in range(6):
        left = np.asarray(load(f"Frames/LeftWalk/walk_{index:02d}.png"))
        right = np.asarray(load(f"Frames/RightWalk/walk_{index:02d}.png"))
        mirrors.append(
            {
                "index": index,
                "mismatch_pixels": int(
                    np.count_nonzero(np.any(left != right[:, ::-1], axis=2))
                ),
            }
        )
    summary = {
        "frames": len(rows),
        "boundaries": len(boundary_rows),
        "all_boundaries_pass": all(bool(row["pass"]) for row in boundary_rows),
        "all_bounds_inside_canvas": all(
            int(row["bbox_left"]) > 0
            and int(row["bbox_top"]) > 0
            and int(row["bbox_right"]) < 511
            and int(row["bbox_bottom"]) < 863
            for row in rows
        ),
        "all_floor_negatives_black": all(
            bool(row["pass"]) for row in negative_results
        ),
        "front_pixels_identical": np.array_equal(
            np.asarray(load("Frames/LeftTurn/turn_0.png")),
            canonical,
        ),
        "front_png_bytes_identical": left_front.read_bytes()
        == right_front.read_bytes(),
        "shared_plate_exact": all(
            np.array_equal(
                np.asarray(load(path))[
                    EXPECTED_STATIONARY_PLATE_SEAM_Y:,
                    :,
                    :3,
                ],
                canonical[
                    EXPECTED_STATIONARY_PLATE_SEAM_Y:,
                    :,
                    :3,
                ],
            )
            for path in shared_paths
        ),
        "walk_mirrors": mirrors,
        "source_hashes": {
            name: sha256(PROJECT / name)
            for name in validate_release.SOURCE_CONTRACTS
        },
    }
    (after / "visual-summary.json").write_text(
        json.dumps(summary, indent=2),
        encoding="utf-8",
    )
    print(
        f"EVIDENCE_PASS frames={len(rows)} boundaries={len(boundary_rows)} "
        f"boundary_pass={summary['all_boundaries_pass']} "
        f"negatives_black={summary['all_floor_negatives_black']} "
        f"front_identity={summary['front_png_bytes_identical']} "
        f"shared_plate={summary['shared_plate_exact']}"
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--snapshot", type=Path)
    args = parser.parse_args()
    if args.snapshot is not None:
        write_hash_snapshot(args.snapshot)
    else:
        generate()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
