"""Generate opaque full-body turn frames on uniform pure black."""

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


SOURCE_SHAPE = (1448, 1086, 3)
CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
TARGET_TORSO_CENTER_X = 256
TARGET_FLOOR_BASELINE_Y = CANVAS_HEIGHT - 1

@dataclass(frozen=True)
class SheetConfig:
    name: str
    source_path: Path
    source_sha256: str
    source_size_bytes: int
    source_format: str
    source_mode: str
    output_dir: Path
    panel_crops: tuple[tuple[int, int, int, int], ...]
    label_rect: tuple[int, int, int, int]
    panel_offsets: tuple[tuple[int, int], ...]
    torso_centers: tuple[int, ...]
    cutouts: tuple[CutoutConfig, ...]
    source_exclusions: tuple[tuple[Polygon, ...], ...]
    final_translations: tuple[tuple[int, int], ...]


@dataclass(frozen=True)
class GeneratedFrame:
    panel: np.ndarray
    frame: np.ndarray
    cutout: CutoutResult
    offset_x: int
    offset_y: int


# LTurning has antialiased vertical dividers at x=359..362 and x=721..724.
# Its horizontal divider starts at y=724. Existing calibrated translations are
# retained so the current left-turn visuals remain unchanged.
LEFT_CONFIG = SheetConfig(
    name="LeftTurn",
    source_path=ROOT / "LTurning.png",
    source_sha256=(
        "9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91"
    ),
    source_size_bytes=1502641,
    source_format="PNG",
    source_mode="RGBA",
    output_dir=ROOT / "Frames" / "LeftTurn",
    panel_crops=(
        (0, 0, 359, 724),
        (363, 0, 721, 724),
        (725, 0, 1086, 724),
    ),
    label_rect=(12, 14, 96, 50),
    panel_offsets=(
        (66, 140),
        (69, 140),
        (72, 140),
    ),
    torso_centers=(190, 187, 184),
    cutouts=(
        CutoutConfig(
            "LeftTurn/turn_0",
            (70, 160, 395, 860),
            (145, 270, 355, 575),
            ((165, 700, 255, 844), (275, 700, 355, 844)),
            730,
            floor_reject_rects=((185, 764, 205, 791),),
        ),
        CutoutConfig(
            "LeftTurn/turn_1",
            (115, 160, 390, 860),
            (150, 270, 350, 575),
            ((165, 700, 258, 845), (265, 700, 350, 845)),
            780,
        ),
        CutoutConfig(
            "LeftTurn/turn_2",
            (145, 160, 345, 860),
            (170, 270, 330, 575),
            ((195, 700, 260, 835), (235, 700, 325, 835)),
            730,
            maximum_component_gap=6,
            cool_reject_rects=((205, 730, 229, 805),),
        ),
    ),
    source_exclusions=(
        (((138, 686), (172, 686), (172, 704), (138, 704)),),
        (((485, 680), (552, 680), (552, 705), (485, 705)),),
        (
            ((856, 695), (876, 695), (876, 705), (856, 705)),
            ((863, 668), (874, 668), (874, 672), (863, 672)),
        ),
    ),
    final_translations=((0, 0), (0, 0), (2, 0)),
)

# RTurning uses different native cell widths. Its antialiased vertical
# dividers occupy x=356..358 and x=724..727, and the horizontal divider starts
# at y=725. The source label glyphs occupy x=19..83 and y=24..39 inside the
# clean cells. Native whole-panel translations align calibrated torso axes
# (183, 188, 177) at x=256 and the retained floor at y=863.
RIGHT_CONFIG = SheetConfig(
    name="RightTurn",
    source_path=ROOT / "RTurning.png",
    source_sha256=(
        "2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840"
    ),
    source_size_bytes=1390487,
    source_format="PNG",
    source_mode="RGB",
    output_dir=ROOT / "Frames" / "RightTurn",
    panel_crops=(
        (0, 0, 356, 725),
        (359, 0, 724, 725),
        (728, 0, 1086, 725),
    ),
    label_rect=(10, 14, 96, 50),
    panel_offsets=(
        (73, 139),
        (68, 139),
        (79, 139),
    ),
    torso_centers=(183, 188, 177),
    cutouts=(
        CutoutConfig(
            "RightTurn/turn_0",
            (95, 155, 405, 860),
            (145, 265, 365, 575),
            ((155, 700, 260, 843), (285, 700, 350, 843)),
            780,
        ),
        CutoutConfig(
            "RightTurn/turn_1",
            (120, 155, 390, 860),
            (155, 265, 360, 575),
            ((190, 700, 255, 848), (265, 700, 375, 833)),
            730,
            garment_rects=((168, 570, 190, 675),),
        ),
        CutoutConfig(
            "RightTurn/turn_2",
            (145, 155, 350, 860),
            (175, 265, 335, 575),
            ((195, 700, 275, 842), (255, 700, 335, 842)),
            780,
        ),
    ),
    source_exclusions=(
        (
            ((135, 687), (184, 687), (184, 705), (135, 705)),
            ((174, 627), (180, 627), (180, 636), (174, 636)),
        ),
        (),
        (((862, 697), (976, 697), (976, 704), (862, 704)),),
    ),
    final_translations=((0, 0), (0, -6), (2, 0)),
)

SHEET_CONFIGS = (LEFT_CONFIG, RIGHT_CONFIG)


def source_sha256(config: SheetConfig) -> str:
    return sha256_file(config.source_path, config.source_size_bytes)


def validate_source(config: SheetConfig) -> tuple[np.ndarray, str]:
    if not config.source_path.is_file():
        raise FileNotFoundError(config.source_path)

    hash_before = source_sha256(config)
    if hash_before != config.source_sha256:
        raise RuntimeError(
            f"{config.source_path.name} SHA-256 is {hash_before}; "
            f"expected {config.source_sha256}."
        )

    with Image.open(config.source_path) as image:
        source_format = image.format
        source_mode = image.mode
        source_size = image.size
    if source_format != config.source_format or source_mode != config.source_mode:
        raise RuntimeError(
            f"{config.source_path.name} is {source_format} {source_mode}; "
            f"expected {config.source_format} {config.source_mode}."
        )
    if source_size != (SOURCE_SHAPE[1], SOURCE_SHAPE[0]):
        raise RuntimeError(
            f"{config.source_path.name} size is {source_size}; "
            f"expected {(SOURCE_SHAPE[1], SOURCE_SHAPE[0])}."
        )

    source = cv2.imread(str(config.source_path), cv2.IMREAD_COLOR)
    if source is None:
        raise RuntimeError(f"OpenCV could not decode {config.source_path}.")
    if source.shape != SOURCE_SHAPE:
        raise RuntimeError(
            f"{config.name} source has shape {source.shape}; "
            f"expected {SOURCE_SHAPE}."
        )

    print(
        f"source={config.source_path.name} format={source_format} "
        f"mode={source_mode} shape={source.shape} sha256={hash_before}"
    )
    return source, hash_before


def prepare_panel(
    source: np.ndarray,
    crop: tuple[int, int, int, int],
    label_rect: tuple[int, int, int, int],
) -> np.ndarray:
    """Crop one complete source cell and remove its label."""

    x0, y0, x1, y1 = crop
    panel = source[y0:y1, x0:x1].copy()

    label_x0, label_y0, label_x1, label_y1 = label_rect
    panel[label_y0:label_y1, label_x0:label_x1] = 0
    return panel


def place_panel(panel: np.ndarray, offset: tuple[int, int]) -> np.ndarray:
    """Translate a native-size complete panel onto the shared opaque canvas."""

    offset_x, offset_y = offset
    height, width = panel.shape[:2]
    if (
        offset_x < 0
        or offset_y < 0
        or offset_x + width > CANVAS_WIDTH
        or offset_y + height > CANVAS_HEIGHT
    ):
        raise RuntimeError(
            f"Panel {panel.shape} at {offset} exceeds "
            f"{CANVAS_WIDTH}x{CANVAS_HEIGHT} canvas."
        )

    canvas = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH, 4), dtype=np.uint8)
    canvas[:, :, 3] = 255
    canvas[
        offset_y : offset_y + height,
        offset_x : offset_x + width,
        :3,
    ] = panel
    return canvas


def generate_frames(
    source: np.ndarray,
    config: SheetConfig,
) -> list[GeneratedFrame]:
    generated: list[GeneratedFrame] = []
    for crop, offset, cutout_config, source_exclusions, translation in zip(
        config.panel_crops,
        config.panel_offsets,
        config.cutouts,
        config.source_exclusions,
        config.final_translations,
        strict=True,
    ):
        panel = prepare_panel(source, crop, config.label_rect)
        placed = place_panel(panel, offset)
        exclusion_mask = project_source_exclusion_mask(
            (CANVAS_HEIGHT, CANVAS_WIDTH),
            crop,
            panel.shape[:2],
            offset,
            source_exclusions,
        )
        cutout = isolate_character(
            placed,
            cutout_config,
            source_exclusion_mask=exclusion_mask,
        )
        cutout = translate_cutout(cutout, *translation)
        generated.append(
            GeneratedFrame(panel, cutout.frame, cutout, *offset)
        )
    return generated


def translate_cutout(
    cutout: CutoutResult,
    delta_x: int,
    delta_y: int,
) -> CutoutResult:
    """Translate retained source pixels by whole pixels without resampling."""

    if delta_x == 0 and delta_y == 0:
        return cutout
    height, width = cutout.matte.shape
    source_x0 = max(0, -delta_x)
    source_y0 = max(0, -delta_y)
    source_x1 = min(width, width - delta_x)
    source_y1 = min(height, height - delta_y)
    target_x0 = source_x0 + delta_x
    target_y0 = source_y0 + delta_y
    target_x1 = source_x1 + delta_x
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


def canonicalize_front_identity(
    generated_by_name: dict[str, list[GeneratedFrame]],
) -> None:
    """Make both front runtime files the exact canonical right-front image."""

    left = generated_by_name["LeftTurn"]
    right = generated_by_name["RightTurn"]
    canonical = right[0]
    left_cutout = replace(
        left[0].cutout,
        frame=canonical.frame.copy(),
        matte=canonical.cutout.matte.copy(),
        black_ratio=canonical.cutout.black_ratio,
        foreground_pixels=canonical.cutout.foreground_pixels,
        shoe_pixels=canonical.cutout.shoe_pixels,
        shoe_retention=canonical.cutout.shoe_retention,
        shoe_dark_retention=canonical.cutout.shoe_dark_retention,
        shoe_extent_retention=canonical.cutout.shoe_extent_retention,
        shoe_visible_retention=canonical.cutout.shoe_visible_retention,
        shoe_visible_extent_retention=(
            canonical.cutout.shoe_visible_extent_retention
        ),
        visible_component_count=canonical.cutout.visible_component_count,
    )
    left[0] = replace(
        left[0],
        frame=canonical.frame.copy(),
        cutout=left_cutout,
    )
    if not np.array_equal(left[0].frame, right[0].frame):
        raise RuntimeError("Canonical front pixel identity failed.")


def validate_frames(
    source: np.ndarray,
    generated: list[GeneratedFrame],
    config: SheetConfig,
) -> None:
    if len(generated) != 3:
        raise RuntimeError(
            f"{config.name}: expected exactly 3 frames, got {len(generated)}."
        )
    if (
        len(config.source_exclusions) != len(generated)
        or len(config.final_translations) != len(generated)
    ):
        raise RuntimeError(
            f"{config.name}: source exclusion/translation records changed."
        )

    label_x0, label_y0, label_x1, label_y1 = config.label_rect
    for index, item in enumerate(generated):
        frame = item.frame
        panel = item.panel
        crop = config.panel_crops[index]
        source_panel = source[crop[1] : crop[3], crop[0] : crop[2]]

        if frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(
                f"{config.name} frame {index} has unexpected shape {frame.shape}."
            )
        if not np.all(frame[:, :, 3] == 255):
            raise RuntimeError(f"{config.name} frame {index} is not fully opaque.")
        if panel.shape != source_panel.shape:
            raise RuntimeError(
                f"{config.name} panel {index} changed dimensions: "
                f"{source_panel.shape} -> {panel.shape}."
            )
        if np.any(panel[label_y0:label_y1, label_x0:label_x1]):
            raise RuntimeError(
                f"{config.name} panel {index} label rectangle is not black."
            )

        expected_panel = source_panel.copy()
        expected_panel[label_y0:label_y1, label_x0:label_x1] = 0
        if not np.array_equal(panel, expected_panel):
            raise RuntimeError(
                f"{config.name} panel {index} altered source pixels outside label."
            )

        torso_x = item.offset_x + config.torso_centers[index]
        if torso_x != TARGET_TORSO_CENTER_X:
            raise RuntimeError(
                f"{config.name} panel {index} torso center is {torso_x}, "
                f"expected {TARGET_TORSO_CENTER_X}."
            )
        floor_y = item.offset_y + panel.shape[0] - 1
        if floor_y != TARGET_FLOOR_BASELINE_Y:
            raise RuntimeError(
                f"{config.name} panel {index} floor baseline is {floor_y}, "
                f"expected {TARGET_FLOOR_BASELINE_Y}."
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
        for index in range(2)
    ]
    if min(differences) < 1.0:
        raise RuntimeError(
            f"{config.name} adjacent poses are not distinct: {differences}."
        )

    print(f"sheet={config.name}")
    print(f"panel_crops={config.panel_crops}")
    print(f"label_rect={config.label_rect}")
    print(f"panel_offsets={config.panel_offsets}")
    print(f"torso_centers={config.torso_centers}")
    print(
        "alignment="
        f"torso_x:{TARGET_TORSO_CENTER_X} "
        f"floor_baseline_y:{TARGET_FLOOR_BASELINE_Y}"
    )
    print("background=pure_black segmentation=mask_initialized_grabcut")
    print(f"adjacent_mean_color_differences={differences}")


def validate_output_directory(config: SheetConfig) -> None:
    config.output_dir.mkdir(parents=True, exist_ok=True)
    expected_names = {f"turn_{index}.png" for index in range(3)}
    unexpected = [
        path
        for path in config.output_dir.glob("*.png")
        if path.name not in expected_names
    ]
    if unexpected:
        raise RuntimeError(
            "Unexpected runtime frame assets must be removed explicitly: "
            + ", ".join(str(path.relative_to(ROOT)) for path in unexpected)
        )


def write_png(path: Path, image: np.ndarray) -> None:
    if not cv2.imwrite(str(path), image, [cv2.IMWRITE_PNG_COMPRESSION, 9]):
        raise RuntimeError(f"Failed to write {path}.")


def main() -> int:
    parser = argparse.ArgumentParser(
        description=(
            "Generate only the three left-turn and three right-turn runtime "
            "frames."
        )
    )
    parser.add_argument(
        "--evidence",
        action="store_true",
        help="also refresh the six-frame directional-turn contact sheet",
    )
    args = parser.parse_args()

    cv2.setRNGSeed(0)
    generated_by_name: dict[str, list[GeneratedFrame]] = {}
    source_records: dict[str, tuple[SheetConfig, str]] = {}
    for config in SHEET_CONFIGS:
        source, hash_before = validate_source(config)

        generated = generate_frames(source, config)
        validate_frames(source, generated, config)
        validate_output_directory(config)
        generated_by_name[config.name] = generated
        source_records[config.name] = (config, hash_before)

    canonicalize_front_identity(generated_by_name)
    for config in SHEET_CONFIGS:
        generated = generated_by_name[config.name]
        for index, item in enumerate(generated):
            output_path = config.output_dir / f"turn_{index}.png"
            write_png(output_path, item.frame)
            print(
                f"wrote={output_path.relative_to(ROOT)} "
                f"shape={item.frame.shape}"
            )
        hash_before = source_records[config.name][1]
        hash_after = source_sha256(config)
        if hash_after != hash_before:
            raise RuntimeError(
                f"{config.source_path.name} changed during extraction: "
                f"{hash_before} -> {hash_after}."
            )
        print(
            f"source_sha256={hash_after} source_unchanged=true"
        )
    left_front = LEFT_CONFIG.output_dir / "turn_0.png"
    right_front = RIGHT_CONFIG.output_dir / "turn_0.png"
    if left_front.read_bytes() != right_front.read_bytes():
        raise RuntimeError("Canonical front PNG bytes are not identical.")
    print(
        "canonical_front=RightTurn/turn_0 "
        "LeftTurn/turn_0 pixels_and_png_bytes_identical=true"
    )

    if args.evidence:
        print(
            "evidence=combined 18-frame contact sheet is refreshed by "
            "extract_right_walk.py"
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
