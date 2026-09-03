"""Generate opaque full-body turn frames on uniform pure black."""

from __future__ import annotations

import argparse
import sys
from dataclasses import dataclass
from io import BytesIO
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


SOURCE_SHAPE = (1448, 1086, 3)
CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
TARGET_TORSO_CENTER_X = 256
TARGET_FLOOR_BASELINE_Y = CANVAS_HEIGHT - 1

@dataclass(frozen=True)
class SheetConfig:
    name: str
    source_path: Path
    output_dir: Path
    panel_crops: tuple[tuple[int, int, int, int], ...]
    label_rect: tuple[int, int, int, int]
    panel_offsets: tuple[tuple[int, int], ...]
    torso_centers: tuple[int, ...]
    cutouts: tuple[CutoutConfig, ...]
    normalize_container: bool = False


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
            ((145, 620, 270, 860), (250, 620, 385, 860)),
            620,
        ),
        CutoutConfig(
            "LeftTurn/turn_1",
            (115, 160, 390, 860),
            (150, 270, 350, 575),
            ((155, 620, 270, 860), (250, 620, 375, 860)),
            620,
        ),
        CutoutConfig(
            "LeftTurn/turn_2",
            (145, 160, 345, 860),
            (170, 270, 330, 575),
            ((165, 620, 275, 860), (220, 620, 345, 860)),
            620,
        ),
    ),
)

# RTurning uses different native cell widths. Its antialiased vertical
# dividers occupy x=356..358 and x=724..727, and the horizontal divider starts
# at y=725. The source label glyphs occupy x=19..83 and y=24..39 inside the
# clean cells. Native whole-panel translations align calibrated torso axes
# (183, 188, 177) at x=256 and the retained floor at y=863.
RIGHT_CONFIG = SheetConfig(
    name="RightTurn",
    source_path=ROOT / "RTurning.png",
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
            ((150, 620, 270, 860), (250, 620, 385, 860)),
            620,
        ),
        CutoutConfig(
            "RightTurn/turn_1",
            (120, 155, 390, 860),
            (155, 265, 360, 575),
            ((155, 620, 275, 860), (250, 620, 375, 860)),
            620,
        ),
        CutoutConfig(
            "RightTurn/turn_2",
            (145, 155, 350, 860),
            (175, 265, 335, 575),
            ((165, 620, 275, 860), (220, 620, 350, 860)),
            620,
        ),
    ),
    normalize_container=True,
)

SHEET_CONFIGS = (LEFT_CONFIG, RIGHT_CONFIG)


def normalize_png_container(path: Path) -> None:
    """Replace a mislabeled image container with PNG without changing pixels."""

    with Image.open(path) as source_image:
        source_format = source_image.format
        decoded_rgb = np.asarray(source_image.convert("RGB"), dtype=np.uint8).copy()

    if source_format == "PNG":
        print(f"container={path.name} format=PNG already_normalized=true")
        return

    encoded = BytesIO()
    Image.fromarray(decoded_rgb, mode="RGB").save(
        encoded,
        format="PNG",
        compress_level=9,
    )
    path.write_bytes(encoded.getvalue())

    with Image.open(path) as normalized_image:
        normalized_rgb = np.asarray(
            normalized_image.convert("RGB"),
            dtype=np.uint8,
        )
        normalized_format = normalized_image.format

    if normalized_format != "PNG":
        raise RuntimeError(f"Failed to normalize {path.name} to a PNG container.")
    if not np.array_equal(decoded_rgb, normalized_rgb):
        raise RuntimeError(f"Normalizing {path.name} changed decoded artwork.")

    print(
        f"container={path.name} format={source_format}->PNG "
        "decoded_pixels_unchanged=true"
    )


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
    for crop, offset, cutout_config in zip(
        config.panel_crops,
        config.panel_offsets,
        config.cutouts,
        strict=True,
    ):
        panel = prepare_panel(source, crop, config.label_rect)
        placed = place_panel(panel, offset)
        cutout = isolate_character(placed, cutout_config)
        generated.append(
            GeneratedFrame(panel, cutout.frame, cutout, *offset)
        )
    return generated


def validate_frames(
    source: np.ndarray,
    generated: list[GeneratedFrame],
    config: SheetConfig,
) -> None:
    if len(generated) != 3:
        raise RuntimeError(
            f"{config.name}: expected exactly 3 frames, got {len(generated)}."
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

    for config in SHEET_CONFIGS:
        if config.normalize_container:
            normalize_png_container(config.source_path)

        source = cv2.imread(str(config.source_path), cv2.IMREAD_COLOR)
        if source is None:
            raise FileNotFoundError(config.source_path)
        if source.shape != SOURCE_SHAPE:
            raise RuntimeError(
                f"{config.name} source has shape {source.shape}; "
                f"expected {SOURCE_SHAPE}."
            )

        generated = generate_frames(source, config)
        validate_frames(source, generated, config)
        validate_output_directory(config)

        for index, item in enumerate(generated):
            output_path = config.output_dir / f"turn_{index}.png"
            write_png(output_path, item.frame)
            print(
                f"wrote={output_path.relative_to(ROOT)} "
                f"shape={item.frame.shape}"
            )

    if args.evidence:
        print(
            "evidence=combined 18-frame contact sheet is refreshed by "
            "extract_right_walk.py"
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
