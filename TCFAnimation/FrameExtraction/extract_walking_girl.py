"""Generate the twelve transparent walking-girl runtime frames.

The source sheet contains one approved row of twelve photographic poses above
an unrelated annotation/repeat area.  Extraction is deliberately calibrated:
the exact horizontal intervals and the exclusive source band ``y=[0,516)``
are fixed contracts, while foreground masks are produced from deterministic
OpenCV color seeds, mask-initialized GrabCut, and bounded shoe masks.
"""

from __future__ import annotations

import hashlib
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
from file_integrity import sha256_file  # noqa: E402


SOURCE_PATH = ROOT / "WalkingGirlFrames.png"
OUTPUT_DIR = ROOT / "Frames" / "GirlWalk"

SOURCE_SHA256 = (
    "524F6F3CA6F55B5062433766734BF304D0285E5994B54A9AD12915C4FBF4D881"
)
SOURCE_SIZE_BYTES = 2_120_344
SOURCE_FORMAT = "PNG"
SOURCE_MODE = "RGB"
SOURCE_SIZE = (1942, 809)
VALID_SOURCE_Y = (0, 516)
SOURCE_SUBJECT_BOTTOM_EXCLUSIVE = 497
X_INTERVALS = (
    (0, 158),
    (160, 317),
    (320, 480),
    (483, 642),
    (645, 805),
    (808, 967),
    (970, 1130),
    (1133, 1294),
    (1297, 1457),
    (1460, 1619),
    (1621, 1780),
    (1783, 1942),
)

FRAME_COUNT = 12
CANVAS_WIDTH = 512
CANVAS_HEIGHT = 864
OUTPUT_SCALE = 1.70
TARGET_TORSO_X = 256
TARGET_BASELINE_Y = 840

# Calibrated local source rectangles contain only the two shoes for each pose.
# They never cross an approved x interval or the valid y=[0,516) source band.
SHOE_RECTS = (
    ((25, 445, 104, 497), (79, 445, 151, 497)),
    ((13, 445, 80, 497), (70, 445, 146, 497)),
    ((2, 443, 68, 497), (76, 443, 160, 497)),
    ((0, 443, 69, 497), (91, 443, 159, 497)),
    ((0, 443, 69, 497), (92, 443, 160, 497)),
    ((6, 443, 80, 497), (70, 443, 153, 497)),
    ((0, 443, 68, 497), (85, 443, 160, 497)),
    ((0, 443, 74, 497), (97, 443, 161, 497)),
    ((0, 443, 74, 497), (89, 443, 160, 497)),
    ((0, 443, 74, 497), (85, 443, 159, 497)),
    ((0, 443, 74, 497), (84, 443, 159, 497)),
    ((6, 443, 82, 497), (73, 443, 159, 497)),
)

MINIMUM_ALPHA_PIXELS = 70_000
MAXIMUM_ALPHA_PIXELS = 180_000
MINIMUM_SOURCE_SHOE_PIXELS = 250
MINIMUM_ADJACENT_CHANGED_PIXELS = 12_000
MAXIMUM_TORSO_ALIGNMENT_DRIFT = 1
MAXIMUM_BASELINE_DRIFT = 0


@dataclass(frozen=True)
class Segmentation:
    mask: np.ndarray
    torso_x: float
    shoe_pixels: tuple[int, int]
    retained_yellow: int
    retained_white: int
    retained_skin: int
    retained_hair: int


@dataclass(frozen=True)
class GeneratedFrame:
    image: np.ndarray
    source_bounds: tuple[int, int, int, int]
    alpha_bounds: tuple[int, int, int, int]
    torso_x: int
    baseline_y: int
    shoe_pixels: tuple[int, int]
    retained_yellow: int
    retained_white: int
    retained_skin: int
    retained_hair: int


def source_sha256() -> str:
    return sha256_file(SOURCE_PATH, SOURCE_SIZE_BYTES)


def validate_source() -> tuple[np.ndarray, str]:
    hash_before = source_sha256()
    if hash_before != SOURCE_SHA256:
        raise RuntimeError(
            f"{SOURCE_PATH.name} SHA-256 is {hash_before}; "
            f"expected {SOURCE_SHA256}."
        )

    with Image.open(SOURCE_PATH) as image:
        source_format = image.format
        source_mode = image.mode
        source_size = image.size
    if (
        source_format != SOURCE_FORMAT
        or source_mode != SOURCE_MODE
        or source_size != SOURCE_SIZE
    ):
        raise RuntimeError(
            f"{SOURCE_PATH.name} is {source_format} {source_mode} "
            f"{source_size}; expected {SOURCE_FORMAT} {SOURCE_MODE} "
            f"{SOURCE_SIZE}."
        )

    source = cv2.imread(str(SOURCE_PATH), cv2.IMREAD_COLOR)
    expected_shape = (SOURCE_SIZE[1], SOURCE_SIZE[0], 3)
    if source is None or source.shape != expected_shape:
        raise RuntimeError(
            f"{SOURCE_PATH.name} OpenCV shape is "
            f"{None if source is None else source.shape}; "
            f"expected {expected_shape}."
        )

    print(
        f"source={SOURCE_PATH.name} format={source_format} mode={source_mode} "
        f"size={source_size} bytes={SOURCE_SIZE_BYTES} sha256={hash_before}"
    )
    return source, hash_before


def validate_source_geometry() -> None:
    if len(X_INTERVALS) != FRAME_COUNT or len(SHOE_RECTS) != FRAME_COUNT:
        raise RuntimeError("Walking-girl calibration records are inconsistent.")
    if VALID_SOURCE_Y != (0, 516):
        raise RuntimeError(
            f"Valid source band changed to {VALID_SOURCE_Y}; expected (0, 516)."
        )

    previous_x1 = -1
    for index, ((x0, x1), shoe_rects) in enumerate(
        zip(X_INTERVALS, SHOE_RECTS, strict=True)
    ):
        panel_width = x1 - x0
        if not (0 <= x0 < x1 <= SOURCE_SIZE[0]):
            raise RuntimeError(f"Frame {index}: invalid x interval {(x0, x1)}.")
        if x0 <= previous_x1:
            raise RuntimeError(
                f"Frame {index}: interval {(x0, x1)} overlaps its predecessor."
            )
        previous_x1 = x1
        for rect in shoe_rects:
            rx0, ry0, rx1, ry1 = rect
            if not (
                0 <= rx0 < rx1 <= panel_width
                and VALID_SOURCE_Y[0] <= ry0 < ry1 <= VALID_SOURCE_Y[1]
            ):
                raise RuntimeError(
                    f"Frame {index}: shoe rectangle {rect} exceeds "
                    f"{panel_width}x{VALID_SOURCE_Y[1]} approved crop."
                )

    print(
        f"x_intervals={X_INTERVALS} valid_source_y={VALID_SOURCE_Y} "
        f"shoe_rects={SHOE_RECTS}"
    )


def _components_touching(
    mask: np.ndarray,
    touch: np.ndarray,
    maximum_components: int,
) -> np.ndarray:
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        mask.astype(np.uint8),
        8,
    )
    ranked: list[tuple[int, int]] = []
    for label in range(1, count):
        component = labels == label
        overlap = int(np.count_nonzero(component & touch))
        if overlap:
            area = int(stats[label, cv2.CC_STAT_AREA])
            ranked.append((overlap * 10_000 + area, label))

    retained = np.zeros(mask.shape, dtype=bool)
    for _, label in sorted(ranked, reverse=True)[:maximum_components]:
        retained |= labels == label
    return retained


def _measure_torso_x(yellow: np.ndarray) -> float:
    centers = []
    for y in range(155, 350):
        xs = np.flatnonzero(yellow[y])
        if xs.size >= 18:
            centers.append(
                (
                    float(np.percentile(xs, 10))
                    + float(np.percentile(xs, 90))
                )
                / 2.0
            )
    if not centers:
        raise RuntimeError("Could not measure the yellow-garment torso axis.")
    return float(np.median(centers))


def _make_shoe_mask(
    value: np.ndarray,
    saturation: np.ndarray,
    green: np.ndarray,
    skin: np.ndarray,
    yellow: np.ndarray,
    rect: tuple[int, int, int, int],
) -> np.ndarray:
    height, width = value.shape
    x0, y0, x1, y1 = rect
    region = np.zeros((height, width), dtype=bool)
    region[y0:y1, x0:x1] = True

    dark_source = (value < 112) & region
    dark_core = cv2.morphologyEx(
        dark_source.astype(np.uint8),
        cv2.MORPH_OPEN,
        np.ones((5, 1), dtype=np.uint8),
        iterations=1,
    ) != 0
    dark_core = cv2.morphologyEx(
        dark_core.astype(np.uint8),
        cv2.MORPH_CLOSE,
        np.ones((3, 3), dtype=np.uint8),
        iterations=1,
    ) != 0
    dark_core = _components_touching(
        dark_core,
        region,
        maximum_components=1,
    )
    dark = dark_source & (
        cv2.dilate(
            dark_core.astype(np.uint8),
            np.ones((5, 5), dtype=np.uint8),
            iterations=1,
        )
        != 0
    )
    dark_count = int(np.count_nonzero(dark))
    if dark_count < MINIMUM_SOURCE_SHOE_PIXELS:
        raise RuntimeError(
            f"Shoe rectangle {rect} retained only {dark_count} dark pixels."
        )

    nearby = cv2.dilate(
        dark.astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 7)),
        iterations=1,
    ) != 0
    warm = (skin | yellow) & region & nearby
    specular = (
        (value >= 70)
        & (saturation <= 90)
        & region
        & nearby
    )
    shoe_mask = dark | warm | specular
    shoe_mask = cv2.morphologyEx(
        shoe_mask.astype(np.uint8),
        cv2.MORPH_CLOSE,
        np.ones((5, 5), dtype=np.uint8),
        iterations=1,
    ) != 0
    shoe_mask = _components_touching(
        shoe_mask,
        dark,
        maximum_components=2,
    )
    maximum_dark_y = int(np.where(dark)[0].max())
    shoe_mask[
        np.indices(shoe_mask.shape)[0] > min(y1 - 1, maximum_dark_y + 1)
    ] = False
    return shoe_mask


def segment_panel(panel: np.ndarray, index: int) -> Segmentation:
    height, width = panel.shape[:2]
    if height != VALID_SOURCE_Y[1] or width != (
        X_INTERVALS[index][1] - X_INTERVALS[index][0]
    ):
        raise RuntimeError(
            f"Frame {index}: panel shape {panel.shape} violates its crop."
        )

    hsv = cv2.cvtColor(panel, cv2.COLOR_BGR2HSV)
    hue, saturation, value = cv2.split(hsv)
    y_channel, cr, cb = cv2.split(
        cv2.cvtColor(panel, cv2.COLOR_BGR2YCrCb)
    )
    rows = np.indices((height, width))[0]
    red = panel[:, :, 2].astype(np.int16)
    green_channel = panel[:, :, 1].astype(np.int16)
    blue = panel[:, :, 0].astype(np.int16)

    green = (
        (hue >= 23)
        & (hue <= 110)
        & (saturation >= 40)
        & (
            (green_channel >= red * 0.90)
            | ((red - blue) < 48)
        )
    )
    lower_color_ok = (rows < 400) | (
        (hue <= 24)
        & (saturation >= 60)
        & ((red - blue) >= 50)
    )
    yellow_raw = (
        (hue >= 6)
        & (hue <= 32)
        & (saturation >= 28)
        & lower_color_ok
        & (value >= 45)
        & (red > blue * 1.15)
        & (rows < 475)
    )
    yellow_closed = cv2.morphologyEx(
        yellow_raw.astype(np.uint8),
        cv2.MORPH_CLOSE,
        np.ones((5, 5), dtype=np.uint8),
        iterations=1,
    ) != 0
    torso_zone = np.zeros((height, width), dtype=bool)
    torso_zone[125:375, max(8, width // 6) : min(width - 6, width * 5 // 6)] = (
        True
    )
    yellow_component = _components_touching(
        yellow_closed,
        torso_zone,
        maximum_components=1,
    )
    if np.count_nonzero(yellow_component) < 8_000:
        raise RuntimeError(f"Frame {index}: yellow garment seed is too small.")

    tracked_yellow = np.zeros((height, width), dtype=bool)
    tracked_yellow[:400] = yellow_component[:400]
    horizontal_step = np.ones((1, 13), dtype=np.uint8)
    for y in range(400, 475):
        prior_columns = np.any(
            tracked_yellow[max(0, y - 6) : y],
            axis=0,
        ).astype(np.uint8)[np.newaxis, :]
        allowed_columns = cv2.dilate(
            prior_columns,
            horizontal_step,
            iterations=1,
        )[0] != 0
        tracked_yellow[y] = yellow_closed[y] & allowed_columns
    yellow = yellow_raw & (
        cv2.dilate(
            tracked_yellow.astype(np.uint8),
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0
    )

    skin_all = (
        (cr >= 130)
        & (cr <= 190)
        & (cb >= 72)
        & (cb <= 135)
        & (y_channel >= 45)
        & (hue <= 29)
        & ~green
    )
    head_zone = np.zeros((height, width), dtype=bool)
    head_zone[25:145, :] = True
    head = _components_touching(
        cv2.morphologyEx(
            (skin_all & head_zone).astype(np.uint8),
            cv2.MORPH_CLOSE,
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0,
        head_zone,
        maximum_components=1,
    )
    if np.count_nonzero(head) < 500:
        raise RuntimeError(f"Frame {index}: face seed is too small.")

    near_subject = cv2.dilate(
        (tracked_yellow | head).astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (35, 35)),
        iterations=1,
    ) != 0
    skin = skin_all & (rows < 440) & near_subject
    white = (
        (saturation <= 45)
        & (value >= 125)
        & (rows < 405)
        & near_subject
    )
    core = yellow | skin | white

    head_y, head_x = np.where(head)
    hair_region = np.zeros((height, width), dtype=bool)
    hair_region[
        max(20, int(head_y.min()) - 24) : min(
            195, int(head_y.max()) + 85
        ),
        max(0, int(head_x.min()) - 28) : min(
            width, int(head_x.max()) + 29
        ),
    ] = True
    hair_raw = (value < 150) & hair_region & ~green
    hair = _components_touching(
        cv2.morphologyEx(
            hair_raw.astype(np.uint8),
            cv2.MORPH_CLOSE,
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0,
        cv2.dilate(
            head.astype(np.uint8),
            np.ones((15, 15), dtype=np.uint8),
            iterations=1,
        )
        != 0,
        maximum_components=2,
    )
    if np.count_nonzero(hair) < 700:
        raise RuntimeError(f"Frame {index}: hair seed is too small.")

    envelope = cv2.dilate(
        core.astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (15, 15)),
        iterations=1,
    ) != 0
    envelope |= (
        cv2.dilate(
            hair.astype(np.uint8),
            np.ones((9, 9), dtype=np.uint8),
            iterations=1,
        )
        != 0
    )
    envelope &= rows < 456

    grabcut_mask = np.full(
        (height, width),
        cv2.GC_BGD,
        dtype=np.uint8,
    )
    grabcut_mask[envelope] = cv2.GC_PR_FGD
    grabcut_mask[green] = cv2.GC_BGD
    grabcut_mask[:18, :] = cv2.GC_BGD
    grabcut_mask[456:, :] = cv2.GC_BGD
    definite_foreground = (
        cv2.erode(
            core.astype(np.uint8),
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0
    ) | (
        cv2.erode(
            hair.astype(np.uint8),
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0
    )
    grabcut_mask[definite_foreground] = cv2.GC_FGD

    background_model = np.zeros((1, 65), dtype=np.float64)
    foreground_model = np.zeros((1, 65), dtype=np.float64)
    cv2.grabCut(
        panel,
        grabcut_mask,
        None,
        background_model,
        foreground_model,
        8,
        cv2.GC_INIT_WITH_MASK,
    )
    body = (
        ((grabcut_mask == cv2.GC_FGD) | (grabcut_mask == cv2.GC_PR_FGD))
        & envelope
        & ~green
        & (rows < 456)
    )
    component_count, labels, stats, _ = cv2.connectedComponentsWithStats(
        body.astype(np.uint8),
        8,
    )
    if component_count <= 1:
        raise RuntimeError(f"Frame {index}: GrabCut found no body component.")
    body_label = max(
        range(1, component_count),
        key=lambda label: (
            int(
                np.count_nonzero(
                    (labels == label) & tracked_yellow & torso_zone
                )
            ),
            int(stats[label, cv2.CC_STAT_AREA]),
        ),
    )
    body = labels == body_label
    close_support = (
        cv2.dilate(
            (core | hair).astype(np.uint8),
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)),
            iterations=1,
        )
        != 0
    )
    lower_support = (
        cv2.dilate(
            yellow.astype(np.uint8),
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
            iterations=1,
        )
        != 0
    )
    body &= close_support
    body &= (rows < 395) | lower_support

    shoe_masks = tuple(
        _make_shoe_mask(
            value,
            saturation,
            green,
            skin_all,
            yellow,
            rect,
        )
        for rect in SHOE_RECTS[index]
    )
    mask = body | shoe_masks[0] | shoe_masks[1]
    mask = (
        cv2.morphologyEx(
            mask.astype(np.uint8),
            cv2.MORPH_CLOSE,
            np.ones((3, 3), dtype=np.uint8),
            iterations=1,
        )
        != 0
    )
    mask[SOURCE_SUBJECT_BOTTOM_EXCLUSIVE:, :] = False

    if not np.any(mask):
        raise RuntimeError(f"Frame {index}: segmentation is empty.")

    shoe_pixels = tuple(int(np.count_nonzero(mask & shoe)) for shoe in shoe_masks)
    if min(shoe_pixels) < MINIMUM_SOURCE_SHOE_PIXELS:
        raise RuntimeError(
            f"Frame {index}: shoe retention is too small: {shoe_pixels}."
        )

    return Segmentation(
        mask=mask,
        torso_x=_measure_torso_x(yellow),
        shoe_pixels=(shoe_pixels[0], shoe_pixels[1]),
        retained_yellow=int(np.count_nonzero(mask & yellow)),
        retained_white=int(np.count_nonzero(mask & white)),
        retained_skin=int(np.count_nonzero(mask & skin_all)),
        retained_hair=int(np.count_nonzero(mask & hair)),
    )


def _resize_premultiplied_rgba(
    rgba: np.ndarray,
    width: int,
    height: int,
) -> np.ndarray:
    alpha = rgba[:, :, 3].astype(np.float32) / 255.0
    premultiplied = rgba[:, :, :3].astype(np.float32) * alpha[:, :, None]
    resized_premultiplied = cv2.resize(
        premultiplied,
        (width, height),
        interpolation=cv2.INTER_LANCZOS4,
    )
    resized_alpha = np.clip(
        cv2.resize(
            alpha,
            (width, height),
            interpolation=cv2.INTER_LANCZOS4,
        ),
        0.0,
        1.0,
    )
    resized_rgb = np.zeros_like(resized_premultiplied)
    visible = resized_alpha > (1.0 / 255.0)
    resized_rgb[visible] = (
        resized_premultiplied[visible] / resized_alpha[visible, None]
    )

    resized = np.zeros((height, width, 4), dtype=np.uint8)
    resized[:, :, :3] = np.clip(
        np.rint(resized_rgb),
        0,
        255,
    ).astype(np.uint8)
    resized[:, :, 3] = np.clip(
        np.rint(resized_alpha * 255.0),
        0,
        255,
    ).astype(np.uint8)
    resized[:, :, :3][resized[:, :, 3] == 0] = 0
    return resized


def normalize_frame(
    panel: np.ndarray,
    segmentation: Segmentation,
) -> GeneratedFrame:
    source_y, source_x = np.where(segmentation.mask)
    source_bounds = (
        int(source_x.min()),
        int(source_y.min()),
        int(source_x.max()),
        int(source_y.max()),
    )
    source_left, source_top, source_right, source_bottom = source_bounds

    rgba = np.zeros((panel.shape[0], panel.shape[1], 4), dtype=np.uint8)
    rgba[:, :, :3] = cv2.cvtColor(panel, cv2.COLOR_BGR2RGB)
    rgba[:, :, 3] = segmentation.mask.astype(np.uint8) * 255
    rgba[:, :, :3][~segmentation.mask] = 0
    crop = rgba[
        source_top : source_bottom + 1,
        source_left : source_right + 1,
    ]
    resized_width = round(crop.shape[1] * OUTPUT_SCALE)
    resized_height = round(crop.shape[0] * OUTPUT_SCALE)
    resized = _resize_premultiplied_rgba(
        crop,
        resized_width,
        resized_height,
    )
    resized_y, resized_x = np.where(resized[:, :, 3] > 0)
    if resized_y.size == 0:
        raise RuntimeError("Resizing removed the entire foreground.")

    scaled_torso_x = round(
        (segmentation.torso_x - source_left) * OUTPUT_SCALE
    )
    output_x = TARGET_TORSO_X - scaled_torso_x
    output_y = TARGET_BASELINE_Y - int(resized_y.max())
    if (
        output_x < 0
        or output_y < 0
        or output_x + resized_width > CANVAS_WIDTH
        or output_y + resized_height > CANVAS_HEIGHT
    ):
        raise RuntimeError(
            f"Normalized frame {resized_width}x{resized_height} at "
            f"({output_x},{output_y}) exceeds "
            f"{CANVAS_WIDTH}x{CANVAS_HEIGHT}."
        )

    canvas = np.zeros((CANVAS_HEIGHT, CANVAS_WIDTH, 4), dtype=np.uint8)
    canvas[
        output_y : output_y + resized_height,
        output_x : output_x + resized_width,
    ] = resized
    canvas[:, :, :3][canvas[:, :, 3] == 0] = 0

    alpha_y, alpha_x = np.where(canvas[:, :, 3] > 0)
    alpha_bounds = (
        int(alpha_x.min()),
        int(alpha_y.min()),
        int(alpha_x.max()),
        int(alpha_y.max()),
    )
    return GeneratedFrame(
        image=canvas,
        source_bounds=source_bounds,
        alpha_bounds=alpha_bounds,
        torso_x=output_x + scaled_torso_x,
        baseline_y=int(alpha_y.max()),
        shoe_pixels=segmentation.shoe_pixels,
        retained_yellow=segmentation.retained_yellow,
        retained_white=segmentation.retained_white,
        retained_skin=segmentation.retained_skin,
        retained_hair=segmentation.retained_hair,
    )


def generate_frames(source: np.ndarray) -> list[GeneratedFrame]:
    crop_y0, crop_y1 = VALID_SOURCE_Y
    generated = []
    cv2.setRNGSeed(0)
    for index, (x0, x1) in enumerate(X_INTERVALS):
        panel = source[crop_y0:crop_y1, x0:x1].copy()
        segmentation = segment_panel(panel, index)
        generated.append(normalize_frame(panel, segmentation))
    return generated


def validate_generated(generated: list[GeneratedFrame]) -> None:
    if len(generated) != FRAME_COUNT:
        raise RuntimeError(
            f"Expected {FRAME_COUNT} frames, got {len(generated)}."
        )

    for index, item in enumerate(generated):
        frame = item.image
        if frame.shape != (CANVAS_HEIGHT, CANVAS_WIDTH, 4):
            raise RuntimeError(
                f"Frame {index}: unexpected shape {frame.shape}."
            )
        alpha = frame[:, :, 3]
        alpha_pixels = int(np.count_nonzero(alpha))
        if not (MINIMUM_ALPHA_PIXELS <= alpha_pixels <= MAXIMUM_ALPHA_PIXELS):
            raise RuntimeError(
                f"Frame {index}: alpha pixel count {alpha_pixels} is outside "
                f"[{MINIMUM_ALPHA_PIXELS},{MAXIMUM_ALPHA_PIXELS}]."
            )
        if np.all(alpha == 0) or np.all(alpha == 255):
            raise RuntimeError(
                f"Frame {index}: transparency is trivial or absent."
            )
        if np.any(frame[:, :, :3][alpha == 0] != 0):
            raise RuntimeError(
                f"Frame {index}: RGB is nonzero where alpha is zero."
            )
        if abs(item.torso_x - TARGET_TORSO_X) > MAXIMUM_TORSO_ALIGNMENT_DRIFT:
            raise RuntimeError(
                f"Frame {index}: torso x is {item.torso_x}, "
                f"expected {TARGET_TORSO_X}."
            )
        if (
            abs(item.baseline_y - TARGET_BASELINE_Y)
            > MAXIMUM_BASELINE_DRIFT
        ):
            raise RuntimeError(
                f"Frame {index}: baseline is {item.baseline_y}, "
                f"expected {TARGET_BASELINE_Y}."
            )

        visible_rgb = frame[:, :, :3][alpha > 0]
        visible_hsv = cv2.cvtColor(
            visible_rgb.reshape(-1, 1, 3)[:, :, ::-1],
            cv2.COLOR_BGR2HSV,
        ).reshape(-1, 3)
        opaque_green = (
            (visible_hsv[:, 0] >= 30)
            & (visible_hsv[:, 0] <= 110)
            & (visible_hsv[:, 1] >= 70)
            & (visible_hsv[:, 2] >= 30)
        )
        green_mask = np.zeros(alpha.shape, dtype=np.uint8)
        green_mask[alpha > 0] = opaque_green.astype(np.uint8)
        green_mask[760:, :] = 0
        green_count = int(np.count_nonzero(green_mask))
        green_components, _, green_stats, _ = cv2.connectedComponentsWithStats(
            green_mask,
            8,
        )
        largest_green_component = max(
            (
                int(green_stats[label, cv2.CC_STAT_AREA])
                for label in range(1, green_components)
            ),
            default=0,
        )
        if green_count > 1_000 or largest_green_component > 256:
            raise RuntimeError(
                f"Frame {index}: retained {green_count} saturated green "
                f"pixels with largest component {largest_green_component}."
            )

        print(
            f"frame={index:02d} source_crop="
            f"({X_INTERVALS[index][0]},0,{X_INTERVALS[index][1]},516) "
            f"source_bounds={item.source_bounds} "
            f"alpha_pixels={alpha_pixels} alpha_bounds={item.alpha_bounds} "
            f"torso_x={item.torso_x} baseline_y={item.baseline_y} "
            f"shoe_pixels={item.shoe_pixels} "
            f"retained=(yellow:{item.retained_yellow},"
            f"white:{item.retained_white},skin:{item.retained_skin},"
            f"hair:{item.retained_hair})"
        )

    adjacent_changed_pixels = []
    for index in range(FRAME_COUNT - 1):
        changed = int(
            np.count_nonzero(
                np.any(
                    generated[index].image
                    != generated[index + 1].image,
                    axis=2,
                )
            )
        )
        adjacent_changed_pixels.append(changed)
        if changed < MINIMUM_ADJACENT_CHANGED_PIXELS:
            raise RuntimeError(
                f"Frames {index:02d}/{index + 1:02d} differ in only "
                f"{changed} pixels."
            )
    print(f"adjacent_changed_pixels={adjacent_changed_pixels}")


def validate_output_directory() -> None:
    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    expected_names = {f"girl_{index:02d}.png" for index in range(FRAME_COUNT)}
    unexpected = [
        path
        for path in OUTPUT_DIR.glob("*.png")
        if path.name not in expected_names
    ]
    if unexpected:
        raise RuntimeError(
            "Unexpected walking-girl PNGs must be removed explicitly: "
            + ", ".join(str(path.relative_to(ROOT)) for path in unexpected)
        )


def write_frames(generated: list[GeneratedFrame]) -> None:
    for index, item in enumerate(generated):
        output_path = OUTPUT_DIR / f"girl_{index:02d}.png"
        Image.fromarray(item.image, mode="RGBA").save(
            output_path,
            format="PNG",
            compress_level=9,
            optimize=False,
        )


def sha256_bytes(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def validate_written_outputs(
    generated: list[GeneratedFrame],
) -> tuple[tuple[int, int, int, int], ...]:
    expected_names = tuple(
        f"girl_{index:02d}.png" for index in range(FRAME_COUNT)
    )
    actual_names = tuple(
        sorted(path.name for path in OUTPUT_DIR.glob("*.png"))
    )
    if actual_names != expected_names:
        raise RuntimeError(
            f"Walking-girl output set is {actual_names}; "
            f"expected {expected_names}."
        )

    hashes = []
    bounds = []
    for index, name in enumerate(expected_names):
        path = OUTPUT_DIR / name
        with Image.open(path) as image:
            if (
                image.format != "PNG"
                or image.mode != "RGBA"
                or image.size != (CANVAS_WIDTH, CANVAS_HEIGHT)
            ):
                raise RuntimeError(
                    f"{name} is {image.format} {image.mode} {image.size}; "
                    "expected PNG RGBA (512, 864)."
                )
            pixels = np.asarray(image, dtype=np.uint8)
        if np.any(pixels[:, :, :3][pixels[:, :, 3] == 0] != 0):
            raise RuntimeError(f"{name}: transparent RGB is not zero.")
        alpha_y, alpha_x = np.where(pixels[:, :, 3] > 0)
        measured_bounds = (
            int(alpha_x.min()),
            int(alpha_y.min()),
            int(alpha_x.max()),
            int(alpha_y.max()),
        )
        if measured_bounds != generated[index].alpha_bounds:
            raise RuntimeError(
                f"{name}: written bounds {measured_bounds} differ from "
                f"generated bounds {generated[index].alpha_bounds}."
            )
        digest = sha256_bytes(path)
        hashes.append(digest)
        bounds.append(measured_bounds)
        print(
            f"output={path.relative_to(ROOT)} bytes={path.stat().st_size} "
            f"sha256={digest} alpha_bounds={measured_bounds}"
        )

    if len(set(hashes)) != FRAME_COUNT:
        raise RuntimeError("Walking-girl output hashes are not all distinct.")

    union_bounds = (
        min(bound[0] for bound in bounds),
        min(bound[1] for bound in bounds),
        max(bound[2] for bound in bounds),
        max(bound[3] for bound in bounds),
    )
    print(
        "alpha_union_bounds="
        f"left:{union_bounds[0]} top:{union_bounds[1]} "
        f"right:{union_bounds[2]} bottom:{union_bounds[3]}"
    )
    print(
        f"alignment=torso_x:{TARGET_TORSO_X} "
        f"torso_drift:{max(abs(item.torso_x - TARGET_TORSO_X) for item in generated)} "
        f"baseline_y:{TARGET_BASELINE_Y} "
        f"baseline_drift:{max(abs(item.baseline_y - TARGET_BASELINE_Y) for item in generated)}"
    )
    return tuple(bounds)


def main() -> int:
    source, hash_before = validate_source()
    validate_source_geometry()
    generated = generate_frames(source)
    validate_generated(generated)
    validate_output_directory()
    write_frames(generated)
    validate_written_outputs(generated)

    hash_after = source_sha256()
    if hash_after != hash_before:
        raise RuntimeError(
            f"{SOURCE_PATH.name} changed during extraction: "
            f"{hash_before} -> {hash_after}."
        )
    print(
        f"source_sha256_before={hash_before} "
        f"source_sha256_after={hash_after} source_unchanged=true"
    )
    print(
        f"generated={FRAME_COUNT} canvas={CANVAS_WIDTH}x{CANVAS_HEIGHT} "
        "mode=RGBA transparent=true transparent_rgb_zero=true "
        f"scale={OUTPUT_SCALE:.2f} rng_seed=0"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
