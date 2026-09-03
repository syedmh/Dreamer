"""Deterministic character extraction for the project's black-studio frames."""

from __future__ import annotations

from dataclasses import dataclass

import cv2  # type: ignore
import numpy as np  # type: ignore


Rect = tuple[int, int, int, int]
SHOE_LIFT_SOURCE_VALUE_MAX = 21
SHOE_LIFT_TARGET_VALUE = 22
SHOE_VISIBLE_VALUE_MIN = 20
MINIMUM_VISIBLE_SHOE_RETENTION = 0.98


@dataclass(frozen=True)
class CutoutConfig:
    """Per-pose geometry used to keep dark hair and shoes attached."""

    name: str
    person_rect: Rect
    torso_rect: Rect
    shoe_rects: tuple[Rect, ...]
    floor_start_y: int
    minimum_shoe_pixels: int = 90


@dataclass(frozen=True)
class CutoutResult:
    frame: np.ndarray
    matte: np.ndarray
    black_ratio: float
    foreground_pixels: int
    shoe_pixels: tuple[int, ...]
    shoe_retention: tuple[float, ...]
    shoe_dark_retention: tuple[float, ...]
    shoe_extent_retention: tuple[float, ...]
    shoe_visible_retention: tuple[float, ...]
    shoe_visible_extent_retention: tuple[float, ...]


def _rect_mask(shape: tuple[int, int], rect: Rect) -> np.ndarray:
    height, width = shape
    x0, y0, x1, y1 = rect
    if not (0 <= x0 < x1 <= width and 0 <= y0 < y1 <= height):
        raise RuntimeError(f"Invalid cutout rectangle {rect} for {width}x{height}.")
    mask = np.zeros(shape, dtype=np.uint8)
    mask[y0:y1, x0:x1] = 1
    return mask


def _fill_holes(mask: np.ndarray) -> np.ndarray:
    inverse = np.where(mask != 0, 0, 255).astype(np.uint8)
    flood = inverse.copy()
    flood_mask = np.zeros(
        (inverse.shape[0] + 2, inverse.shape[1] + 2),
        dtype=np.uint8,
    )
    cv2.floodFill(flood, flood_mask, (0, 0), 0)
    holes = flood != 0
    return np.where((mask != 0) | holes, 1, 0).astype(np.uint8)


def _lift_dark_shoe_pixels(
    output_rgba: np.ndarray,
    source_bgr: np.ndarray,
    matte: np.ndarray,
    shoe_union: np.ndarray,
) -> np.ndarray:
    """Lift retained near-black shoe RGB to a subtle visible charcoal floor."""

    source_value = np.max(source_bgr, axis=2)
    lift_mask = (
        (matte != 0)
        & shoe_union
        & (source_value > 0)
        & (source_value <= SHOE_LIFT_SOURCE_VALUE_MAX)
    )
    if not np.any(lift_mask):
        return lift_mask

    delta = SHOE_LIFT_TARGET_VALUE - source_value[lift_mask].astype(np.int16)
    lifted = (
        source_bgr[lift_mask].astype(np.int16)
        + delta[:, np.newaxis]
    )
    output_rgba[:, :, :3][lift_mask] = np.clip(lifted, 0, 255).astype(
        np.uint8
    )
    return lift_mask


def _retain_person_component(
    mask: np.ndarray,
    torso_mask: np.ndarray,
    shoe_masks: tuple[np.ndarray, ...],
) -> np.ndarray:
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    if count <= 1:
        raise RuntimeError("Foreground extraction produced no components.")

    candidates: list[tuple[int, int]] = []
    for label in range(1, count):
        component = labels == label
        torso_overlap = int(np.count_nonzero(component & (torso_mask != 0)))
        shoe_overlap = sum(
            int(np.count_nonzero(component & (shoe_mask != 0)))
            for shoe_mask in shoe_masks
        )
        if torso_overlap:
            candidates.append((torso_overlap * 100 + shoe_overlap, label))

    if not candidates:
        raise RuntimeError("No foreground component intersects the torso seed.")

    person_label = max(candidates)[1]
    torso_component = labels == person_label
    person = torso_component.copy()

    # A dark shoe can be separated from a trouser cuff by a narrow run of
    # near-black antialiasing. Admit only components inside calibrated shoe
    # boxes, then bridge that invisible gap without adding visible artwork.
    for label in range(1, count):
        if label == person_label:
            continue
        component = labels == label
        if any(
            np.any(component & (shoe_mask != 0))
            for shoe_mask in shoe_masks
        ):
            person |= component

    connected_count, connected_labels, _, _ = cv2.connectedComponentsWithStats(
        person.astype(np.uint8),
        8,
    )
    anchor = torso_component.astype(np.uint8)
    pending = [
        connected_labels == label
        for label in range(1, connected_count)
        if not np.any((connected_labels == label) & torso_component)
    ]
    while pending:
        anchor_contours, _ = cv2.findContours(
            anchor,
            cv2.RETR_EXTERNAL,
            cv2.CHAIN_APPROX_NONE,
        )
        anchor_points = np.concatenate(anchor_contours)[:, 0, :]
        nearest_component_index = -1
        nearest_component_pair: tuple[np.ndarray, np.ndarray] | None = None
        nearest_component_distance = float("inf")
        for component_index, component in enumerate(pending):
            contours, _ = cv2.findContours(
                component.astype(np.uint8),
                cv2.RETR_EXTERNAL,
                cv2.CHAIN_APPROX_NONE,
            )
            component_points = np.concatenate(contours)[:, 0, :]
            for point in component_points:
                deltas = anchor_points - point
                distances = np.einsum("ij,ij->i", deltas, deltas)
                nearest_index = int(np.argmin(distances))
                distance = float(distances[nearest_index])
                if distance < nearest_component_distance:
                    nearest_component_distance = distance
                    nearest_component_index = component_index
                    nearest_component_pair = (
                        point,
                        anchor_points[nearest_index],
                    )
        if (
            nearest_component_pair is None
            or nearest_component_distance > 45 * 45
        ):
            break
        component = pending.pop(nearest_component_index)
        cv2.line(
            person,
            tuple(map(int, nearest_component_pair[0])),
            tuple(map(int, nearest_component_pair[1])),
            1,
            1,
            cv2.LINE_8,
        )
        anchor |= component.astype(np.uint8)
        cv2.line(
            anchor,
            tuple(map(int, nearest_component_pair[0])),
            tuple(map(int, nearest_component_pair[1])),
            1,
            1,
            cv2.LINE_8,
        )

    person = cv2.morphologyEx(
        person.astype(np.uint8),
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
    )
    person = _fill_holes(person)

    final_count, final_labels, final_stats, _ = cv2.connectedComponentsWithStats(
        person,
        8,
    )
    if final_count <= 1:
        raise RuntimeError("Foreground extraction lost the person component.")
    final_label = 1 + int(np.argmax(final_stats[1:, cv2.CC_STAT_AREA]))
    return (final_labels == final_label).astype(np.uint8)


def isolate_character(
    canvas_rgba: np.ndarray,
    config: CutoutConfig,
) -> CutoutResult:
    """Isolate one person and composite the retained source RGB onto black."""

    if canvas_rgba.ndim != 3 or canvas_rgba.shape[2] != 4:
        raise RuntimeError(
            f"{config.name}: expected RGBA input, got {canvas_rgba.shape}."
        )
    if canvas_rgba.dtype != np.uint8:
        raise RuntimeError(f"{config.name}: expected uint8 input.")

    bgr = canvas_rgba[:, :, :3]
    height, width = bgr.shape[:2]
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    saturation = hsv[:, :, 1]
    value = hsv[:, :, 2]

    person_region = _rect_mask((height, width), config.person_rect)
    torso_region = _rect_mask((height, width), config.torso_rect)
    shoe_regions = tuple(
        _rect_mask((height, width), rect) for rect in config.shoe_rects
    )

    # Bright cloth/skin and saturated green are unambiguous foreground. Keep
    # floor highlights out of the seed except inside calibrated shoe boxes.
    high_confidence = (
        ((value >= 72) | ((saturation >= 62) & (value >= 34)))
        & (person_region != 0)
    )
    shoe_union = np.zeros((height, width), dtype=bool)
    for shoe_region in shoe_regions:
        shoe_union |= shoe_region != 0
    high_confidence &= (
        (np.indices((height, width))[0] < config.floor_start_y) | shoe_union
    )

    torso_seed = high_confidence & (torso_region != 0)
    if np.count_nonzero(torso_seed) < 500:
        raise RuntimeError(
            f"{config.name}: insufficient high-confidence torso seed."
        )

    seed_kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    definite_foreground = cv2.erode(
        high_confidence.astype(np.uint8),
        seed_kernel,
        iterations=1,
    ) != 0

    # Dilated trusted color reaches dark hair, beard edges, cuffs, and shoes,
    # while the calibrated person rectangle excludes neighboring panels.
    probable_foreground = cv2.dilate(
        high_confidence.astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (31, 31)),
        iterations=1,
    ) != 0
    probable_foreground &= person_region != 0
    probable_foreground &= value >= 4

    # Dark shoes need a wider probable region than the generic color seed.
    for shoe_region in shoe_regions:
        probable_foreground |= (
            (shoe_region != 0)
            & (value >= 5)
        )

    grabcut_mask = np.full((height, width), cv2.GC_BGD, dtype=np.uint8)
    grabcut_mask[person_region != 0] = cv2.GC_PR_BGD
    grabcut_mask[probable_foreground] = cv2.GC_PR_FGD
    grabcut_mask[definite_foreground] = cv2.GC_FGD

    background_model = np.zeros((1, 65), dtype=np.float64)
    foreground_model = np.zeros((1, 65), dtype=np.float64)
    cv2.grabCut(
        bgr,
        grabcut_mask,
        None,
        background_model,
        foreground_model,
        5,
        cv2.GC_INIT_WITH_MASK,
    )

    grabcut_foreground = (
        (grabcut_mask == cv2.GC_FGD)
        | (grabcut_mask == cv2.GC_PR_FGD)
    ).astype(np.uint8)

    # At and below the calibrated floor line, retain only pixels close to
    # trusted shoe highlights. This removes floor/shadow regions that touch a
    # sole while hole filling below restores the dark leather interiors.
    # The retained studio floor is characteristically cool blue/purple,
    # unlike the low-saturation white trousers and warm/neutral leather.
    neutral_or_warm = (
        (saturation <= 35)
        | (bgr[:, :, 0].astype(np.int16) <= bgr[:, :, 2].astype(np.int16) + 4)
    )
    lower_shoe_support = np.zeros((height, width), dtype=bool)
    shoe_support_kernel = cv2.getStructuringElement(
        cv2.MORPH_ELLIPSE,
        (35, 35),
    )
    horizontal_shoe_kernel = cv2.getStructuringElement(
        cv2.MORPH_ELLIPSE,
        (35, 1),
    )
    row_indices = np.indices((height, width))[0]
    for shoe_region in shoe_regions:
        trusted_shoe_pixels = (
            high_confidence
            & neutral_or_warm
            & (shoe_region != 0)
        )
        trusted_shoe_highlights = (
            (grabcut_foreground != 0)
            & trusted_shoe_pixels
        )
        shoe_support = (
            cv2.dilate(
                trusted_shoe_pixels.astype(np.uint8),
                shoe_support_kernel,
                iterations=1,
            )
            != 0
        ) & (shoe_region != 0)
        horizontal_trusted = cv2.dilate(
            trusted_shoe_highlights.astype(np.uint8),
            horizontal_shoe_kernel,
            iterations=1,
        ) != 0
        trusted_rows = np.where(horizontal_trusted, row_indices, -1)
        bottom_by_column = np.max(trusted_rows, axis=0)
        shoe_support &= (
            (bottom_by_column[np.newaxis, :] >= 0)
            & (
                row_indices
                <= bottom_by_column[np.newaxis, :] + 2
            )
        )
        lower_shoe_support |= shoe_support
    lower_allowed = (
        lower_shoe_support
        & shoe_union
        & (value >= 5)
    )
    shoe_close_kernel = cv2.getStructuringElement(
        cv2.MORPH_ELLIPSE,
        (25, 25),
    )
    for shoe_region in shoe_regions:
        shoe_allowed = lower_allowed & (shoe_region != 0)
        closed_shoe = cv2.morphologyEx(
            shoe_allowed.astype(np.uint8),
            cv2.MORPH_CLOSE,
            shoe_close_kernel,
        )
        closed_shoe = _fill_holes(closed_shoe) != 0
        lower_allowed |= (
            closed_shoe
            & lower_shoe_support
            & (shoe_region != 0)
            & (value >= 1)
        )
    grabcut_foreground[config.floor_start_y :] &= lower_allowed[
        config.floor_start_y :
    ].astype(np.uint8)
    grabcut_foreground[config.floor_start_y :] |= lower_allowed[
        config.floor_start_y :
    ].astype(np.uint8)
    grabcut_foreground = cv2.morphologyEx(
        grabcut_foreground,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5)),
    )

    matte = _retain_person_component(
        grabcut_foreground,
        torso_region,
        shoe_regions,
    )

    output = np.zeros_like(canvas_rgba)
    output[:, :, 3] = 255
    output[:, :, :3][matte != 0] = bgr[matte != 0]
    # The matte deliberately retains source-black bridge pixels only for
    # connectivity, so leave value 0 untouched. Within calibrated shoe boxes,
    # raise only retained nonzero near-black leather by an equal per-channel
    # delta; this preserves hue while making complete dark shoes readable
    # against the exact-black background.
    shoe_lift_mask = _lift_dark_shoe_pixels(
        output,
        bgr,
        matte,
        shoe_union,
    )

    shoe_reference_masks = tuple(
        lower_allowed & (shoe_region != 0)
        for shoe_region in shoe_regions
    )
    result = validate_cutout(
        canvas_rgba,
        output,
        matte,
        config,
        shoe_reference_masks,
        shoe_lift_mask,
    )
    return result


def validate_cutout(
    source_rgba: np.ndarray,
    output_rgba: np.ndarray,
    matte: np.ndarray,
    config: CutoutConfig,
    shoe_reference_masks: tuple[np.ndarray, ...],
    shoe_lift_mask: np.ndarray,
) -> CutoutResult:
    """Validate exact black background and the calibrated character anatomy."""

    if output_rgba.shape != source_rgba.shape:
        raise RuntimeError(
            f"{config.name}: output shape changed "
            f"{source_rgba.shape} -> {output_rgba.shape}."
        )
    if not np.all(output_rgba[:, :, 3] == 255):
        raise RuntimeError(f"{config.name}: output is not fully opaque.")
    if np.any(output_rgba[:, :, :3][matte == 0]):
        raise RuntimeError(f"{config.name}: pixels outside matte are not black.")
    source_rgb = source_rgba[:, :, :3]
    output_rgb = output_rgba[:, :, :3]
    changed_rgb = np.any(output_rgb != source_rgb, axis=2) & (matte != 0)
    if not np.array_equal(changed_rgb, shoe_lift_mask):
        raise RuntimeError(
            f"{config.name}: retained RGB changed outside the shoe lift."
        )
    if np.any(shoe_lift_mask):
        if np.any(source_rgb[shoe_lift_mask] == 255):
            raise RuntimeError(
                f"{config.name}: shoe lift would clip a source channel."
            )
        if np.any(
            np.max(output_rgb[shoe_lift_mask], axis=1)
            != SHOE_LIFT_TARGET_VALUE
        ):
            raise RuntimeError(
                f"{config.name}: lifted shoe pixels missed charcoal target "
                f"{SHOE_LIFT_TARGET_VALUE}."
            )
    if np.any(output_rgb[(matte != 0) & (np.max(source_rgb, axis=2) == 0)]):
        raise RuntimeError(
            f"{config.name}: invisible matte bridge pixels became visible."
        )
    if np.any(output_rgba[0, :, :3]) or np.any(output_rgba[-1, :, :3]):
        raise RuntimeError(f"{config.name}: non-black pixels touch a horizontal edge.")
    if np.any(output_rgba[:, 0, :3]) or np.any(output_rgba[:, -1, :3]):
        raise RuntimeError(f"{config.name}: non-black pixels touch a vertical edge.")

    components, _, stats, _ = cv2.connectedComponentsWithStats(matte, 8)
    if components != 2:
        areas = sorted((int(area) for area in stats[1:, cv2.CC_STAT_AREA]), reverse=True)
        raise RuntimeError(
            f"{config.name}: expected one person component, got areas {areas}."
        )

    hsv = cv2.cvtColor(source_rgba[:, :, :3], cv2.COLOR_BGR2HSV)
    high_confidence = (
        (hsv[:, :, 2] >= 72)
        | ((hsv[:, :, 1] >= 62) & (hsv[:, :, 2] >= 34))
    )
    torso_mask = _rect_mask(matte.shape, config.torso_rect) != 0
    required_seed = high_confidence & torso_mask
    required_count = int(np.count_nonzero(required_seed))
    retained_seed = required_seed & (matte != 0)
    retained_count = int(np.count_nonzero(retained_seed))
    retained_ratio = retained_count / required_count
    if retained_ratio < 0.97:
        raise RuntimeError(
            f"{config.name}: retained {retained_ratio:.4f} of "
            "high-confidence torso pixels."
        )
    if not np.array_equal(
        output_rgba[:, :, :3][retained_seed],
        source_rgba[:, :, :3][retained_seed],
    ):
        raise RuntimeError(
            f"{config.name}: retained source torso RGB was altered."
        )

    shoe_pixels: list[int] = []
    shoe_retention: list[float] = []
    shoe_dark_retention: list[float] = []
    shoe_extent_retention: list[float] = []
    shoe_visible_retention: list[float] = []
    shoe_visible_extent_retention: list[float] = []
    source_value = hsv[:, :, 2]
    output_value = np.max(output_rgb, axis=2)
    if len(shoe_reference_masks) != len(config.shoe_rects):
        raise RuntimeError(
            f"{config.name}: shoe validation mask count changed."
        )
    for rect, required_shoe in zip(
        config.shoe_rects,
        shoe_reference_masks,
        strict=True,
    ):
        shoe_mask = _rect_mask(matte.shape, rect) != 0
        count = int(np.count_nonzero(matte[shoe_mask]))
        shoe_pixels.append(count)
        if count < config.minimum_shoe_pixels:
            raise RuntimeError(
                f"{config.name}: dark shoe region {rect} retained only "
                f"{count} pixels."
            )

        required_shoe = required_shoe & shoe_mask
        required_count = int(np.count_nonzero(required_shoe))
        if required_count == 0:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has no source support."
            )
        retained_shoe = required_shoe & (matte != 0)
        retention = int(np.count_nonzero(retained_shoe)) / required_count
        shoe_retention.append(retention)
        if retention < 0.96:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{retention:.4f} of calibrated source support."
            )

        required_dark = required_shoe & (source_value < 72)
        required_dark_count = int(np.count_nonzero(required_dark))
        if required_dark_count == 0:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has no dark source support."
            )
        dark_retention = int(
            np.count_nonzero(required_dark & (matte != 0))
        ) / required_dark_count
        shoe_dark_retention.append(dark_retention)
        if dark_retention < 0.95:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{dark_retention:.4f} of dark source support."
            )

        required_columns = np.flatnonzero(np.any(required_shoe, axis=0))
        retained_columns = np.flatnonzero(np.any(retained_shoe, axis=0))
        required_extent = int(required_columns[-1] - required_columns[0] + 1)
        retained_extent = int(retained_columns[-1] - retained_columns[0] + 1)
        extent_retention = retained_extent / required_extent
        shoe_extent_retention.append(extent_retention)
        rect_width = rect[2] - rect[0]
        if required_extent < round(rect_width * 0.55):
            raise RuntimeError(
                f"{config.name}: shoe region {rect} source support spans only "
                f"{required_extent}/{rect_width}px."
            )
        if extent_retention < 0.95:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{retained_extent}/{required_extent}px horizontal extent."
            )

        visible_shoe = (
            required_shoe
            & (matte != 0)
            & (output_value >= SHOE_VISIBLE_VALUE_MIN)
        )
        visible_count = int(np.count_nonzero(visible_shoe))
        visible_retention = visible_count / required_count
        shoe_visible_retention.append(visible_retention)
        if visible_retention < MINIMUM_VISIBLE_SHOE_RETENTION:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has only "
                f"{visible_retention:.4f} visibly non-black calibrated "
                f"support at value >= {SHOE_VISIBLE_VALUE_MIN}."
            )

        visible_columns = np.flatnonzero(np.any(visible_shoe, axis=0))
        if visible_columns.size == 0:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has no visible output."
            )
        visible_extent = int(visible_columns[-1] - visible_columns[0] + 1)
        visible_extent_retention = visible_extent / required_extent
        shoe_visible_extent_retention.append(visible_extent_retention)
        if visible_extent_retention < 0.95:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} visibly spans only "
                f"{visible_extent}/{required_extent}px horizontal extent."
            )

    foreground_pixels = int(np.count_nonzero(matte))
    black_ratio = 1.0 - foreground_pixels / matte.size
    if black_ratio < 0.68:
        raise RuntimeError(
            f"{config.name}: black ratio {black_ratio:.4f} is too low."
        )

    floor_rows = matte[config.floor_start_y :]
    if floor_rows.size:
        widest_floor_row = int(np.max(np.count_nonzero(floor_rows, axis=1)))
        if widest_floor_row > round(matte.shape[1] * 0.55):
            raise RuntimeError(
                f"{config.name}: wide retained floor band is "
                f"{widest_floor_row}px."
            )

    return CutoutResult(
        frame=output_rgba,
        matte=matte,
        black_ratio=black_ratio,
        foreground_pixels=foreground_pixels,
        shoe_pixels=tuple(shoe_pixels),
        shoe_retention=tuple(shoe_retention),
        shoe_dark_retention=tuple(shoe_dark_retention),
        shoe_extent_retention=tuple(shoe_extent_retention),
        shoe_visible_retention=tuple(shoe_visible_retention),
        shoe_visible_extent_retention=tuple(shoe_visible_extent_retention),
    )
