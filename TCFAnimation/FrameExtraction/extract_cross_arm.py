"""Generate front cross-arm and release transitions on opaque black canvases.

Both sources are JPEG containers under .png extensions:

    CrossArm3.png: 3 columns x 2 rows, normal row-major human numbering
    CrossArm4.png: 3 columns x 2 rows, normal row-major human numbering

Only human-numbered poses 4, 5, and 6 from CrossArm3 are runtime crossing
frames. Human pose 6 is the persistent crossed-arm hold. All six CrossArm4
poses form the crossed-hold-to-front-idle release in row-major order.
"""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass, replace
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUPPORT_PACKAGES = ROOT / ".tools" / "python"
if SUPPORT_PACKAGES.is_dir():
    sys.path.insert(0, str(SUPPORT_PACKAGES))

import cv2  # type: ignore  # noqa: E402
import numpy as np  # type: ignore  # noqa: E402
from PIL import Image  # type: ignore  # noqa: E402
from foreground_cutout import (  # noqa: E402
    CutoutConfig,
    CutoutResult,
    Polygon,
    isolate_character,
    project_source_exclusion_mask,
)
from file_integrity import sha256_file  # noqa: E402


EVIDENCE_DIR = (
    ROOT
    / ".ai-org"
    / "missions"
    / "2026-09-03-front-cross-arms-cecc239c"
)
COMBINED_CONTACT_SHEET_PATH = EVIDENCE_DIR / "cross-arm-combined-contact-sheet.png"
CANONICAL_FRONT_PATH = ROOT / "Frames" / "RightTurn" / "turn_0.png"

CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
TARGET_TORSO_X = 256
TARGET_SHOE_BASELINE_Y = 847
CANONICAL_VISIBLE_SHOE_BASELINE_Y = 842
CROSS_PLAYBACK_FPS = 8.0
RELEASE_PLAYBACK_FPS = 8.0
HEIGHT_TOLERANCE = 6
LANDMARK_TOLERANCE = 16
# The stationary lower plate contract is exact from this output row onward.
# Keep this production constant independent from the release validator.
STATIONARY_PLATE_SEAM_Y = 640
LOWER_BODY_CORE_TOP_Y = 600
LOWER_BODY_CORE_X = (180, 335)
LOWER_BODY_GARMENT_RUN_MIN_WIDTH = 80
RELEASE_X_OFFSET_CANDIDATES = (18, 11, 10, 14, 11, 9)
STATIONARY_ANCHOR_TOLERANCE = 4.0


@dataclass(frozen=True)
class SequenceSpec:
    source_name: str
    source_sha256: str
    source_size_bytes: int
    source_shape: tuple[int, int, int]
    source_layout: str
    source_order: str
    selected_human_frames: tuple[int, ...]
    source_crops: tuple[tuple[int, int, int, int], ...]
    output_directory: str
    output_prefix: str
    contact_sheet_name: str
    playback_fps: float
    scale: float
    canonical_height_scales: tuple[float, ...]
    torso_centers: tuple[float, ...]
    shoe_baselines: tuple[int, ...]
    person_component_bounds: tuple[tuple[int, int, int, int], ...]
    pose_names: tuple[str, ...]
    terminal_pose: str
    source_exclusions: tuple[tuple[Polygon, ...], ...]
    preserved_output_sha256: tuple[str, ...] = ()


@dataclass(frozen=True)
class GeneratedFrame:
    panel: np.ndarray
    frame: np.ndarray
    cutout: CutoutResult
    offset_x: int
    offset_y: int
    canonical_height_scale: float
    canonical_vertical_translation: float
    uncalibrated_metrics: AnatomicalMetrics


@dataclass(frozen=True)
class AnatomicalMetrics:
    head_top: int
    shoe_baseline: int
    body_height: int
    shoulder_top: int
    waist_bottom: int


@dataclass(frozen=True)
class VisibleAnchors:
    head_x: float
    head_y: float
    torso_x: float
    torso_y: float
    shoe_baseline: int


CROSS_SPEC = SequenceSpec(
    source_name="CrossArm3.png",
    source_sha256=(
        "276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192"
    ),
    source_size_bytes=628721,
    source_shape=(1448, 1086, 3),
    source_layout="3x2",
    source_order="row_major_left_to_right_top_to_bottom",
    selected_human_frames=(4, 5, 6),
    source_crops=(
        (0, 727, 360, 1388),
        (362, 727, 722, 1388),
        (724, 727, 1086, 1388),
    ),
    output_directory="CrossArm",
    output_prefix="cross",
    contact_sheet_name="cross-arm-contact-sheet.png",
    playback_fps=CROSS_PLAYBACK_FPS,
    scale=1.22,
    canonical_height_scales=(0.846547, 0.846547, 0.846547),
    torso_centers=(166.0, 166.0, 172.0),
    shoe_baselines=(1382, 1382, 1382),
    person_component_bounds=(
        (47, 738, 304, 1382),
        (426, 738, 657, 1382),
        (790, 738, 1022, 1382),
    ),
    pose_names=(
        "human_4_arms_crossing_start",
        "human_5_arms_crossing_mid",
        "human_6_arms_crossed_complete",
    ),
    terminal_pose="frame 02 (human source pose 6) is the persistent hold",
    source_exclusions=(
        (((114, 1368), (161, 1368), (161, 1380), (114, 1380)),),
        (((476, 1368), (523, 1368), (523, 1380), (476, 1380)),),
        (((838, 1368), (885, 1368), (885, 1380), (838, 1380)),),
    ),
)

RELEASE_SPEC = SequenceSpec(
    source_name="CrossArm4.png",
    source_sha256=(
        "475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F"
    ),
    source_size_bytes=632179,
    source_shape=(1448, 1086, 3),
    source_layout="3x2",
    source_order="row_major_left_to_right_top_to_bottom",
    selected_human_frames=(1, 2, 3, 4, 5, 6),
    source_crops=(
        (0, 0, 355, 675),
        (359, 0, 718, 675),
        (720, 0, 1086, 675),
        (0, 740, 355, 1388),
        (359, 740, 718, 1388),
        (720, 740, 1086, 1388),
    ),
    output_directory="CrossArmRelease",
    output_prefix="release",
    contact_sheet_name="cross-arm-release-contact-sheet.png",
    playback_fps=RELEASE_PLAYBACK_FPS,
    scale=1.22,
    canonical_height_scales=(
        0.840102,
        0.839037,
        0.835859,
        0.866492,
        0.862400,
        0.867628,
    ),
    torso_centers=(177.0, 181.0, 180.0, 177.0, 180.0, 180.0),
    shoe_baselines=(663, 664, 666, 1383, 1384, 1380),
    person_component_bounds=(
        (64, 12, 339, 664),
        (423, 12, 696, 665),
        (784, 12, 1026, 667),
        (55, 746, 354, 1384),
        (423, 746, 717, 1385),
        (785, 746, 1025, 1381),
    ),
    pose_names=(
        "human_1_arms_crossed_release_start",
        "human_2_uncrossing_start",
        "human_3_uncrossing_mid",
        "human_4_hands_dropping_down",
        "human_5_arms_relaxed_at_sides",
        "human_6_hands_down_idle_end",
    ),
    terminal_pose="frame 05 (human source pose 6) is normal front idle",
    source_exclusions=((), (), (), (), (), ()),
)


def source_path(spec: SequenceSpec) -> Path:
    return ROOT / spec.source_name


def output_dir(spec: SequenceSpec) -> Path:
    return ROOT / "Frames" / spec.output_directory


def source_sha256(spec: SequenceSpec) -> str:
    return sha256_file(source_path(spec), spec.source_size_bytes)


def validate_source(spec: SequenceSpec) -> tuple[np.ndarray, str]:
    path = source_path(spec)
    if not path.is_file():
        raise FileNotFoundError(f"Missing source image: {path}")

    hash_before = source_sha256(spec)
    if hash_before != spec.source_sha256:
        raise RuntimeError(
            f"{spec.source_name} SHA-256 is {hash_before}; "
            f"expected {spec.source_sha256}."
        )

    with Image.open(path) as image:
        source_format = image.format
        decoded_shape = (image.height, image.width, len(image.getbands()))
    if source_format != "JPEG":
        raise RuntimeError(
            f"{spec.source_name} container is {source_format}; expected JPEG "
            "data under the .png extension."
        )
    if decoded_shape != spec.source_shape:
        raise RuntimeError(
            f"{spec.source_name} Pillow shape is {decoded_shape}; "
            f"expected {spec.source_shape}."
        )

    source = cv2.imread(str(path), cv2.IMREAD_COLOR)
    if source is None or source.shape != spec.source_shape:
        raise RuntimeError(
            f"{spec.source_name} OpenCV shape is "
            f"{None if source is None else source.shape}; "
            f"expected {spec.source_shape}."
        )

    print(
        f"source={spec.source_name} format={source_format} "
        f"shape={source.shape} sha256={hash_before}"
    )
    return source, hash_before


def validate_source_geometry(spec: SequenceSpec) -> None:
    frame_count = len(spec.pose_names)
    if (
        len(spec.selected_human_frames) != frame_count
        or len(spec.source_crops) != frame_count
        or len(spec.canonical_height_scales) != frame_count
        or len(spec.torso_centers) != frame_count
        or len(spec.shoe_baselines) != frame_count
        or len(spec.person_component_bounds) != frame_count
        or len(spec.source_exclusions) != frame_count
    ):
        raise RuntimeError(f"{spec.source_name}: inconsistent pose records.")

    for index, (crop, bounds) in enumerate(
        zip(spec.source_crops, spec.person_component_bounds, strict=True)
    ):
        x0, y0, x1, y1 = crop
        bx0, by0, bx1, by1 = bounds
        if not (x0 <= bx0 < bx1 <= x1 and y0 <= by0 < by1 <= y1):
            raise RuntimeError(
                f"{spec.source_name} frame {index} crop {crop} clips "
                f"component {bounds}."
            )
        included = []
        for component_index, other in enumerate(spec.person_component_bounds):
            ox0, oy0, ox1, oy1 = other
            if not (ox1 <= x0 or ox0 >= x1 or oy1 <= y0 or oy0 >= y1):
                included.append(component_index)
        if included != [index]:
            raise RuntimeError(
                f"{spec.source_name} frame {index} crop contains components "
                f"{included}; expected only [{index}]."
            )

    print(
        f"source={spec.source_name} layout={spec.source_layout} "
        f"order={spec.source_order} "
        f"selected_human_frames={spec.selected_human_frames} "
        f"crops={spec.source_crops} "
        f"component_bounds={spec.person_component_bounds} "
        f"poses={spec.pose_names} "
        f"playback_fps={spec.playback_fps:g} terminal={spec.terminal_pose} "
        "duplicate_terminal_pose=false"
    )


def make_cutout_config(spec: SequenceSpec, index: int) -> CutoutConfig:
    if spec is CROSS_SPEC:
        return CutoutConfig(
            f"{spec.output_directory}/{spec.output_prefix}_{index:02d}",
            (85, 45, 430, 860),
            (130, 150, 390, 610),
            (
                (165, 680, 260, 842),
                (290, 680, 360, 842),
            ),
            700,
            minimum_shoe_pixels=80,
            minimum_dark_shoe_retention=0.93,
            minimum_visible_shoe_retention=0.97,
        )

    return CutoutConfig(
        f"{spec.output_directory}/{spec.output_prefix}_{index:02d}",
        (70, 35, 445, 860),
        (120, 145, 405, 650),
        (
            (150, 680, 232, 842),
            (280, 680, 345, 842),
        ),
        700,
        minimum_shoe_pixels=80,
        minimum_dark_shoe_retention=0.93,
        minimum_visible_shoe_retention=0.97,
    )


def generate_frames(
    source: np.ndarray,
    spec: SequenceSpec,
) -> list[GeneratedFrame]:
    generated: list[GeneratedFrame] = []
    for index, (
        crop,
        torso_center,
        shoe_baseline,
        canonical_height_scale,
        source_exclusions,
    ) in enumerate(
        zip(
            spec.source_crops,
            spec.torso_centers,
            spec.shoe_baselines,
            spec.canonical_height_scales,
            spec.source_exclusions,
            strict=True,
        )
    ):
        x0, y0, x1, y1 = crop
        panel = source[y0:y1, x0:x1].copy()
        resized_width = round(panel.shape[1] * spec.scale)
        resized_height = round(panel.shape[0] * spec.scale)
        panel = cv2.resize(
            panel,
            (resized_width, resized_height),
            interpolation=cv2.INTER_LANCZOS4,
        )
        local_torso_x = torso_center
        local_shoe_y = shoe_baseline - y0
        offset_x = TARGET_TORSO_X - round(local_torso_x * spec.scale)
        offset_y = TARGET_SHOE_BASELINE_Y - round(local_shoe_y * spec.scale)
        if (
            offset_x < 0
            or offset_y < 0
            or offset_x + resized_width > CANVAS_WIDTH
            or offset_y + resized_height > CANVAS_HEIGHT
        ):
            raise RuntimeError(
                f"{spec.source_name} frame {index} panel {panel.shape} at "
                f"({offset_x}, {offset_y}) does not fit the canvas."
            )

        canvas = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH, 4), dtype=np.uint8)
        canvas[:, :, 3] = 255
        canvas[
            offset_y : offset_y + resized_height,
            offset_x : offset_x + resized_width,
            :3,
        ] = panel
        exclusion_mask = project_source_exclusion_mask(
            (CANVAS_HEIGHT, CANVAS_WIDTH),
            crop,
            panel.shape[:2],
            (offset_x, offset_y),
            source_exclusions,
        )
        cutout = isolate_character(
            canvas,
            make_cutout_config(spec, index),
            source_exclusion_mask=exclusion_mask,
        )
        uncalibrated_metrics = measure_anatomy(
            cutout.frame,
            f"{spec.output_directory}/{spec.output_prefix}_{index:02d} "
            "uncalibrated",
        )
        cutout, vertical_translation = calibrate_to_canonical_height(
            cutout,
            canonical_height_scale,
            uncalibrated_metrics.shoe_baseline,
        )
        generated.append(
            GeneratedFrame(
                panel,
                cutout.frame,
                cutout,
                offset_x,
                offset_y,
                canonical_height_scale,
                vertical_translation,
                uncalibrated_metrics,
            )
        )
    return generated


def validate_generated(
    generated: list[GeneratedFrame],
    spec: SequenceSpec,
) -> None:
    frame_count = len(spec.pose_names)
    if len(generated) != frame_count:
        raise RuntimeError(
            f"{spec.source_name}: expected {frame_count} frames, "
            f"got {len(generated)}."
        )

    expected_names = {
        f"{spec.output_prefix}_{index:02d}.png"
        for index in range(frame_count)
    }
    directory = output_dir(spec)
    if not directory.is_dir():
        raise FileNotFoundError(
            f"Missing runtime output directory: {directory}"
        )
    unexpected = [
        path for path in directory.glob("*.png")
        if path.name not in expected_names
    ]
    if unexpected:
        raise RuntimeError(
            f"Unexpected {spec.output_directory} PNG assets must be removed "
            "explicitly: "
            + ", ".join(str(path.relative_to(ROOT)) for path in unexpected)
        )

    for index, item in enumerate(generated):
        if item.frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(
                f"{spec.source_name} frame {index} has shape "
                f"{item.frame.shape}."
            )
        if not np.all(item.frame[:, :, 3] == 255):
            raise RuntimeError(
                f"{spec.source_name} frame {index} is not fully opaque."
            )
        background = item.cutout.matte == 0
        if np.any(item.frame[:, :, :3][background] != 0):
            raise RuntimeError(
                f"{spec.source_name} frame {index} background is not "
                "exact RGB black."
            )
        torso_x = item.offset_x + round(
            spec.torso_centers[index] * spec.scale
        )
        shoe_y = item.offset_y + round(
            (spec.shoe_baselines[index] - spec.source_crops[index][1])
            * spec.scale
        )
        if torso_x != TARGET_TORSO_X or shoe_y != TARGET_SHOE_BASELINE_Y:
            raise RuntimeError(
                f"{spec.source_name} frame {index} alignment is "
                f"torso={torso_x}, shoe={shoe_y}."
            )
        print(
            f"sequence={spec.output_directory} frame={index:02d} "
            f"pose={spec.pose_names[index]} "
            f"panel={item.panel.shape[1]}x{item.panel.shape[0]} "
            f"offset=({item.offset_x},{item.offset_y}) "
            f"canonical_height_scale={item.canonical_height_scale:.6f} "
            f"effective_source_scale="
            f"{spec.scale * item.canonical_height_scale:.6f} "
            f"canonical_vertical_translation="
            f"{item.canonical_vertical_translation:.3f} "
            f"uncalibrated_head={item.uncalibrated_metrics.head_top} "
            f"uncalibrated_shoe="
            f"{item.uncalibrated_metrics.shoe_baseline} "
            f"black_ratio={item.cutout.black_ratio:.4f} "
            f"foreground_pixels={item.cutout.foreground_pixels} "
            f"shoe_pixels={item.cutout.shoe_pixels}"
        )


def write_png(path: Path, image: np.ndarray) -> None:
    if not cv2.imwrite(str(path), image, [cv2.IMWRITE_PNG_COMPRESSION, 9]):
        raise RuntimeError(f"Failed to write {path}.")


def measure_anatomy(frame: np.ndarray, name: str) -> AnatomicalMetrics:
    if frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
        raise RuntimeError(f"{name}: unexpected frame shape {frame.shape}.")

    bgr = frame[:, :, :3]
    foreground = np.max(bgr, axis=2) > 0
    foreground_rows = np.flatnonzero(np.any(foreground, axis=1))
    if foreground_rows.size == 0:
        raise RuntimeError(f"{name}: no visible foreground.")

    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    green_vest = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & foreground
    )
    green_rows = np.flatnonzero(np.count_nonzero(green_vest, axis=1) >= 12)
    if green_rows.size == 0:
        raise RuntimeError(f"{name}: could not locate the green vest landmarks.")

    head_top = int(foreground_rows[0])
    shoe_baseline = int(foreground_rows[-1])
    return AnatomicalMetrics(
        head_top=head_top,
        shoe_baseline=shoe_baseline,
        body_height=shoe_baseline - head_top + 1,
        shoulder_top=int(green_rows[0]),
        waist_bottom=int(green_rows[-1]),
    )


def calibrate_to_canonical_height(
    cutout: CutoutResult,
    scale: float,
    source_visible_shoe_baseline: int,
) -> tuple[CutoutResult, float]:
    """Uniformly calibrate one isolated pose around its visible foot baseline."""

    vertical_translation = (
        CANONICAL_VISIBLE_SHOE_BASELINE_Y
        - scale * source_visible_shoe_baseline
    )
    transform = np.array(
        (
            (scale, 0.0, TARGET_TORSO_X * (1.0 - scale)),
            (0.0, scale, vertical_translation),
        ),
        dtype=np.float32,
    )
    calibrated_rgb = cv2.warpAffine(
        cutout.frame[:, :, :3],
        transform,
        (CANVAS_WIDTH, CANVAS_HEIGHT),
        flags=cv2.INTER_LANCZOS4,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )
    calibrated_matte = cv2.warpAffine(
        cutout.matte,
        transform,
        (CANVAS_WIDTH, CANVAS_HEIGHT),
        flags=cv2.INTER_NEAREST,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )
    calibrated_rgb[calibrated_matte == 0] = 0
    calibrated_frame = np.zeros_like(cutout.frame)
    calibrated_frame[:, :, :3] = calibrated_rgb
    calibrated_frame[:, :, 3] = 255
    return (
        replace(
            cutout,
            frame=calibrated_frame,
            matte=calibrated_matte,
            foreground_pixels=int(np.count_nonzero(calibrated_matte)),
            black_ratio=(
                1.0
                - int(np.count_nonzero(calibrated_matte))
                / calibrated_matte.size
            ),
        ),
        vertical_translation,
    )


def visible_anchors(frame: np.ndarray, name: str) -> VisibleAnchors:
    """Measure final visible upper-body centroids on the runtime canvas."""

    visible = np.max(frame[:, :, :3], axis=2) > 0
    ys, _ = np.where(visible)
    if ys.size == 0:
        raise RuntimeError(f"{name}: no visible foreground.")
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
    if head_x.size == 0 or torso_x.size == 0:
        raise RuntimeError(f"{name}: final visible anchors are empty.")
    return VisibleAnchors(
        head_x=float(head_x.mean()),
        head_y=float(head_y.mean()),
        torso_x=float(torso_x.mean()),
        torso_y=float(torso_y.mean()),
        shoe_baseline=bottom,
    )


def translate_cutout_integer(
    cutout: CutoutResult,
    delta_x: int,
    delta_y: int,
) -> CutoutResult:
    """Translate a cutout by whole pixels without resampling."""

    if delta_x == 0 and delta_y == 0:
        return cutout
    height, width = cutout.matte.shape
    source_x0 = max(0, -delta_x)
    source_x1 = min(width, width - delta_x)
    source_y0 = max(0, -delta_y)
    source_y1 = min(height, height - delta_y)
    target_x0 = source_x0 + delta_x
    target_x1 = source_x1 + delta_x
    target_y0 = source_y0 + delta_y
    target_y1 = source_y1 + delta_y
    frame = np.zeros_like(cutout.frame)
    frame[:, :, 3] = 255
    matte = np.zeros_like(cutout.matte)
    frame[target_y0:target_y1, target_x0:target_x1, :3] = cutout.frame[
        source_y0:source_y1,
        source_x0:source_x1,
        :3,
    ]
    matte[target_y0:target_y1, target_x0:target_x1] = cutout.matte[
        source_y0:source_y1,
        source_x0:source_x1,
    ]
    if (
        np.any(frame[0, :, :3])
        or np.any(frame[-1, :, :3])
        or np.any(frame[:, 0, :3])
        or np.any(frame[:, -1, :3])
    ):
        raise RuntimeError(
            f"Integer translation {(delta_x, delta_y)} clips visible pixels."
        )
    return replace(
        cutout,
        frame=frame,
        matte=matte,
        foreground_pixels=int(np.count_nonzero(matte)),
        black_ratio=1.0 - np.count_nonzero(matte) / matte.size,
    )


def with_cutout(
    item: GeneratedFrame,
    cutout: CutoutResult,
) -> GeneratedFrame:
    return replace(item, frame=cutout.frame, cutout=cutout)


def compose_canonical_lower_plate(
    item: GeneratedFrame,
    canonical: np.ndarray,
    delta_x: int,
    delta_y: int,
) -> GeneratedFrame:
    """Align the upper body, then copy one canonical lower plate verbatim."""

    translated = translate_cutout_integer(item.cutout, delta_x, delta_y)
    frame = translated.frame.copy()
    matte = translated.matte.copy()
    canonical_visible = np.max(canonical[:, :, :3], axis=2) > 0
    source_frame = frame.copy()
    source_matte = matte.copy()
    source_visible = np.max(source_frame[:, :, :3], axis=2) > 0
    plate_mask = np.zeros(matte.shape, dtype=bool)
    core_x0, core_x1 = LOWER_BODY_CORE_X
    plate_mask[LOWER_BODY_CORE_TOP_Y:] = True
    frame[:, :, :3][plate_mask] = canonical[:, :, :3][plate_mask]
    matte[plate_mask] = canonical_visible[plate_mask].astype(np.uint8)
    preserve_upper = np.zeros(matte.shape, dtype=bool)
    for y in range(LOWER_BODY_CORE_TOP_Y, STATIONARY_PLATE_SEAM_Y):
        padded = np.pad(source_visible[y].astype(np.int8), (1, 1))
        transitions = np.diff(padded)
        starts = np.flatnonzero(transitions == 1)
        ends = np.flatnonzero(transitions == -1)
        for start, end in zip(starts, ends, strict=True):
            width = end - start
            is_outer_limb = (
                (end <= core_x0 or start >= core_x1)
                and width >= 4
            )
            if width >= LOWER_BODY_GARMENT_RUN_MIN_WIDTH or is_outer_limb:
                preserve_upper[y, start:end] = True
    preserve_upper &= source_visible
    frame[:, :, :3][preserve_upper] = source_frame[
        :,
        :,
        :3,
    ][preserve_upper]
    matte[preserve_upper] = source_matte[preserve_upper]
    frame[:, :, 3] = 255
    frame[:, :, :3][matte == 0] = 0
    composed = replace(
        translated,
        frame=frame,
        matte=matte,
        foreground_pixels=int(np.count_nonzero(matte)),
        black_ratio=1.0 - np.count_nonzero(matte) / matte.size,
    )
    return with_cutout(item, composed)


def align_cross_release_continuity(
    cross_frames: list[GeneratedFrame],
    release_frames: list[GeneratedFrame],
) -> tuple[
    list[GeneratedFrame],
    list[GeneratedFrame],
    tuple[tuple[int, int], ...],
]:
    """Share one lower plate and calibrate release uppers from visible anchors."""

    canonical = load_runtime_frame(CANONICAL_FRONT_PATH)
    canonical_visible = np.max(canonical[:, :, :3], axis=2) > 0
    seam_counts = np.count_nonzero(canonical_visible, axis=1)
    if not (
        seam_counts[STATIONARY_PLATE_SEAM_Y - 1] > 0
        and seam_counts[STATIONARY_PLATE_SEAM_Y] > 0
    ):
        raise RuntimeError(
            f"Canonical lower-body seam {STATIONARY_PLATE_SEAM_Y} is not "
            "inside "
            "visible anatomy."
        )

    aligned_cross = list(cross_frames)
    aligned_cross[2] = compose_canonical_lower_plate(
        aligned_cross[2],
        canonical,
        0,
        0,
    )
    target = visible_anchors(
        aligned_cross[2].frame,
        "CrossArm/cross_02 aligned",
    )
    canonical_target = visible_anchors(canonical, "canonical front")
    canonical_anatomy = measure_anatomy(canonical, "canonical front")

    aligned_release: list[GeneratedFrame] = []
    selected_offsets: list[tuple[int, int]] = []
    for index, (item, candidate) in enumerate(
        zip(
            release_frames,
            RELEASE_X_OFFSET_CANDIDATES,
            strict=True,
        )
    ):
        choices = []
        for delta_x in range(candidate - 3, candidate + 4):
            for delta_y in range(-2, 7):
                composed = compose_canonical_lower_plate(
                    item,
                    canonical,
                    delta_x,
                    delta_y,
                )
                measured = visible_anchors(
                    composed.frame,
                    f"CrossArmRelease/release_{index:02d} candidate",
                )
                anatomy = measure_anatomy(
                    composed.frame,
                    f"CrossArmRelease/release_{index:02d} candidate anatomy",
                )
                if (
                    abs(anatomy.head_top - canonical_anatomy.head_top)
                    > HEIGHT_TOLERANCE
                    or abs(
                        anatomy.shoulder_top
                        - canonical_anatomy.shoulder_top
                    )
                    > LANDMARK_TOLERANCE
                    or abs(
                        anatomy.waist_bottom
                        - canonical_anatomy.waist_bottom
                    )
                    > LANDMARK_TOLERANCE
                    or abs(
                        anatomy.shoe_baseline
                        - canonical_anatomy.shoe_baseline
                    )
                    > HEIGHT_TOLERANCE
                    or abs(
                        anatomy.body_height
                        - canonical_anatomy.body_height
                    )
                    > HEIGHT_TOLERANCE
                ):
                    continue
                deltas = (
                    measured.head_x - target.head_x,
                    measured.head_y - target.head_y,
                    measured.torso_x - target.torso_x,
                    measured.torso_y - target.torso_y,
                )
                if index == len(release_frames) - 1:
                    deltas = (
                        *deltas,
                        measured.head_x - canonical_target.head_x,
                        measured.head_y - canonical_target.head_y,
                        measured.torso_x - canonical_target.torso_x,
                        measured.torso_y - canonical_target.torso_y,
                    )
                choices.append(
                    (
                        max(abs(delta) for delta in deltas),
                        sum(abs(delta) for delta in deltas),
                        abs(delta_x - candidate) + abs(delta_y),
                        delta_x,
                        delta_y,
                        composed,
                        measured,
                    )
                )
        if not choices:
            raise RuntimeError(
                f"release_{index:02d} has no final-anchor and landmark "
                "alignment candidate."
            )
        (
            _,
            _,
            _,
            selected,
            selected_y,
            aligned,
            measured,
        ) = min(choices, key=lambda choice: choice[:5])
        aligned_release.append(aligned)
        selected_offsets.append((selected, selected_y))
        print(
            f"release_alignment=release_{index:02d} "
            f"candidate_x={candidate:+d} "
            f"selected=({selected:+d},{selected_y:+d}) "
            f"head_dx={measured.head_x - target.head_x:+.2f} "
            f"head_dy={measured.head_y - target.head_y:+.2f} "
            f"torso_dx={measured.torso_x - target.torso_x:+.2f} "
            f"torso_dy={measured.torso_y - target.torso_y:+.2f}"
        )
    return aligned_cross, aligned_release, tuple(selected_offsets)


def validate_final_continuity(
    cross_frames: list[GeneratedFrame],
    release_frames: list[GeneratedFrame],
    selected_offsets: tuple[tuple[int, int], ...],
) -> None:
    canonical = load_runtime_frame(CANONICAL_FRONT_PATH)
    canonical_plate = canonical[STATIONARY_PLATE_SEAM_Y:, :, :3]
    shared = (cross_frames[2], *release_frames)
    for index, item in enumerate(shared):
        if not np.array_equal(
            item.frame[STATIONARY_PLATE_SEAM_Y:, :, :3],
            canonical_plate,
        ):
            raise RuntimeError(
                f"Shared lower plate differs in sequence item {index}."
            )
        visible = np.max(item.frame[:, :, :3], axis=2) > 0
        ys, xs = np.where(visible)
        if (
            int(xs.min()) <= 0
            or int(xs.max()) >= CANVAS_WIDTH - 1
            or int(ys.min()) <= 0
            or int(ys.max()) >= CANVAS_HEIGHT - 1
        ):
            raise RuntimeError(
                f"Composited sequence item {index} touches a canvas edge."
            )

    target = visible_anchors(
        cross_frames[2].frame,
        "CrossArm/cross_02 final",
    )
    for index, item in enumerate(release_frames):
        measured = visible_anchors(
            item.frame,
            f"CrossArmRelease/release_{index:02d} final",
        )
        if (
            abs(measured.head_x - target.head_x)
            > STATIONARY_ANCHOR_TOLERANCE
            or abs(measured.head_y - target.head_y)
            > STATIONARY_ANCHOR_TOLERANCE
            or abs(measured.torso_x - target.torso_x)
            > STATIONARY_ANCHOR_TOLERANCE
            or abs(measured.torso_y - target.torso_y)
            > STATIONARY_ANCHOR_TOLERANCE
        ):
            raise RuntimeError(
                f"release_{index:02d} final visible anchors exceed "
                f"{STATIONARY_ANCHOR_TOLERANCE:g}px from cross_02."
            )
    print(
        f"canonical_lower_plate=RightTurn/turn_0 "
        f"upper_overlay=outer_x_and_runs>="
        f"{LOWER_BODY_GARMENT_RUN_MIN_WIDTH}px "
        f"range_y={LOWER_BODY_CORE_TOP_Y}.."
        f"{STATIONARY_PLATE_SEAM_Y - 1} "
        f"full_seam_y={STATIONARY_PLATE_SEAM_Y} "
        f"shared_frames=7 selected_release_offsets={selected_offsets} "
        "pixels_copied_without_resampling=true final_anchors=validated"
    )


def load_runtime_frame(path: Path) -> np.ndarray:
    frame = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
    if frame is None:
        raise RuntimeError(f"Could not load runtime frame: {path}")
    return frame


def runtime_frame_paths() -> list[Path]:
    return [
        *(
            output_dir(CROSS_SPEC) / f"cross_{index:02d}.png"
            for index in range(len(CROSS_SPEC.pose_names))
        ),
        *(
            output_dir(RELEASE_SPEC) / f"release_{index:02d}.png"
            for index in range(len(RELEASE_SPEC.pose_names))
        ),
    ]


def make_canonical_height_overlay(
    canonical: np.ndarray,
    frames: list[tuple[Path, np.ndarray, AnatomicalMetrics]],
) -> np.ndarray:
    cell_width = CANVAS_WIDTH
    cell_height = 930
    sheet = np.zeros(
        (cell_height, cell_width * (len(frames) + 1), 3),
        dtype=np.uint8,
    )
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    all_frames = [
        (CANONICAL_FRONT_PATH, canonical, canonical_metrics),
        *frames,
    ]
    guide_rows = (
        (canonical_metrics.head_top, (0, 80, 255), "head"),
        (canonical_metrics.shoulder_top, (0, 220, 255), "shoulder"),
        (canonical_metrics.waist_bottom, (255, 180, 0), "waist"),
        (canonical_metrics.shoe_baseline, (255, 80, 255), "shoe"),
    )
    for index, (path, frame, metrics) in enumerate(all_frames):
        cell_x = index * cell_width
        sheet[58:922, cell_x : cell_x + CANVAS_WIDTH] = frame[:, :, :3]
        for guide_y, color, label in guide_rows:
            display_y = 58 + guide_y
            cv2.line(
                sheet,
                (cell_x, display_y),
                (cell_x + cell_width - 1, display_y),
                color,
                1,
                cv2.LINE_8,
            )
            cv2.putText(
                sheet,
                label,
                (cell_x + 4, display_y - 3),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.34,
                color,
                1,
                cv2.LINE_AA,
            )
        cv2.putText(
            sheet,
            path.stem,
            (cell_x + 8, 20),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.48,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
        cv2.putText(
            sheet,
            (
                f"top={metrics.head_top} shoulder={metrics.shoulder_top} "
                f"waist={metrics.waist_bottom} shoe={metrics.shoe_baseline} "
                f"height={metrics.body_height}"
            ),
            (cell_x + 8, 43),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.34,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def make_zoomed_height_overlay(
    canonical: np.ndarray,
    frames: list[tuple[Path, np.ndarray, AnatomicalMetrics]],
) -> np.ndarray:
    crop_x0, crop_y0, crop_x1, crop_y1 = (95, 155, 417, 855)
    zoom = 2
    cell_width = (crop_x1 - crop_x0) * zoom
    cell_height = (crop_y1 - crop_y0) * zoom + 58
    sheet = np.zeros(
        (cell_height, cell_width * (len(frames) + 1), 3),
        dtype=np.uint8,
    )
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    all_frames = [
        (CANONICAL_FRONT_PATH, canonical, canonical_metrics),
        *frames,
    ]
    guide_rows = (
        (canonical_metrics.head_top, (0, 80, 255)),
        (canonical_metrics.shoulder_top, (0, 220, 255)),
        (canonical_metrics.waist_bottom, (255, 180, 0)),
        (canonical_metrics.shoe_baseline, (255, 80, 255)),
    )
    for index, (path, frame, metrics) in enumerate(all_frames):
        cell_x = index * cell_width
        crop = frame[crop_y0:crop_y1, crop_x0:crop_x1, :3]
        enlarged = cv2.resize(
            crop,
            (cell_width, cell_height - 58),
            interpolation=cv2.INTER_NEAREST,
        )
        sheet[58:, cell_x : cell_x + cell_width] = enlarged
        for guide_y, color in guide_rows:
            display_y = 58 + (guide_y - crop_y0) * zoom
            if 58 <= display_y < cell_height:
                cv2.line(
                    sheet,
                    (cell_x, display_y),
                    (cell_x + cell_width - 1, display_y),
                    color,
                    2,
                    cv2.LINE_8,
                )
        cv2.putText(
            sheet,
            (
                f"{path.stem} top={metrics.head_top} "
                f"shoulder={metrics.shoulder_top} "
                f"waist={metrics.waist_bottom} "
                f"shoe={metrics.shoe_baseline}"
            ),
            (cell_x + 8, 34),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.48,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def validate_existing_height_assets(
    evidence_stage: str,
    write_evidence: bool = False,
) -> None:
    canonical = load_runtime_frame(CANONICAL_FRONT_PATH)
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    frames = []
    failures = []
    for path in runtime_frame_paths():
        frame = load_runtime_frame(path)
        metrics = measure_anatomy(frame, str(path.relative_to(ROOT)))
        frames.append((path, frame, metrics))
        deltas = AnatomicalMetrics(
            head_top=metrics.head_top - canonical_metrics.head_top,
            shoe_baseline=metrics.shoe_baseline - canonical_metrics.shoe_baseline,
            body_height=metrics.body_height - canonical_metrics.body_height,
            shoulder_top=metrics.shoulder_top - canonical_metrics.shoulder_top,
            waist_bottom=metrics.waist_bottom - canonical_metrics.waist_bottom,
        )
        print(
            f"height_check={path.relative_to(ROOT)} "
            f"head_top={metrics.head_top} shoulder={metrics.shoulder_top} "
            f"waist={metrics.waist_bottom} shoe={metrics.shoe_baseline} "
            f"height={metrics.body_height} "
            f"deltas=({deltas.head_top:+d},{deltas.shoulder_top:+d},"
            f"{deltas.waist_bottom:+d},{deltas.shoe_baseline:+d},"
            f"{deltas.body_height:+d})"
        )
        if (
            abs(deltas.head_top) > HEIGHT_TOLERANCE
            or abs(deltas.shoe_baseline) > HEIGHT_TOLERANCE
            or abs(deltas.body_height) > HEIGHT_TOLERANCE
            or abs(deltas.shoulder_top) > LANDMARK_TOLERANCE
            or abs(deltas.waist_bottom) > LANDMARK_TOLERANCE
        ):
            failures.append(
                f"{path.name}: head {deltas.head_top:+d}, "
                f"shoulder {deltas.shoulder_top:+d}, "
                f"waist {deltas.waist_bottom:+d}, "
                f"shoe {deltas.shoe_baseline:+d}, "
                f"height {deltas.body_height:+d}"
            )

    if write_evidence:
        EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
        overlay_path = (
            EVIDENCE_DIR
            / f"cross-arm-canonical-height-overlay-{evidence_stage}.png"
        )
        write_png(
            overlay_path,
            make_canonical_height_overlay(canonical, frames),
        )
        print(f"evidence={overlay_path.relative_to(ROOT)}")
        zoomed_overlay_path = (
            EVIDENCE_DIR
            / f"cross-arm-canonical-height-overlay-{evidence_stage}-zoomed.png"
        )
        write_png(
            zoomed_overlay_path,
            make_zoomed_height_overlay(canonical, frames),
        )
        print(f"evidence={zoomed_overlay_path.relative_to(ROOT)}")

    if failures:
        raise RuntimeError(
            "Cross-arm canonical-height regression failed "
            f"(height tolerance {HEIGHT_TOLERANCE}px, landmark tolerance "
            f"{LANDMARK_TOLERANCE}px): "
            + "; ".join(failures)
        )


def write_frames(
    generated: list[GeneratedFrame],
    spec: SequenceSpec,
) -> None:
    directory = output_dir(spec)
    for index, item in enumerate(generated):
        path = directory / f"{spec.output_prefix}_{index:02d}.png"
        write_png(path, item.frame)
        print(
            f"wrote={path.relative_to(ROOT)} shape={item.frame.shape} "
            "mode=RGBA opaque=true"
        )


def validate_written_outputs(spec: SequenceSpec) -> None:
    directory = output_dir(spec)
    expected_names = {
        f"{spec.output_prefix}_{index:02d}.png"
        for index in range(len(spec.pose_names))
    }
    actual_names = {path.name for path in directory.glob("*.png")}
    if actual_names != expected_names:
        raise RuntimeError(
            f"{spec.output_directory} output set is {sorted(actual_names)}; "
            f"expected {sorted(expected_names)}."
        )
    for index, name in enumerate(sorted(expected_names)):
        with Image.open(directory / name) as image:
            if (
                image.format != "PNG"
                or image.mode != "RGBA"
                or image.size != (CANVAS_WIDTH, CANVAS_HEIGHT)
            ):
                raise RuntimeError(
                    f"{name} is {image.format} {image.mode} {image.size}."
                )
            pixels = np.asarray(image, dtype=np.uint8)
            if not np.all(pixels[:, :, 3] == 255):
                raise RuntimeError(f"{name} is not fully opaque.")
        if spec.preserved_output_sha256:
            actual_hash = hashlib.sha256(
                (directory / name).read_bytes()
            ).hexdigest().upper()
            expected_hash = spec.preserved_output_sha256[index]
            if actual_hash != expected_hash:
                raise RuntimeError(
                    f"{name} SHA-256 is {actual_hash}; "
                    f"expected preserved hash {expected_hash}."
                )


def load_written_frames(
    generated: list[GeneratedFrame],
    spec: SequenceSpec,
) -> list[GeneratedFrame]:
    loaded: list[GeneratedFrame] = []
    directory = output_dir(spec)
    for index, item in enumerate(generated):
        path = directory / f"{spec.output_prefix}_{index:02d}.png"
        frame = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
        if frame is None or frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(f"Could not reload required runtime frame: {path}")
        loaded.append(replace(item, frame=frame))
    return loaded


def make_contact_sheet(
    generated: list[GeneratedFrame],
    spec: SequenceSpec,
) -> np.ndarray:
    cell_width = 512
    cell_height = 960
    sheet = np.zeros(
        (cell_height, cell_width * len(generated), 3),
        dtype=np.uint8,
    )
    for index, item in enumerate(generated):
        cell_x = index * cell_width
        sheet[70:934, cell_x : cell_x + 512] = item.frame[:, :, :3]
        cv2.putText(
            sheet,
            f"{index:02d} {spec.pose_names[index]}",
            (cell_x + 18, 42),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.58,
            (235, 235, 235),
            2,
            cv2.LINE_AA,
        )
    return sheet


def make_combined_contact_sheet(
    cross_frames: list[GeneratedFrame],
    release_frames: list[GeneratedFrame],
) -> np.ndarray:
    cell_width = 512
    cell_height = 960
    column_count = max(4, len(release_frames))
    sheet = np.zeros(
        (cell_height * 2, cell_width * column_count, 3),
        dtype=np.uint8,
    )
    rows = (
        (
            "CROSS",
            cross_frames + [cross_frames[-1]],
            (
                *CROSS_SPEC.pose_names,
                "persistent_hold_cross_02",
            ),
        ),
        ("RELEASE", release_frames, RELEASE_SPEC.pose_names),
    )
    for row, (label, generated, pose_names) in enumerate(rows):
        for index, item in enumerate(generated):
            cell_x = index * cell_width
            cell_y = row * cell_height
            sheet[
                cell_y + 70 : cell_y + 934,
                cell_x : cell_x + 512,
            ] = item.frame[:, :, :3]
            cv2.putText(
                sheet,
                f"{label} {index:02d} {pose_names[index]}",
                (cell_x + 14, cell_y + 42),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.54,
                (235, 235, 235),
                2,
                cv2.LINE_AA,
            )
    canonical = load_runtime_frame(CANONICAL_FRONT_PATH)
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    for guide_y, color in (
        (canonical_metrics.head_top, (0, 80, 255)),
        (canonical_metrics.shoulder_top, (0, 220, 255)),
        (canonical_metrics.waist_bottom, (255, 180, 0)),
        (canonical_metrics.shoe_baseline, (255, 80, 255)),
    ):
        for row in range(2):
            display_y = row * cell_height + 70 + guide_y
            cv2.line(
                sheet,
                (0, display_y),
                (sheet.shape[1] - 1, display_y),
                color,
                1,
                cv2.LINE_8,
            )
    return sheet


def process_sequence(
    spec: SequenceSpec,
    write_outputs: bool = True,
) -> tuple[list[GeneratedFrame], str]:
    source, hash_before = validate_source(spec)
    validate_source_geometry(spec)
    cv2.setRNGSeed(0)
    generated = generate_frames(source, spec)
    validate_generated(generated, spec)
    if spec.preserved_output_sha256:
        validate_written_outputs(spec)
        generated = load_written_frames(generated, spec)
        print(
            f"sequence={spec.output_directory} outputs=preserved "
            "hashes_unchanged=true"
        )
    elif write_outputs:
        write_frames(generated, spec)
    if spec.preserved_output_sha256 or write_outputs:
        validate_written_outputs(spec)
    hash_after = source_sha256(spec)
    if hash_after != hash_before:
        raise RuntimeError(
            f"{spec.source_name} changed during extraction: "
            f"{hash_before} -> {hash_after}."
        )
    frame_count = len(spec.pose_names)
    duration = frame_count / spec.playback_fps
    playback_order = tuple(range(frame_count))
    print(
        f"sequence={spec.output_directory} working_scale={spec.scale} "
        f"canonical_height_scales={spec.canonical_height_scales} "
        f"target_torso_x={TARGET_TORSO_X} "
        f"target_shoe_baseline_y={TARGET_SHOE_BASELINE_Y} "
        f"playback_order={playback_order} "
        f"source_human_frames={spec.selected_human_frames} "
        f"playback_fps={spec.playback_fps:g} "
        f"duration_seconds={duration:.3f} terminal={spec.terminal_pose} "
        "labels=none dividers=none source_unchanged=true"
    )
    return generated, hash_before


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate cross-arm and release transition frames."
    )
    parser.add_argument(
        "--evidence",
        action="store_true",
        help="also write numbered transition and combined contact sheets",
    )
    parser.add_argument(
        "--validate-height-assets",
        action="store_true",
        help="validate existing cross/release assets against canonical front",
    )
    parser.add_argument(
        "--height-evidence-stage",
        choices=("pre-fix", "post-fix"),
        default="post-fix",
        help="label for the canonical-height overlay evidence",
    )
    args = parser.parse_args()

    if args.validate_height_assets:
        validate_existing_height_assets(
            args.height_evidence_stage,
            write_evidence=args.evidence,
        )
        return 0

    cross_frames, _ = process_sequence(CROSS_SPEC, write_outputs=False)
    release_frames, _ = process_sequence(RELEASE_SPEC, write_outputs=False)
    cross_frames, release_frames, selected_offsets = (
        align_cross_release_continuity(
            cross_frames,
            release_frames,
        )
    )
    validate_final_continuity(
        cross_frames,
        release_frames,
        selected_offsets,
    )
    write_frames(cross_frames, CROSS_SPEC)
    write_frames(release_frames, RELEASE_SPEC)
    validate_written_outputs(CROSS_SPEC)
    validate_written_outputs(RELEASE_SPEC)

    if args.evidence:
        EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
        for generated, spec in (
            (cross_frames, CROSS_SPEC),
            (release_frames, RELEASE_SPEC),
        ):
            path = EVIDENCE_DIR / spec.contact_sheet_name
            sheet = make_contact_sheet(generated, spec)
            write_png(path, sheet)
            print(
                f"evidence={path.relative_to(ROOT)} "
                f"shape={sheet.shape} sequence={spec.output_directory}"
            )
        combined = make_combined_contact_sheet(cross_frames, release_frames)
        write_png(COMBINED_CONTACT_SHEET_PATH, combined)
        print(
            f"evidence={COMBINED_CONTACT_SHEET_PATH.relative_to(ROOT)} "
            f"shape={combined.shape} sequence=cross_hold_release"
        )

    validate_existing_height_assets(
        "post-fix",
        write_evidence=args.evidence,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
