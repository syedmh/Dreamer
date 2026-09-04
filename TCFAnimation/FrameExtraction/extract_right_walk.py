"""Generate the shared six-pose right walk and exact mirrored left walk."""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
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
from file_integrity import sha256_file  # noqa: E402


SOURCE_PATH = ROOT / "RWalking2.png"
RIGHT_OUTPUT_DIR = ROOT / "Frames" / "RightWalk"
LEFT_OUTPUT_DIR = ROOT / "Frames" / "LeftWalk"
EVIDENCE_DIR = (
    ROOT
    / ".ai-org"
    / "missions"
    / "2026-09-03-black-backgrounds-cecc239c"
)
CONTACT_SHEET_PATH = EVIDENCE_DIR / "black-background-contact-sheet.png"

SOURCE_SHA256 = (
    "CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A"
)
SOURCE_SIZE_BYTES = 495754
SOURCE_SHAPE = (1024, 1536, 3)
X_INTERVALS = (
    (0, 280),
    (281, 552),
    (553, 807),
    (808, 1044),
    (1045, 1302),
    (1303, 1536),
)
SOURCE_CROP_Y = (180, 760)
PERSON_COMPONENT_BOUNDS = (
    (22, 235, 268, 759),
    (292, 236, 540, 759),
    (564, 234, 778, 743),
    (837, 235, 995, 743),
    (1092, 235, 1271, 743),
    (1334, 235, 1529, 741),
)
TORSO_CENTERS = (128, 129, 125, 117, 119, 112)
POSE_LABELS = ("01", "02", "03", "04", "05", "06")

SCALE = 1.295
CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
OUTPUT_Y = 113
TARGET_TORSO_X = 256
EXPECTED_WIDTHS = (363, 351, 329, 306, 333, 302)
EXPECTED_HEIGHT = 751
EXPECTED_OFFSETS_X = (90, 89, 94, 104, 102, 111)
FRAME_COUNT = 6

# Calibrated after panel placement. The broad person boxes admit moving limbs;
# separate shoe boxes keep both dark shoes in every pose without admitting the
# studio-floor band.
CUTOUT_CONFIGS = (
    CutoutConfig(
        "RightWalk/walk_00",
        (105, 165, 455, 850),
        (165, 280, 345, 570),
        ((115, 680, 220, 833), (295, 680, 445, 833)),
        700,
        maximum_component_gap=18,
    ),
    CutoutConfig(
        "RightWalk/walk_01",
        (105, 165, 450, 850),
        (165, 280, 345, 570),
        ((105, 680, 210, 833), (290, 680, 435, 833)),
        700,
        maximum_component_gap=18,
    ),
    CutoutConfig(
        "RightWalk/walk_02",
        (115, 165, 410, 850),
        (165, 280, 345, 570),
        ((110, 680, 195, 841), (255, 680, 400, 841)),
        700,
        maximum_component_gap=18,
    ),
    CutoutConfig(
        "RightWalk/walk_03",
        (135, 165, 370, 850),
        (170, 280, 340, 570),
        ((150, 680, 225, 841), (225, 680, 365, 841)),
        700,
        maximum_component_gap=18,
    ),
    CutoutConfig(
        "RightWalk/walk_04",
        (135, 165, 420, 850),
        (165, 280, 345, 570),
        ((170, 680, 260, 842), (250, 680, 350, 842)),
        700,
        maximum_component_gap=18,
    ),
    CutoutConfig(
        "RightWalk/walk_05",
        (125, 165, 390, 850),
        (165, 280, 345, 570),
        ((145, 680, 230, 835), (285, 680, 385, 835)),
        700,
        maximum_component_gap=18,
    ),
)

ADJACENT_DIFFERENCE_MIN = 1.0
SOURCE_TOP_MARGIN_ROWS = 48
SOURCE_BOTTOM_FLOOR_ROWS = 8
BACKGROUND_MAX_MEAN = 12.0
FLOOR_MAX_MEAN = 20.0
BOUNDARY_ROI_HALF_WIDTH = 3
PERSON_PIXEL_MIN = 30


@dataclass(frozen=True)
class GeneratedFrame:
    panel: np.ndarray
    frame: np.ndarray
    cutout: CutoutResult
    offset_x: int


def source_sha256() -> str:
    return sha256_file(SOURCE_PATH, SOURCE_SIZE_BYTES)


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
    if source_format != "JPEG":
        raise RuntimeError(
            f"{SOURCE_PATH.name} container is {source_format}; expected JPEG "
            "data under the .png extension."
        )
    if decoded_shape != SOURCE_SHAPE:
        raise RuntimeError(
            f"{SOURCE_PATH.name} Pillow shape is {decoded_shape}; "
            f"expected {SOURCE_SHAPE}."
        )

    source = cv2.imread(str(SOURCE_PATH), cv2.IMREAD_COLOR)
    if source is None:
        raise RuntimeError(f"OpenCV could not decode {SOURCE_PATH}.")
    if source.shape != SOURCE_SHAPE:
        raise RuntimeError(
            f"{SOURCE_PATH.name} OpenCV shape is {source.shape}; "
            f"expected {SOURCE_SHAPE}."
        )

    print(
        f"source={SOURCE_PATH.relative_to(ROOT)} format={source_format} "
        f"shape={source.shape} sha256={hash_before}"
    )
    return source, hash_before


def generate_frames(source: np.ndarray) -> list[GeneratedFrame]:
    generated: list[GeneratedFrame] = []
    crop_y0, crop_y1 = SOURCE_CROP_Y

    for index, ((x0, x1), torso_center, cutout_config) in enumerate(
        zip(X_INTERVALS, TORSO_CENTERS, CUTOUT_CONFIGS, strict=True)
    ):
        clean_cell = source[crop_y0:crop_y1, x0:x1].copy()

        resized_width = round(clean_cell.shape[1] * SCALE)
        resized_height = round(clean_cell.shape[0] * SCALE)
        panel = cv2.resize(
            clean_cell,
            (resized_width, resized_height),
            interpolation=cv2.INTER_LANCZOS4,
        )
        offset_x = TARGET_TORSO_X - round(torso_center * SCALE)

        if (
            offset_x < 0
            or offset_x + resized_width > CANVAS_WIDTH
            or OUTPUT_Y < 0
            or OUTPUT_Y + resized_height > CANVAS_HEIGHT
        ):
            raise RuntimeError(
                f"Frame {index} panel {panel.shape} at "
                f"({offset_x}, {OUTPUT_Y}) does not fit the canvas."
            )

        frame = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH, 4), dtype=np.uint8)
        frame[:, :, 3] = 255
        frame[
            OUTPUT_Y : OUTPUT_Y + resized_height,
            offset_x : offset_x + resized_width,
            :3,
        ] = panel
        cutout = isolate_character(frame, cutout_config)
        generated.append(
            GeneratedFrame(panel, cutout.frame, cutout, offset_x)
        )

    return generated


def validate_generated_frames(generated: list[GeneratedFrame]) -> list[float]:
    if len(generated) != FRAME_COUNT:
        raise RuntimeError(
            f"Expected exactly {FRAME_COUNT} frames, got {len(generated)}."
        )

    actual_widths = tuple(item.panel.shape[1] for item in generated)
    if actual_widths != EXPECTED_WIDTHS:
        raise RuntimeError(
            f"Scaled widths are {actual_widths}; "
            f"expected {EXPECTED_WIDTHS}."
        )

    actual_offsets = tuple(item.offset_x for item in generated)
    if actual_offsets != EXPECTED_OFFSETS_X:
        raise RuntimeError(
            f"Panel x offsets are {actual_offsets}; "
            f"expected {EXPECTED_OFFSETS_X}."
        )

    for index, item in enumerate(generated):
        expected_width = EXPECTED_WIDTHS[index]
        if item.panel.shape != (EXPECTED_HEIGHT, expected_width, 3):
            raise RuntimeError(
                f"Frame {index} panel shape is {item.panel.shape}; expected "
                f"({EXPECTED_HEIGHT}, {expected_width}, 3)."
            )
        if item.frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(
                f"Frame {index} shape is {item.frame.shape}; expected "
                f"({CANVAS_HEIGHT}, {CANVAS_WIDTH}, 4)."
            )
        if not np.all(item.frame[:, :, 3] == 255):
            raise RuntimeError(f"Frame {index} is not fully opaque.")
        if OUTPUT_Y + item.panel.shape[0] != CANVAS_HEIGHT:
            raise RuntimeError(f"Frame {index} does not end at the canvas floor.")

        torso_x = item.offset_x + round(TORSO_CENTERS[index] * SCALE)
        if torso_x != TARGET_TORSO_X:
            raise RuntimeError(
                f"Frame {index} torso center is {torso_x}; "
                f"expected {TARGET_TORSO_X}."
            )

        print(
            f"cutout={item.cutout.black_ratio:.4f}_black "
            f"foreground_pixels={item.cutout.foreground_pixels} "
            f"shoe_pixels={item.cutout.shoe_pixels} "
            f"shoe_retention={item.cutout.shoe_retention} "
            f"shoe_dark_retention={item.cutout.shoe_dark_retention} "
            f"shoe_extent_retention={item.cutout.shoe_extent_retention} "
            f"shoe_visible_retention={item.cutout.shoe_visible_retention} "
            "shoe_visible_extent_retention="
            f"{item.cutout.shoe_visible_extent_retention}"
        )

    differences = [
        float(
            np.mean(
                cv2.absdiff(
                    generated[index].frame[:, :, :3],
                    generated[index + 1].frame[:, :, :3],
                )
            )
        )
        for index in range(FRAME_COUNT - 1)
    ]
    if min(differences) < ADJACENT_DIFFERENCE_MIN:
        raise RuntimeError(
            f"Adjacent poses are not sufficiently distinct: {differences}."
        )
    return differences


def validate_source_geometry(source: np.ndarray) -> None:
    crop_y0, crop_y1 = SOURCE_CROP_Y
    if crop_y1 - crop_y0 != 580:
        raise RuntimeError(f"Source crop height is {crop_y1 - crop_y0}; expected 580.")

    if len(X_INTERVALS) != FRAME_COUNT:
        raise RuntimeError(
            f"Expected {FRAME_COUNT} source x intervals, got {len(X_INTERVALS)}."
        )
    if len(PERSON_COMPONENT_BOUNDS) != FRAME_COUNT:
        raise RuntimeError(
            f"Expected {FRAME_COUNT} person components, "
            f"got {len(PERSON_COMPONENT_BOUNDS)}."
        )

    for index, ((x0, x1), intended_bounds) in enumerate(
        zip(X_INTERVALS, PERSON_COMPONENT_BOUNDS, strict=True)
    ):
        component_x0, component_y0, component_x1, component_y1 = intended_bounds
        if not (
            x0 <= component_x0 <= component_x1 < x1
            and crop_y0 <= component_y0 <= component_y1 < crop_y1
        ):
            raise RuntimeError(
                f"Crop {index} {(x0, crop_y0, x1, crop_y1)} clips its intended "
                f"person component {intended_bounds}."
            )

        included_components = []
        for component_index, bounds in enumerate(PERSON_COMPONENT_BOUNDS):
            other_x0, other_y0, other_x1, other_y1 = bounds
            intersects = not (
                other_x1 < x0
                or other_x0 >= x1
                or other_y1 < crop_y0
                or other_y0 >= crop_y1
            )
            if intersects:
                included_components.append(component_index)
        if included_components != [index]:
            raise RuntimeError(
                f"Crop {index} contains source person components "
                f"{included_components}; expected only [{index}]."
            )

    cropped = source[crop_y0:crop_y1, :]
    top_mean = float(np.mean(cropped[:SOURCE_TOP_MARGIN_ROWS]))
    bottom_mean = float(np.mean(cropped[-SOURCE_BOTTOM_FLOOR_ROWS:]))
    if top_mean > BACKGROUND_MAX_MEAN:
        raise RuntimeError(
            f"Source crop top margin mean is {top_mean:.2f}; "
            f"expected dark studio background <= {BACKGROUND_MAX_MEAN:.2f}."
        )
    if bottom_mean > FLOOR_MAX_MEAN:
        raise RuntimeError(
            f"Source crop bottom strip mean is {bottom_mean:.2f}; "
            f"expected dark studio floor <= {FLOOR_MAX_MEAN:.2f}."
        )

    boundary_stats = []
    for boundary_x in (interval[0] for interval in X_INTERVALS[1:]):
        neighborhood = source[
            crop_y0:crop_y1,
            boundary_x - BOUNDARY_ROI_HALF_WIDTH :
            boundary_x + BOUNDARY_ROI_HALF_WIDTH,
        ]
        brightest = np.max(neighborhood, axis=2)
        person_pixels = int(np.count_nonzero(brightest >= PERSON_PIXEL_MIN))
        boundary_stats.append(
            (
                boundary_x,
                person_pixels,
                int(np.max(brightest)),
                float(np.mean(brightest)),
            )
        )
    contaminated = [stats for stats in boundary_stats if stats[1] != 0]
    if contaminated:
        raise RuntimeError(
            "Crop boundary ROI contains bright/colored neighboring anatomy: "
            f"{contaminated}."
        )

    print(
        "source_components="
        f"{PERSON_COMPONENT_BOUNDS} crop_component_membership=one_each "
        f"boundary_rois={boundary_stats}"
    )


def validate_output_sets() -> None:
    RIGHT_OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    LEFT_OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    expected_names = {f"walk_{index:02d}.png" for index in range(FRAME_COUNT)}
    expected_imports = {f"{name}.import" for name in expected_names}

    unexpected = []
    for output_dir in (RIGHT_OUTPUT_DIR, LEFT_OUTPUT_DIR):
        unexpected.extend(
            path
            for pattern, allowed in (
                ("*.png", expected_names),
                ("*.png.import", expected_imports),
            )
            for path in output_dir.glob(pattern)
            if path.name not in allowed
        )
    if unexpected:
        raise RuntimeError(
            "Unexpected walk assets must be removed explicitly: "
            + ", ".join(str(path.relative_to(ROOT)) for path in unexpected)
        )


def write_png(path: Path, image: np.ndarray) -> None:
    if not cv2.imwrite(str(path), image, [cv2.IMWRITE_PNG_COMPRESSION, 9]):
        raise RuntimeError(f"Failed to write {path}.")


def validate_written_outputs() -> None:
    expected_names = {f"walk_{index:02d}.png" for index in range(FRAME_COUNT)}
    for direction, output_dir in (
        ("RightWalk", RIGHT_OUTPUT_DIR),
        ("LeftWalk", LEFT_OUTPUT_DIR),
    ):
        actual_names = {path.name for path in output_dir.glob("*.png")}
        if actual_names != expected_names:
            raise RuntimeError(
                f"{direction} output set is {sorted(actual_names)}; "
                f"expected {sorted(expected_names)}."
            )

        for name in sorted(expected_names):
            with Image.open(output_dir / name) as image:
                if image.format != "PNG":
                    raise RuntimeError(f"{direction}/{name} is not PNG.")
                if image.mode != "RGBA":
                    raise RuntimeError(
                        f"{direction}/{name} mode is {image.mode}, not RGBA."
                    )
                if image.size != (CANVAS_WIDTH, CANVAS_HEIGHT):
                    raise RuntimeError(
                        f"{direction}/{name} size is {image.size}; "
                        f"expected ({CANVAS_WIDTH}, {CANVAS_HEIGHT})."
                    )
                alpha = np.asarray(image.getchannel("A"), dtype=np.uint8)
                if not np.all(alpha == 255):
                    raise RuntimeError(
                        f"{direction}/{name} is not fully opaque."
                    )

    for name in sorted(expected_names):
        right = cv2.imread(
            str(RIGHT_OUTPUT_DIR / name),
            cv2.IMREAD_UNCHANGED,
        )
        left = cv2.imread(
            str(LEFT_OUTPUT_DIR / name),
            cv2.IMREAD_UNCHANGED,
        )
        if right is None or left is None:
            raise RuntimeError(f"Could not decode mirrored pair {name}.")
        if not np.array_equal(left, right[:, ::-1]):
            raise RuntimeError(f"LeftWalk/{name} is not an exact mirror.")
        print(f"mirror_verified={name} byte_exact=true")


def make_contact_sheet(generated: list[GeneratedFrame]) -> np.ndarray:
    sheet_width = 3840
    sheet_height = 2880
    columns = 6
    cell_width = sheet_width // columns
    cell_height = sheet_height // 3
    display_scale = 0.82
    sheet = np.zeros((sheet_height, sheet_width, 3), dtype=np.uint8)

    turn_paths = [
        ROOT / "Frames" / direction / f"turn_{index}.png"
        for direction in ("LeftTurn", "RightTurn")
        for index in range(3)
    ]
    turn_frames = [
        cv2.imread(str(path), cv2.IMREAD_UNCHANGED)
        for path in turn_paths
    ]
    if any(frame is None for frame in turn_frames):
        raise RuntimeError("Run extract_directional_turns.py before evidence.")

    rows = (
        ("TURNS", turn_frames),
        ("RIGHT WALK", [item.frame for item in generated]),
        ("LEFT WALK (EXACT MIRROR)", [item.frame[:, ::-1] for item in generated]),
    )
    for row, (label, frames) in enumerate(rows):
        for index, frame in enumerate(frames):
            display_width = round(CANVAS_WIDTH * display_scale)
            display_height = round(CANVAS_HEIGHT * display_scale)
            display = cv2.resize(
                frame[:, :, :3],
                (display_width, display_height),
                interpolation=cv2.INTER_NEAREST,
            )
            cell_x = index * cell_width
            cell_y = row * cell_height
            x = cell_x + (cell_width - display_width) // 2
            y = cell_y + 145
            sheet[y : y + display_height, x : x + display_width] = display
            cv2.putText(
                sheet,
                f"{label} {index + 1:02d}",
                (cell_x + 85, cell_y + 82),
                cv2.FONT_HERSHEY_SIMPLEX,
                0.9,
                (235, 235, 235),
                2,
                cv2.LINE_AA,
            )

    return sheet


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Generate six normalized right-walk frames and exact mirrored "
            "left-walk frames."
        )
    )
    parser.add_argument(
        "--evidence",
        action="store_true",
        help="also write the numbered two-row mirrored walk contact sheet",
    )
    args = parser.parse_args()

    source, hash_before = validate_source()
    validate_source_geometry(source)
    cv2.setRNGSeed(0)
    generated = generate_frames(source)
    differences = validate_generated_frames(generated)
    validate_output_sets()

    for index, item in enumerate(generated):
        name = f"walk_{index:02d}.png"
        right_output_path = RIGHT_OUTPUT_DIR / name
        left_output_path = LEFT_OUTPUT_DIR / name
        write_png(right_output_path, item.frame)
        write_png(left_output_path, item.frame[:, ::-1])
        print(
            f"wrote={right_output_path.relative_to(ROOT)} "
            f"shape={item.frame.shape} mode=RGBA opaque=true "
            f"panel={item.panel.shape[1]}x{item.panel.shape[0]} "
            f"offset=({item.offset_x},{OUTPUT_Y})"
        )
        print(
            f"wrote={left_output_path.relative_to(ROOT)} "
            f"shape={item.frame.shape} mode=RGBA opaque=true "
            "transform=right[:,::-1]"
        )
    validate_written_outputs()

    if args.evidence:
        EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
        write_png(CONTACT_SHEET_PATH, make_contact_sheet(generated))
        with Image.open(CONTACT_SHEET_PATH) as contact_sheet:
            if contact_sheet.format != "PNG" or contact_sheet.size != (3840, 2880):
                raise RuntimeError(
                    f"Contact sheet is {contact_sheet.format} "
                    f"{contact_sheet.size}; expected PNG (3840, 2880)."
                )
        print(
            f"evidence={CONTACT_SHEET_PATH.relative_to(ROOT)} "
            "shape=(2880, 3840, 3)"
        )

    hash_after = source_sha256()
    if hash_after != hash_before:
        raise RuntimeError(
            f"{SOURCE_PATH.name} changed during extraction: "
            f"{hash_before} -> {hash_after}."
        )

    print(f"x_intervals={X_INTERVALS} crop_y={SOURCE_CROP_Y}")
    print("labels=none dividers=none background=pure_black")
    print(f"scale={SCALE} interpolation=INTER_LANCZOS4")
    print(
        f"panel_widths={EXPECTED_WIDTHS} panel_height={EXPECTED_HEIGHT} "
        f"offsets_x={EXPECTED_OFFSETS_X} output_y={OUTPUT_Y}"
    )
    print(
        f"torso_centers={TORSO_CENTERS} "
        f"target_torso_x={TARGET_TORSO_X}"
    )
    print(f"adjacent_mean_color_differences={differences}")
    print(f"source_sha256={hash_after} source_unchanged=true")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
