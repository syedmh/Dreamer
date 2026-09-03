"""Generate the authoritative Clapping2 front-clap runtime frames.

Clapping2.png contains JPEG data under a .png extension. It is a labeled
3-column by 2-row sheet in normal human reading order:

    1 hands down -> 2 hands raise -> 3 hands approach
    -> 4 clap contact -> 5 hands rebound -> 6 hands down

All six labeled poses are valid and form the intended complete one-shot cycle.
Runtime therefore uses human frames 1,2,3,4,5,6 once, at a fixed 8 FPS.
"""

from __future__ import annotations

import argparse
import hashlib
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
    isolate_character,
)


SOURCE_PATH = ROOT / "Clapping2.png"
OUTPUT_DIR = ROOT / "Frames" / "Clap"
CANONICAL_FRONT_PATH = ROOT / "Frames" / "LeftTurn" / "turn_0.png"
EVIDENCE_DIR = (
    ROOT
    / ".ai-org"
    / "missions"
    / "2026-09-03-clapping2-cecc239c"
)
SOURCE_SELECTION_PATH = EVIDENCE_DIR / "clapping2-source-selection.png"
RUNTIME_SEQUENCE_PATH = EVIDENCE_DIR / "clapping2-runtime-sequence.png"
HEIGHT_OVERLAY_PATH = EVIDENCE_DIR / "clapping2-canonical-height-overlay.png"
HEIGHT_OVERLAY_ZOOMED_PATH = (
    EVIDENCE_DIR / "clapping2-canonical-height-overlay-zoomed.png"
)
MOTION_STRIP_PATH = EVIDENCE_DIR / "clapping2-motion-strip.png"

SOURCE_SHA256 = (
    "FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00"
)
SOURCE_SHAPE = (1448, 1086, 3)
SOURCE_LAYOUT = "3x2_labeled"
SOURCE_READING_ORDER = "left_to_right_top_to_bottom"
SELECTED_HUMAN_FRAMES = (1, 2, 3, 4, 5, 6)
PLAYBACK_ORDER = (0, 1, 2, 3, 4, 5)
POSE_NAMES = (
    "human_1_hands_down_start",
    "human_2_hands_raise_prepare",
    "human_3_hands_moving_together",
    "human_4_clap_contact",
    "human_5_hands_separate_rebound",
    "human_6_hands_down_finish",
)
CONTACT_STEPS = (3,)

CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
FRAME_COUNT = 6
PLAYBACK_FPS = 8.0
WORKING_SCALE = 1.22
TARGET_TORSO_X = 256
TARGET_WORKING_SHOE_Y = 847
TARGET_VISIBLE_SHOE_Y = 843
HEIGHT_TOLERANCE = 4
LANDMARK_TOLERANCE = 14
TORSO_TOLERANCE = 4

# White vertical divider bands occupy x=357..361 and x=721..724. The white
# horizontal divider occupies y=737..740. Top labels begin at about y=681;
# bottom labels begin at about y=1397. These exclusive crops retain safe black
# margins around each complete character while excluding every divider/label.
SOURCE_CROPS = (
    (0, 0, 356, 675),
    (363, 0, 718, 675),
    (726, 0, 1086, 675),
    (0, 743, 356, 1390),
    (363, 743, 718, 1390),
    (726, 743, 1086, 1390),
)
PERSON_COMPONENT_BOUNDS = (
    (51, 26, 279, 665),
    (429, 27, 650, 665),
    (778, 27, 1004, 666),
    (52, 753, 280, 1384),
    (424, 753, 649, 1384),
    (783, 753, 1005, 1383),
)

# Torso axes use the median center of the broad green-vest runs between source
# shoulder and waist rows, avoiding pose-dependent hand/arm extrema.
TORSO_CENTERS = (165.0, 176.5, 171.0, 168.5, 172.5, 168.0)
SOURCE_SHOE_BASELINES = (664, 664, 665, 1383, 1383, 1383)

# Uniform post-cutout scales are derived from each uncalibrated visible
# head-to-shoe span so the final head top and total body height match the
# canonical front texture while the visible shoe baseline stays fixed.
CANONICAL_HEIGHT_SCALES = (
    0.846547,
    0.847631,
    0.845467,
    0.858625,
    0.857328,
    0.856034,
)


@dataclass(frozen=True)
class AnatomicalMetrics:
    head_top: int
    shoulder_top: int
    waist_bottom: int
    shoe_baseline: int
    body_height: int
    torso_x: int


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


def source_sha256() -> str:
    return hashlib.sha256(SOURCE_PATH.read_bytes()).hexdigest().upper()


def validate_source() -> tuple[np.ndarray, str]:
    if not SOURCE_PATH.is_file():
        raise FileNotFoundError(f"Missing source image: {SOURCE_PATH}")

    hash_before = source_sha256()
    if hash_before != SOURCE_SHA256:
        raise RuntimeError(
            f"{SOURCE_PATH.name} SHA-256 is {hash_before}; "
            f"expected {SOURCE_SHA256}."
        )

    with Image.open(SOURCE_PATH) as image:
        source_format = image.format
        decoded_shape = (image.height, image.width, len(image.getbands()))
        decoded_mode = image.mode
    if source_format != "JPEG":
        raise RuntimeError(
            f"{SOURCE_PATH.name} container is {source_format}; expected JPEG "
            "data under the .png extension."
        )
    if decoded_shape != SOURCE_SHAPE or decoded_mode != "RGB":
        raise RuntimeError(
            f"{SOURCE_PATH.name} decoded as {decoded_mode} {decoded_shape}; "
            f"expected RGB {SOURCE_SHAPE}."
        )

    source = cv2.imread(str(SOURCE_PATH), cv2.IMREAD_COLOR)
    if source is None or source.shape != SOURCE_SHAPE:
        raise RuntimeError(
            f"{SOURCE_PATH.name} OpenCV shape is "
            f"{None if source is None else source.shape}; "
            f"expected {SOURCE_SHAPE}."
        )
    print(
        f"source={SOURCE_PATH.name} format={source_format} mode={decoded_mode} "
        f"shape={source.shape} sha256={hash_before}"
    )
    return source, hash_before


def validate_source_geometry() -> None:
    records = (
        SELECTED_HUMAN_FRAMES,
        PLAYBACK_ORDER,
        POSE_NAMES,
        SOURCE_CROPS,
        PERSON_COMPONENT_BOUNDS,
        TORSO_CENTERS,
        SOURCE_SHOE_BASELINES,
        CANONICAL_HEIGHT_SCALES,
    )
    if any(len(record) != FRAME_COUNT for record in records):
        raise RuntimeError("Clapping2 pose records are inconsistent.")
    if PLAYBACK_ORDER != tuple(range(FRAME_COUNT)):
        raise RuntimeError(
            f"Unexpected Clapping2 playback order: {PLAYBACK_ORDER}."
        )

    for index, (crop, bounds) in enumerate(
        zip(SOURCE_CROPS, PERSON_COMPONENT_BOUNDS, strict=True)
    ):
        x0, y0, x1, y1 = crop
        bx0, by0, bx1, by1 = bounds
        if not (x0 <= bx0 < bx1 <= x1 and y0 <= by0 < by1 <= y1):
            raise RuntimeError(
                f"Human frame {index + 1} crop {crop} clips bounds {bounds}."
            )
        included = []
        for component_index, other in enumerate(PERSON_COMPONENT_BOUNDS):
            ox0, oy0, ox1, oy1 = other
            if not (ox1 <= x0 or ox0 >= x1 or oy1 <= y0 or oy0 >= y1):
                included.append(component_index)
        if included != [index]:
            raise RuntimeError(
                f"Human frame {index + 1} crop includes components {included}."
            )

    print(
        f"layout={SOURCE_LAYOUT} reading_order={SOURCE_READING_ORDER} "
        "vertical_dividers=x357..361,x721..724 "
        "horizontal_divider=y737..740 "
        "top_labels=y681..732 bottom_labels=y1397..1447 "
        f"selected_human_frames={SELECTED_HUMAN_FRAMES} "
        f"source_crops={SOURCE_CROPS} "
        f"component_bounds={PERSON_COMPONENT_BOUNDS} "
        f"playback_order={PLAYBACK_ORDER} contact_steps={CONTACT_STEPS}"
    )


def make_cutout_config(index: int) -> CutoutConfig:
    return CutoutConfig(
        f"Clap/clap_{index:02d}",
        (55, 35, 460, 860),
        (105, 145, 415, 650),
        ((150, 700, 245, 855), (305, 700, 390, 855)),
        700,
        minimum_shoe_pixels=50,
        minimum_dark_shoe_retention=0.90,
        minimum_visible_shoe_retention=0.96,
    )


def measure_anatomy(frame: np.ndarray, name: str) -> AnatomicalMetrics:
    if frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
        raise RuntimeError(f"{name}: unexpected shape {frame.shape}.")
    bgr = frame[:, :, :3]
    foreground = np.max(bgr, axis=2) > 0
    rows = np.flatnonzero(np.any(foreground, axis=1))
    if rows.size == 0:
        raise RuntimeError(f"{name}: no visible foreground.")

    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    green = (
        (hsv[:, :, 0] >= 35)
        & (hsv[:, :, 0] <= 100)
        & (hsv[:, :, 1] >= 45)
        & (hsv[:, :, 2] >= 25)
        & foreground
    )
    green_rows = np.flatnonzero(np.count_nonzero(green, axis=1) >= 12)
    if green_rows.size == 0:
        raise RuntimeError(f"{name}: could not locate green vest.")

    torso_centers = []
    for y in range(int(green_rows[0]), int(green_rows[-1]) + 1):
        xs = np.flatnonzero(green[y])
        if xs.size >= 35:
            torso_centers.append(
                (float(np.percentile(xs, 5)) + float(np.percentile(xs, 95)))
                / 2.0
            )
    if not torso_centers:
        raise RuntimeError(f"{name}: could not measure torso axis.")

    head_top = int(rows[0])
    shoe_baseline = int(rows[-1])
    return AnatomicalMetrics(
        head_top=head_top,
        shoulder_top=int(green_rows[0]),
        waist_bottom=int(green_rows[-1]),
        shoe_baseline=shoe_baseline,
        body_height=shoe_baseline - head_top + 1,
        torso_x=round(float(np.median(torso_centers))),
    )


def calibrate_to_canonical_height(
    cutout: CutoutResult,
    scale: float,
    source_visible_shoe_baseline: int,
) -> tuple[CutoutResult, float]:
    vertical_translation = (
        TARGET_VISIBLE_SHOE_Y - scale * source_visible_shoe_baseline
    )
    transform = np.array(
        (
            (scale, 0.0, TARGET_TORSO_X * (1.0 - scale)),
            (0.0, scale, vertical_translation),
        ),
        dtype=np.float32,
    )
    rgb = cv2.warpAffine(
        cutout.frame[:, :, :3],
        transform,
        (CANVAS_WIDTH, CANVAS_HEIGHT),
        flags=cv2.INTER_LANCZOS4,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=(0, 0, 0),
    )
    matte = cv2.warpAffine(
        cutout.matte,
        transform,
        (CANVAS_WIDTH, CANVAS_HEIGHT),
        flags=cv2.INTER_NEAREST,
        borderMode=cv2.BORDER_CONSTANT,
        borderValue=0,
    )
    rgb[matte == 0] = 0
    frame = np.zeros_like(cutout.frame)
    frame[:, :, :3] = rgb
    frame[:, :, 3] = 255
    return (
        replace(
            cutout,
            frame=frame,
            matte=matte,
            foreground_pixels=int(np.count_nonzero(matte)),
            black_ratio=1.0 - np.count_nonzero(matte) / matte.size,
        ),
        vertical_translation,
    )


def generate_frames(source: np.ndarray) -> list[GeneratedFrame]:
    cv2.setRNGSeed(0)
    generated = []
    for index, (
        crop,
        torso_center,
        shoe_baseline,
        height_scale,
    ) in enumerate(
        zip(
            SOURCE_CROPS,
            TORSO_CENTERS,
            SOURCE_SHOE_BASELINES,
            CANONICAL_HEIGHT_SCALES,
            strict=True,
        )
    ):
        x0, y0, x1, y1 = crop
        panel = source[y0:y1, x0:x1].copy()
        resized_width = round(panel.shape[1] * WORKING_SCALE)
        resized_height = round(panel.shape[0] * WORKING_SCALE)
        panel = cv2.resize(
            panel,
            (resized_width, resized_height),
            interpolation=cv2.INTER_LANCZOS4,
        )
        offset_x = TARGET_TORSO_X - round(torso_center * WORKING_SCALE)
        offset_y = TARGET_WORKING_SHOE_Y - round(
            (shoe_baseline - y0) * WORKING_SCALE
        )
        if (
            offset_x < 0
            or offset_y < 0
            or offset_x + resized_width > CANVAS_WIDTH
            or offset_y + resized_height > CANVAS_HEIGHT
        ):
            raise RuntimeError(
                f"Human frame {index + 1} panel does not fit canvas: "
                f"{resized_width}x{resized_height} at ({offset_x},{offset_y})."
            )

        canvas = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH, 4), dtype=np.uint8)
        canvas[:, :, 3] = 255
        canvas[
            offset_y : offset_y + resized_height,
            offset_x : offset_x + resized_width,
            :3,
        ] = panel
        cutout = isolate_character(canvas, make_cutout_config(index))
        uncalibrated = measure_anatomy(
            cutout.frame, f"clap_{index:02d} uncalibrated"
        )
        cutout, vertical_translation = calibrate_to_canonical_height(
            cutout,
            height_scale,
            uncalibrated.shoe_baseline,
        )
        generated.append(
            GeneratedFrame(
                panel=panel,
                frame=cutout.frame,
                cutout=cutout,
                offset_x=offset_x,
                offset_y=offset_y,
                canonical_height_scale=height_scale,
                canonical_vertical_translation=vertical_translation,
                uncalibrated_metrics=uncalibrated,
            )
        )
    return generated


def load_frame(path: Path) -> np.ndarray:
    frame = cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
    if frame is None:
        raise RuntimeError(f"Could not load image: {path}")
    return frame


def validate_generated(generated: list[GeneratedFrame]) -> None:
    canonical = load_frame(CANONICAL_FRONT_PATH)
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    failures = []
    for index, item in enumerate(generated):
        if item.frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(f"clap_{index:02d}: shape {item.frame.shape}.")
        if not np.all(item.frame[:, :, 3] == 255):
            raise RuntimeError(f"clap_{index:02d}: output is not opaque.")
        if np.any(item.frame[:, :, :3][item.cutout.matte == 0] != 0):
            raise RuntimeError(
                f"clap_{index:02d}: background is not exact RGB black."
            )

        metrics = measure_anatomy(item.frame, f"clap_{index:02d}")
        deltas = AnatomicalMetrics(
            head_top=metrics.head_top - canonical_metrics.head_top,
            shoulder_top=metrics.shoulder_top - canonical_metrics.shoulder_top,
            waist_bottom=metrics.waist_bottom - canonical_metrics.waist_bottom,
            shoe_baseline=(
                metrics.shoe_baseline - canonical_metrics.shoe_baseline
            ),
            body_height=metrics.body_height - canonical_metrics.body_height,
            torso_x=metrics.torso_x - TARGET_TORSO_X,
        )
        print(
            f"frame={index:02d} human_frame={index + 1} "
            f"pose={POSE_NAMES[index]} crop={SOURCE_CROPS[index]} "
            f"bounds={PERSON_COMPONENT_BOUNDS[index]} "
            f"offset=({item.offset_x},{item.offset_y}) "
            f"working_scale={WORKING_SCALE:.6f} "
            f"canonical_height_scale={item.canonical_height_scale:.6f} "
            f"effective_source_scale="
            f"{WORKING_SCALE * item.canonical_height_scale:.6f} "
            f"pre=(head={item.uncalibrated_metrics.head_top},"
            f"shoulder={item.uncalibrated_metrics.shoulder_top},"
            f"waist={item.uncalibrated_metrics.waist_bottom},"
            f"shoe={item.uncalibrated_metrics.shoe_baseline},"
            f"height={item.uncalibrated_metrics.body_height},"
            f"torso_x={item.uncalibrated_metrics.torso_x}) "
            f"post=(head={metrics.head_top},shoulder={metrics.shoulder_top},"
            f"waist={metrics.waist_bottom},shoe={metrics.shoe_baseline},"
            f"height={metrics.body_height},torso_x={metrics.torso_x}) "
            f"residuals=(head={deltas.head_top:+d},"
            f"shoulder={deltas.shoulder_top:+d},"
            f"waist={deltas.waist_bottom:+d},"
            f"shoe={deltas.shoe_baseline:+d},"
            f"height={deltas.body_height:+d},"
            f"torso_x={deltas.torso_x:+d}) "
            f"black_ratio={item.cutout.black_ratio:.4f}"
        )
        if (
            abs(deltas.head_top) > HEIGHT_TOLERANCE
            or abs(deltas.shoe_baseline) > HEIGHT_TOLERANCE
            or abs(deltas.body_height) > HEIGHT_TOLERANCE
            or abs(deltas.shoulder_top) > LANDMARK_TOLERANCE
            or abs(deltas.waist_bottom) > LANDMARK_TOLERANCE
            or abs(deltas.torso_x) > TORSO_TOLERANCE
        ):
            failures.append(
                f"clap_{index:02d}: "
                f"head {deltas.head_top:+d}, "
                f"shoulder {deltas.shoulder_top:+d}, "
                f"waist {deltas.waist_bottom:+d}, "
                f"shoe {deltas.shoe_baseline:+d}, "
                f"height {deltas.body_height:+d}, "
                f"torso_x {deltas.torso_x:+d}"
            )
    if failures:
        raise RuntimeError(
            "Clap canonical alignment failed: " + "; ".join(failures)
        )


def remove_stale_outputs() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    expected = {f"clap_{index:02d}.png" for index in range(FRAME_COUNT)}
    expected_imports = {f"{name}.import" for name in expected}
    for path in sorted(OUTPUT_DIR.glob("clap_*.png")):
        if path.name not in expected:
            path.unlink()
            print(f"removed_stale={path.relative_to(ROOT)}")
    for path in sorted(OUTPUT_DIR.glob("clap_*.png.import")):
        if path.name not in expected_imports:
            path.unlink()
            print(f"removed_stale={path.relative_to(ROOT)}")


def write_png(path: Path, image: np.ndarray) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    if not cv2.imwrite(str(path), image, [cv2.IMWRITE_PNG_COMPRESSION, 9]):
        raise RuntimeError(f"Failed to write {path}.")


def write_frames(generated: list[GeneratedFrame]) -> None:
    for index, item in enumerate(generated):
        path = OUTPUT_DIR / f"clap_{index:02d}.png"
        write_png(path, item.frame)
        print(
            f"wrote={path.relative_to(ROOT)} shape={item.frame.shape} "
            "mode=RGBA opaque=true"
        )


def validate_written_outputs() -> None:
    expected = {f"clap_{index:02d}.png" for index in range(FRAME_COUNT)}
    actual = {path.name for path in OUTPUT_DIR.glob("clap_*.png")}
    if actual != expected:
        raise RuntimeError(
            f"Clap output set is {sorted(actual)}; expected {sorted(expected)}."
        )
    stale_imports = [
        path.name
        for path in OUTPUT_DIR.glob("clap_*.png.import")
        if path.name not in {f"{name}.import" for name in expected}
    ]
    if stale_imports:
        raise RuntimeError(f"Stale clap import sidecars remain: {stale_imports}.")
    for name in sorted(expected):
        with Image.open(OUTPUT_DIR / name) as image:
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


def make_source_selection(source: np.ndarray) -> np.ndarray:
    scale = 0.64
    cell_width = 360
    cell_height = 500
    sheet = np.zeros((cell_height * 2, cell_width * 3, 3), dtype=np.uint8)
    for index, crop in enumerate(SOURCE_CROPS):
        x0, y0, x1, y1 = crop
        panel = source[y0:y1, x0:x1]
        resized = cv2.resize(
            panel,
            (
                round(panel.shape[1] * scale),
                round(panel.shape[0] * scale),
            ),
            interpolation=cv2.INTER_AREA,
        )
        row, column = divmod(index, 3)
        cell_x = column * cell_width
        cell_y = row * cell_height
        sheet[
            cell_y + 54 : cell_y + 54 + resized.shape[0],
            cell_x : cell_x + resized.shape[1],
        ] = resized
        cv2.putText(
            sheet,
            f"human {index + 1} -> runtime {index:02d}",
            (cell_x + 8, cell_y + 23),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.48,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
        cv2.putText(
            sheet,
            POSE_NAMES[index].replace(f"human_{index + 1}_", ""),
            (cell_x + 8, cell_y + 44),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.38,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def make_runtime_sequence(generated: list[GeneratedFrame]) -> np.ndarray:
    cell_width = CANVAS_WIDTH
    cell_height = 925
    sheet = np.zeros((cell_height, cell_width * FRAME_COUNT, 3), np.uint8)
    for step, source_index in enumerate(PLAYBACK_ORDER):
        cell_x = step * cell_width
        sheet[55:919, cell_x : cell_x + cell_width] = (
            generated[source_index].frame[:, :, :3]
        )
        contact = " CONTACT" if step in CONTACT_STEPS else ""
        cv2.putText(
            sheet,
            f"step {step} human {source_index + 1}{contact}",
            (cell_x + 10, 32),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.5,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def make_height_overlay(
    canonical: np.ndarray,
    generated: list[GeneratedFrame],
    zoomed: bool,
) -> np.ndarray:
    all_frames = [canonical, *(item.frame for item in generated)]
    all_names = ["canonical", *(f"clap_{i:02d}" for i in range(FRAME_COUNT))]
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    crop = (95, 155, 417, 855) if zoomed else (0, 0, 512, 864)
    zoom = 2 if zoomed else 1
    x0, y0, x1, y1 = crop
    cell_width = (x1 - x0) * zoom
    top = 62
    cell_height = top + (y1 - y0) * zoom
    sheet = np.zeros((cell_height, cell_width * len(all_frames), 3), np.uint8)
    guides = (
        (canonical_metrics.head_top, (0, 80, 255)),
        (canonical_metrics.shoulder_top, (0, 220, 255)),
        (canonical_metrics.waist_bottom, (255, 180, 0)),
        (canonical_metrics.shoe_baseline, (255, 80, 255)),
    )
    for index, (name, frame) in enumerate(zip(all_names, all_frames, strict=True)):
        metrics = measure_anatomy(frame, name)
        cell_x = index * cell_width
        visible = frame[y0:y1, x0:x1, :3]
        if zoomed:
            visible = cv2.resize(
                visible,
                (cell_width, cell_height - top),
                interpolation=cv2.INTER_NEAREST,
            )
        sheet[top:, cell_x : cell_x + cell_width] = visible
        for guide_y, color in guides:
            display_y = top + (guide_y - y0) * zoom
            if top <= display_y < cell_height:
                cv2.line(
                    sheet,
                    (cell_x, display_y),
                    (cell_x + cell_width - 1, display_y),
                    color,
                    zoom,
                    cv2.LINE_8,
                )
        cv2.putText(
            sheet,
            (
                f"{name} top={metrics.head_top} shoe={metrics.shoe_baseline} "
                f"h={metrics.body_height} x={metrics.torso_x}"
            ),
            (cell_x + 6, 34),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.39 if zoomed else 0.34,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def make_motion_strip(generated: list[GeneratedFrame]) -> np.ndarray:
    # One pass at quarter size, repeated twice to make cadence/contact obvious.
    width, height = 160, 270
    repeated_order = (*PLAYBACK_ORDER, *PLAYBACK_ORDER)
    sheet = np.zeros((height + 34, width * len(repeated_order), 3), np.uint8)
    for step, source_index in enumerate(repeated_order):
        thumb = cv2.resize(
            generated[source_index].frame[:, :, :3],
            (width, height),
            interpolation=cv2.INTER_AREA,
        )
        x = step * width
        sheet[34:, x : x + width] = thumb
        cv2.putText(
            sheet,
            f"{step / PLAYBACK_FPS:.3f}s",
            (x + 4, 22),
            cv2.FONT_HERSHEY_SIMPLEX,
            0.38,
            (235, 235, 235),
            1,
            cv2.LINE_AA,
        )
    return sheet


def write_evidence(source: np.ndarray, generated: list[GeneratedFrame]) -> None:
    canonical = load_frame(CANONICAL_FRONT_PATH)
    write_png(SOURCE_SELECTION_PATH, make_source_selection(source))
    write_png(RUNTIME_SEQUENCE_PATH, make_runtime_sequence(generated))
    write_png(
        HEIGHT_OVERLAY_PATH,
        make_height_overlay(canonical, generated, zoomed=False),
    )
    write_png(
        HEIGHT_OVERLAY_ZOOMED_PATH,
        make_height_overlay(canonical, generated, zoomed=True),
    )
    write_png(MOTION_STRIP_PATH, make_motion_strip(generated))
    for path in (
        SOURCE_SELECTION_PATH,
        RUNTIME_SEQUENCE_PATH,
        HEIGHT_OVERLAY_PATH,
        HEIGHT_OVERLAY_ZOOMED_PATH,
        MOTION_STRIP_PATH,
    ):
        print(f"evidence={path.relative_to(ROOT)}")


def validate_existing_height_assets() -> None:
    canonical = load_frame(CANONICAL_FRONT_PATH)
    canonical_metrics = measure_anatomy(canonical, "canonical front")
    failures = []
    for index in range(FRAME_COUNT):
        path = OUTPUT_DIR / f"clap_{index:02d}.png"
        metrics = measure_anatomy(load_frame(path), str(path.relative_to(ROOT)))
        residuals = (
            metrics.head_top - canonical_metrics.head_top,
            metrics.shoulder_top - canonical_metrics.shoulder_top,
            metrics.waist_bottom - canonical_metrics.waist_bottom,
            metrics.shoe_baseline - canonical_metrics.shoe_baseline,
            metrics.body_height - canonical_metrics.body_height,
            metrics.torso_x - TARGET_TORSO_X,
        )
        print(
            f"height_check={path.relative_to(ROOT)} "
            f"metrics=(head={metrics.head_top},shoulder={metrics.shoulder_top},"
            f"waist={metrics.waist_bottom},shoe={metrics.shoe_baseline},"
            f"height={metrics.body_height},torso_x={metrics.torso_x}) "
            f"residuals={residuals}"
        )
        if (
            abs(residuals[0]) > HEIGHT_TOLERANCE
            or abs(residuals[1]) > LANDMARK_TOLERANCE
            or abs(residuals[2]) > LANDMARK_TOLERANCE
            or abs(residuals[3]) > HEIGHT_TOLERANCE
            or abs(residuals[4]) > HEIGHT_TOLERANCE
            or abs(residuals[5]) > TORSO_TOLERANCE
        ):
            failures.append(f"{path.name}: {residuals}")
    if failures:
        raise RuntimeError("Clap asset height validation failed: " + "; ".join(failures))


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate the six-pose Clapping2 front clap."
    )
    parser.add_argument(
        "--evidence",
        action="store_true",
        help="write source, runtime, height, zoom, and cadence review sheets",
    )
    parser.add_argument(
        "--validate-height-assets",
        action="store_true",
        help="validate the existing runtime clap frames without regenerating",
    )
    args = parser.parse_args()

    if args.validate_height_assets:
        validate_written_outputs()
        validate_existing_height_assets()
        return 0

    source, hash_before = validate_source()
    validate_source_geometry()
    generated = generate_frames(source)
    validate_generated(generated)
    remove_stale_outputs()
    write_frames(generated)
    validate_written_outputs()
    validate_existing_height_assets()
    if args.evidence:
        write_evidence(source, generated)

    hash_after = source_sha256()
    if hash_after != hash_before:
        raise RuntimeError(
            f"{SOURCE_PATH.name} changed during extraction: "
            f"{hash_before} -> {hash_after}."
        )
    duration = len(PLAYBACK_ORDER) / PLAYBACK_FPS
    print(
        f"runtime_assets={FRAME_COUNT} playback_steps={len(PLAYBACK_ORDER)} "
        f"source_human_frames={SELECTED_HUMAN_FRAMES} "
        f"playback_order={PLAYBACK_ORDER} playback_fps={PLAYBACK_FPS:g} "
        f"duration_seconds={duration:.3f} contact_steps={CONTACT_STEPS} "
        "repeated_contact_cycle=false source=Clapping2.png "
        "labels_removed=true dividers_removed=true source_unchanged=true"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
