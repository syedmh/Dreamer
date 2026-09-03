"""Generate opaque full-body left- and right-turn panels.

Each frame keeps the source character, shoes, studio floor, shadow, and
reflection together. Only source labels/dividers are excluded; no character
segmentation, color recovery, transparency extraction, or artwork scaling is
performed.
"""

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


EVIDENCE_DIR = (
    ROOT / ".ai-org" / "missions" / "2026-09-02-add-right-turn-cecc239c"
)

SOURCE_SHAPE = (1448, 1086, 3)
CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
TARGET_TORSO_CENTER_X = 256
TARGET_FLOOR_BASELINE_Y = CANVAS_HEIGHT - 1

# Fade only the outer studio-background edges. Every character starts well
# inside these ramps, so they do not touch hair, clothing, shoes, the floor
# directly beneath the character, or reflection.
SIDE_FEATHER_PIXELS = 28
TOP_FEATHER_PIXELS = 16


@dataclass(frozen=True)
class SheetConfig:
    name: str
    source_path: Path
    output_dir: Path
    panel_crops: tuple[tuple[int, int, int, int], ...]
    label_rect: tuple[int, int, int, int]
    panel_offsets: tuple[tuple[int, int], ...]
    torso_centers: tuple[int, ...]
    normalize_container: bool = False


@dataclass(frozen=True)
class GeneratedFrame:
    panel: np.ndarray
    frame: np.ndarray
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


def edge_feather(panel: np.ndarray) -> np.ndarray:
    """Fade only outer left/right/top panel edges into viewport black."""

    height, width = panel.shape[:2]
    weights = np.ones((height, width), dtype=np.float32)

    side_ramp = np.linspace(
        0.0,
        1.0,
        SIDE_FEATHER_PIXELS,
        endpoint=True,
        dtype=np.float32,
    )
    weights[:, :SIDE_FEATHER_PIXELS] *= side_ramp
    weights[:, -SIDE_FEATHER_PIXELS:] *= side_ramp[::-1]

    top_ramp = np.linspace(
        0.0,
        1.0,
        TOP_FEATHER_PIXELS,
        endpoint=True,
        dtype=np.float32,
    )
    weights[:TOP_FEATHER_PIXELS, :] *= top_ramp[:, np.newaxis]

    return np.rint(panel.astype(np.float32) * weights[:, :, np.newaxis]).astype(
        np.uint8
    )


def prepare_panel(
    source: np.ndarray,
    crop: tuple[int, int, int, int],
    label_rect: tuple[int, int, int, int],
) -> np.ndarray:
    """Crop one complete source cell, remove its label, and soften outer edges."""

    x0, y0, x1, y1 = crop
    panel = source[y0:y1, x0:x1].copy()

    label_x0, label_y0, label_x1, label_y1 = label_rect
    panel[label_y0:label_y1, label_x0:label_x1] = 0
    return edge_feather(panel)


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
    for crop, offset in zip(
        config.panel_crops,
        config.panel_offsets,
        strict=True,
    ):
        panel = prepare_panel(source, crop, config.label_rect)
        frame = place_panel(panel, offset)
        generated.append(GeneratedFrame(panel, frame, *offset))
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

        # The complete body/floor interior must be byte-identical to source.
        interior_actual = panel[
            TOP_FEATHER_PIXELS:,
            SIDE_FEATHER_PIXELS:-SIDE_FEATHER_PIXELS,
        ]
        interior_expected = source_panel[
            TOP_FEATHER_PIXELS:,
            SIDE_FEATHER_PIXELS:-SIDE_FEATHER_PIXELS,
        ].copy()
        interior_label_x0 = max(0, label_x0 - SIDE_FEATHER_PIXELS)
        interior_label_y0 = max(0, label_y0 - TOP_FEATHER_PIXELS)
        interior_label_x1 = label_x1 - SIDE_FEATHER_PIXELS
        interior_label_y1 = label_y1 - TOP_FEATHER_PIXELS
        interior_expected[
            interior_label_y0:interior_label_y1,
            interior_label_x0:interior_label_x1,
        ] = 0
        if not np.array_equal(interior_actual, interior_expected):
            raise RuntimeError(
                f"{config.name} panel {index} altered source pixels outside "
                "label/edge regions."
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

        occupied = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH), dtype=bool)
        occupied[
            item.offset_y : item.offset_y + panel.shape[0],
            item.offset_x : item.offset_x + panel.shape[1],
        ] = True
        if np.any(frame[:, :, :3][~occupied]):
            raise RuntimeError(
                f"{config.name} frame {index} exposed canvas is not black."
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
    print(
        "edge_feather="
        f"left/right:{SIDE_FEATHER_PIXELS}px top:{TOP_FEATHER_PIXELS}px"
    )
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


def make_contact_sheet(
    generated_by_sheet: list[tuple[SheetConfig, list[GeneratedFrame]]],
) -> np.ndarray:
    """Render all six frames in direction-specific rows on a 4K canvas."""

    sheet = np.zeros((2160, 3840, 3), dtype=np.uint8)
    cell_width = 1280
    cell_height = 1080
    evidence_scale = 1.25

    for row, (config, generated) in enumerate(generated_by_sheet):
        for column, item in enumerate(generated):
            scaled = cv2.resize(
                item.frame[:, :, :3],
                (
                    round(item.frame.shape[1] * evidence_scale),
                    round(item.frame.shape[0] * evidence_scale),
                ),
                interpolation=cv2.INTER_NEAREST,
            )
            x = column * cell_width + (cell_width - scaled.shape[1]) // 2
            y = row * cell_height + (cell_height - scaled.shape[0]) // 2
            sheet[y : y + scaled.shape[0], x : x + scaled.shape[1]] = scaled

            cv2.putText(
                sheet,
                f"{config.name} {column * 45} deg",
                (column * cell_width + 36, row * cell_height + 70),
                cv2.FONT_HERSHEY_SIMPLEX,
                1.25,
                (220, 220, 220),
                2,
                cv2.LINE_AA,
            )

    return sheet


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

    generated_by_sheet: list[tuple[SheetConfig, list[GeneratedFrame]]] = []
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

        generated_by_sheet.append((config, generated))

    if args.evidence:
        EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
        contact_path = EVIDENCE_DIR / "directional-turn-contact-sheet-4k.png"
        write_png(contact_path, make_contact_sheet(generated_by_sheet))
        print(f"evidence={contact_path.relative_to(ROOT)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
