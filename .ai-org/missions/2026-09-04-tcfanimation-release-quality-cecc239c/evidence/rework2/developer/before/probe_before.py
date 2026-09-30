from __future__ import annotations

import hashlib
import json
import sys
from pathlib import Path

import cv2
import numpy as np
from PIL import Image, ImageDraw


EVIDENCE_DIR = Path(__file__).resolve().parent
REPOSITORY = Path(__file__).resolve().parents[7]
PROJECT = REPOSITORY / "TCFAnimation"
sys.path.insert(0, str(PROJECT / "FrameExtraction"))

import validate_release  # noqa: E402


DEFECT_ROIS = {
    "Frames/LeftTurn/turn_0.png": ((204, 826, 238, 844),),
    "Frames/LeftTurn/turn_1.png": ((191, 820, 258, 845),),
    "Frames/LeftTurn/turn_2.png": ((210, 808, 221, 812),),
    "Frames/RightTurn/turn_0.png": ((247, 766, 253, 775),),
    "Frames/RightTurn/turn_2.png": ((213, 774, 327, 842),),
    **{
        f"Frames/Clap/clap_{index:02d}.png": ((195, 828, 255, 844),)
        for index in range(6)
    },
    **{
        f"Frames/CrossArm/cross_{index:02d}.png": ((194, 828, 250, 844),)
        for index in range(3)
    },
}


def load(relative_path: str) -> np.ndarray:
    frame = cv2.imread(str(PROJECT / relative_path), cv2.IMREAD_UNCHANGED)
    if frame is None:
        raise RuntimeError(f"Could not load {relative_path}.")
    return frame


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def anchors(frame: np.ndarray) -> dict[str, float | int]:
    visible = np.max(frame[:, :, :3], axis=2) > 0
    ys, xs = np.where(visible)
    top = int(ys.min())
    bottom = int(ys.max())
    height = bottom - top + 1
    rows = np.indices(visible.shape)[0]
    head = visible & (rows <= top + max(1, round(height * 0.20)))
    torso = visible & (
        (rows >= top + round(height * 0.35))
        & (rows <= top + round(height * 0.62))
    )
    head_y, head_x = np.where(head)
    torso_y, torso_x = np.where(torso)
    foot_y, foot_x = np.where(visible[max(top, bottom - 7) : bottom + 1])
    return {
        "bbox_top": top,
        "bbox_bottom": bottom,
        "bbox_height": height,
        "head_x": round(float(head_x.mean()), 2),
        "head_y": round(float(head_y.mean()), 2),
        "torso_x": round(float(torso_x.mean()), 2),
        "torso_y": round(float(torso_y.mean()), 2),
        "foot_x": round(float(foot_x.mean()), 2),
        "foot_y": bottom,
    }


def boundary(
    from_path: str,
    to_path: str,
) -> dict[str, object]:
    before = anchors(load(from_path))
    after = anchors(load(to_path))
    result: dict[str, object] = {"from": from_path, "to": to_path}
    for key in (
        "bbox_top",
        "bbox_bottom",
        "bbox_height",
        "head_x",
        "head_y",
        "torso_x",
        "torso_y",
        "foot_x",
        "foot_y",
    ):
        result[f"{key}_delta"] = round(
            float(after[key]) - float(before[key]),
            2,
        )
    return result


def accepted_by_validator(
    relative_path: str,
    frame: np.ndarray,
    matte: np.ndarray,
) -> tuple[bool, str]:
    try:
        validate_release.validate_independent_lower_frame(
            relative_path,
            frame,
            matte,
        )
    except RuntimeError as exception:
        return False, str(exception)
    return True, "accepted"


def find_detached_control(
    relative_path: str,
    frame: np.ndarray,
) -> tuple[tuple[int, int, int, int], np.ndarray, np.ndarray]:
    visible = np.max(frame[:, :, :3], axis=2) > 0
    reference = validate_release.lower_reference_for(relative_path)
    approved = np.zeros(visible.shape, dtype=bool)
    for rect in reference.sole_envelopes:
        approved |= validate_release.validator_rect_mask(visible.shape, rect)
    distance = cv2.distanceTransform(
        np.where(visible, 0, 1).astype(np.uint8),
        cv2.DIST_L2,
        3,
    )
    width = 5
    height = 4
    for y in range(730, 840 - height):
        for x in range(100, 400 - width):
            region = np.s_[y : y + height, x : x + width]
            if (
                np.all(approved[region])
                and not np.any(visible[region])
                and 2.0 <= float(np.min(distance[region])) <= 18.0
            ):
                altered = frame.copy()
                altered[region[0], region[1], :3] = (48, 18, 42)
                matte = visible.astype(np.uint8)
                matte[region] = 1
                return (x, y, x + width, y + height), altered, matte
    raise RuntimeError("Could not find an in-envelope detached control ROI.")


def make_roi_sheet() -> None:
    tiles: list[Image.Image] = []
    for relative_path, rois in DEFECT_ROIS.items():
        image = Image.open(PROJECT / relative_path).convert("RGB")
        crop_top = 720
        crop = image.crop((120, crop_top, 390, 864)).resize(
            (540, 288),
            Image.Resampling.NEAREST,
        )
        draw = ImageDraw.Draw(crop)
        for x0, y0, x1, y1 in rois:
            draw.rectangle(
                (
                    (x0 - 120) * 2,
                    (y0 - crop_top) * 2,
                    (x1 - 120) * 2 - 1,
                    (y1 - crop_top) * 2 - 1,
                ),
                outline=(255, 0, 0),
                width=2,
            )
        tile = Image.new("RGB", (540, 314), (32, 32, 32))
        tile.paste(crop, (0, 26))
        ImageDraw.Draw(tile).text((6, 7), relative_path, fill=(255, 255, 255))
        tiles.append(tile)
    columns = 3
    rows = (len(tiles) + columns - 1) // columns
    sheet = Image.new("RGB", (columns * 540, rows * 314), (64, 64, 64))
    for index, tile in enumerate(tiles):
        sheet.paste(tile, ((index % columns) * 540, (index // columns) * 314))
    sheet.save(EVIDENCE_DIR / "defect-rois-before.png")


def main() -> int:
    EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
    roi_results = {}
    for relative_path, rois in DEFECT_ROIS.items():
        frame = load(relative_path)
        rgb = frame[:, :, :3]
        counts = []
        for roi in rois:
            x0, y0, x1, y1 = roi
            counts.append(
                int(np.count_nonzero(np.any(rgb[y0:y1, x0:x1] != 0, axis=2)))
            )
        roi_results[relative_path] = counts

    clap_path = "Frames/Clap/clap_00.png"
    clap = load(clap_path)
    dark_block = clap.copy()
    dark_matte = (np.max(clap[:, :, :3], axis=2) > 0).astype(np.uint8)
    dark_block[838:844, 198:208, :3] = (12, 12, 12)
    dark_matte[838:844, 198:208] = 1
    dark_accepted, dark_reason = accepted_by_validator(
        clap_path,
        dark_block,
        dark_matte,
    )

    cool_rect, cool_frame, cool_matte = find_detached_control(clap_path, clap)
    cool_accepted, cool_reason = accepted_by_validator(
        clap_path,
        cool_frame,
        cool_matte,
    )

    right_front = PROJECT / "Frames" / "RightTurn" / "turn_0.png"
    left_front = PROJECT / "Frames" / "LeftTurn" / "turn_0.png"
    source_hashes = {
        name: sha256(PROJECT / name)
        for name in validate_release.SOURCE_CONTRACTS
    }
    frame_hashes = {
        relative_path: sha256(PROJECT / relative_path)
        for relative_path in validate_release.EXPECTED_FRAME_PATHS
    }
    summary = {
        "stage": "before",
        "frame_count": len(frame_hashes),
        "source_hashes": source_hashes,
        "frame_hashes": frame_hashes,
        "defect_roi_nonblack_counts": roi_results,
        "all_defect_rois_black": all(
            count == 0
            for counts in roi_results.values()
            for count in counts
        ),
        "front_png_bytes_identical": left_front.read_bytes()
        == right_front.read_bytes(),
        "front_pixels_identical": np.array_equal(
            load("Frames/LeftTurn/turn_0.png"),
            load("Frames/RightTurn/turn_0.png"),
        ),
        "boundaries": {
            "front_handoff": boundary(
                "Frames/LeftTurn/turn_0.png",
                "Frames/RightTurn/turn_0.png",
            ),
            "cross02_to_release00": boundary(
                "Frames/CrossArm/cross_02.png",
                "Frames/CrossArmRelease/release_00.png",
            ),
        },
        "adversarial_controls": {
            "in_envelope_dark_rectangle": {
                "accepted": dark_accepted,
                "reason": dark_reason,
                "rect": [198, 838, 208, 844],
            },
            "detached_cool_fragment": {
                "accepted": cool_accepted,
                "reason": cool_reason,
                "rect": list(cool_rect),
            },
        },
    }
    (EVIDENCE_DIR / "probe-before.json").write_text(
        json.dumps(summary, indent=2),
        encoding="utf-8",
    )
    make_roi_sheet()

    print(f"BEFORE_FRAME_COUNT={summary['frame_count']}")
    print(f"BEFORE_ALL_DEFECT_ROIS_BLACK={summary['all_defect_rois_black']}")
    print(f"BEFORE_FRONT_BYTES_IDENTICAL={summary['front_png_bytes_identical']}")
    print(f"BEFORE_FRONT_PIXELS_IDENTICAL={summary['front_pixels_identical']}")
    print(f"BEFORE_DARK_RECTANGLE_ACCEPTED={dark_accepted}")
    print(f"BEFORE_COOL_FRAGMENT_ACCEPTED={cool_accepted} rect={cool_rect}")
    print(
        "BEFORE_CROSS02_RELEASE00="
        + json.dumps(summary["boundaries"]["cross02_to_release00"])
    )
    print(f"WROTE={EVIDENCE_DIR / 'probe-before.json'}")
    print(f"WROTE={EVIDENCE_DIR / 'defect-rois-before.png'}")
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
