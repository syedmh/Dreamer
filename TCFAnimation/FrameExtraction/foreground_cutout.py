"""Deterministic character extraction for the project's black-studio frames."""

from __future__ import annotations

from dataclasses import dataclass

import cv2  # type: ignore
import numpy as np  # type: ignore


Rect = tuple[int, int, int, int]
Point = tuple[int, int]
Polygon = tuple[Point, ...]
SHOE_VISIBLE_VALUE_MIN = 20
MINIMUM_VISIBLE_SHOE_RETENTION = 0.95
DEFAULT_COMPONENT_GAP = 12
MINIMUM_ANATOMY_ATTACHMENT_GAP = 20
LOWER_SOURCE_VALUE_MIN = 8
LOWER_ENVELOPE_PADDING = 3


@dataclass(frozen=True)
class CutoutConfig:
    """Per-pose geometry used to keep dark hair and shoes attached."""

    name: str
    person_rect: Rect
    torso_rect: Rect
    shoe_rects: tuple[Rect, ...]
    floor_start_y: int
    garment_rects: tuple[Rect, ...] = ()
    maximum_component_gap: int = DEFAULT_COMPONENT_GAP
    cool_reject_rects: tuple[Rect, ...] = ()
    floor_reject_rects: tuple[Rect, ...] = ()
    minimum_shoe_pixels: int = 90
    minimum_shoe_retention: float = 0.96
    minimum_dark_shoe_retention: float = 0.95
    minimum_visible_shoe_retention: float = MINIMUM_VISIBLE_SHOE_RETENTION


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
    visible_component_count: int


def _rect_mask(shape: tuple[int, int], rect: Rect) -> np.ndarray:
    height, width = shape
    x0, y0, x1, y1 = rect
    if not (0 <= x0 < x1 <= width and 0 <= y0 < y1 <= height):
        raise RuntimeError(f"Invalid cutout rectangle {rect} for {width}x{height}.")
    mask = np.zeros(shape, dtype=np.uint8)
    mask[y0:y1, x0:x1] = 1
    return mask


def project_source_exclusion_mask(
    canvas_shape: tuple[int, int],
    source_crop: Rect,
    resized_shape: tuple[int, int],
    offset: Point,
    source_polygons: tuple[Polygon, ...],
) -> np.ndarray:
    """Project absolute source-sheet exclusion polygons onto a frame canvas."""

    canvas_height, canvas_width = canvas_shape
    crop_x0, crop_y0, crop_x1, crop_y1 = source_crop
    crop_width = crop_x1 - crop_x0
    crop_height = crop_y1 - crop_y0
    resized_height, resized_width = resized_shape
    offset_x, offset_y = offset
    if crop_width <= 0 or crop_height <= 0:
        raise RuntimeError(f"Invalid source crop {source_crop}.")
    if resized_width <= 0 or resized_height <= 0:
        raise RuntimeError(f"Invalid resized source shape {resized_shape}.")
    if (
        offset_x < 0
        or offset_y < 0
        or offset_x + resized_width > canvas_width
        or offset_y + resized_height > canvas_height
    ):
        raise RuntimeError(
            f"Projected source mask {resized_shape} at {offset} exceeds "
            f"{canvas_width}x{canvas_height} canvas."
        )

    local = np.zeros((crop_height, crop_width), dtype=np.uint8)
    for polygon in source_polygons:
        if len(polygon) < 3:
            raise RuntimeError(
                f"Source exclusion polygon must have at least 3 points: "
                f"{polygon}."
            )
        outside = [
            point
            for point in polygon
            if not (
                crop_x0 <= point[0] < crop_x1
                and crop_y0 <= point[1] < crop_y1
            )
        ]
        if outside:
            raise RuntimeError(
                f"Source exclusion polygon {polygon} exceeds crop "
                f"{source_crop}: {outside}."
            )
        points = np.asarray(
            [
                (source_x - crop_x0, source_y - crop_y0)
                for source_x, source_y in polygon
            ],
            dtype=np.int32,
        )
        cv2.fillPoly(local, (points,), 1, lineType=cv2.LINE_8)

    if (resized_height, resized_width) != local.shape:
        local = cv2.resize(
            local,
            (resized_width, resized_height),
            interpolation=cv2.INTER_NEAREST,
        )
    projected = np.zeros(canvas_shape, dtype=np.uint8)
    projected[
        offset_y : offset_y + resized_height,
        offset_x : offset_x + resized_width,
    ] = local
    return projected


def _component_gap(
    anchor: np.ndarray,
    component: np.ndarray,
    maximum_gap: int,
) -> float:
    if np.any(anchor & component):
        return 0.0
    distance = cv2.distanceTransform(
        np.where(anchor, 0, 1).astype(np.uint8),
        cv2.DIST_L2,
        3,
    )
    gap = float(np.min(distance[component]))
    if gap > maximum_gap + 1:
        return gap
    return gap


def _lower_anatomy_evidence(
    high_confidence: np.ndarray,
    neutral_or_warm: np.ndarray,
    value: np.ndarray,
    region: np.ndarray,
    floor_start_y: int,
    maximum_gap: int,
) -> tuple[np.ndarray, np.ndarray, np.ndarray, np.ndarray]:
    """Build source evidence for a separate lower-anatomy segmentation."""

    rows = np.indices(value.shape)[0]
    trusted = (
        high_confidence
        & region
        & (value >= 24)
    )
    above_floor = trusted & (rows < floor_start_y)
    if not np.any(above_floor):
        raise RuntimeError("Lower anatomy region has no above-floor source seed.")

    candidate = (
        region
        & (value >= LOWER_SOURCE_VALUE_MIN)
        & (
            (rows < floor_start_y)
            | neutral_or_warm
        )
    )
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        candidate.astype(np.uint8),
        8,
    )
    connected_evidence = np.zeros(value.shape, dtype=bool)
    anchor = above_floor.copy()
    pending: list[np.ndarray] = []
    for label in range(1, count):
        component = labels == label
        x, y, width, height, area = map(int, stats[label])
        if width >= 80 and height <= 4:
            continue
        if area <= 2:
            continue
        if np.any(component & anchor):
            connected_evidence |= component
            anchor |= component
        else:
            pending.append(component)

    changed = True
    while changed:
        changed = False
        remaining = []
        for component in pending:
            if _component_gap(
                anchor,
                component,
                maximum_gap,
            ) <= maximum_gap:
                connected_evidence |= component
                anchor |= component
                changed = True
            else:
                remaining.append(component)
        pending = remaining

    connected_evidence &= region
    validation_reference = trusted & connected_evidence
    reference_count, reference_labels, reference_stats, _ = (
        cv2.connectedComponentsWithStats(
            validation_reference.astype(np.uint8),
            8,
        )
    )
    validation_reference = np.zeros(value.shape, dtype=bool)
    for label in range(1, reference_count):
        if int(reference_stats[label, cv2.CC_STAT_AREA]) >= 20:
            validation_reference |= reference_labels == label
    if not np.any(validation_reference):
        raise RuntimeError("Lower anatomy region has no source reference.")

    horizontal_reference = cv2.dilate(
        connected_evidence.astype(np.uint8),
        cv2.getStructuringElement(
            cv2.MORPH_RECT,
            (LOWER_ENVELOPE_PADDING * 2 + 1, 1),
        ),
        iterations=1,
    ) != 0
    supported_columns = np.any(horizontal_reference, axis=0)
    envelope = region & supported_columns[np.newaxis, :]
    shape_seed = connected_evidence & (value >= 40)
    shape_envelope = cv2.dilate(
        shape_seed.astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (31, 31)),
        iterations=1,
    ) != 0
    envelope &= shape_envelope
    attachment_seed = validation_reference & (rows < floor_start_y)
    if not np.any(attachment_seed):
        raise RuntimeError("Lower anatomy region has no ankle attachment seed.")
    return (
        validation_reference,
        attachment_seed,
        connected_evidence,
        envelope,
    )


def _segment_local_anatomy(
    bgr: np.ndarray,
    definite_seed: np.ndarray,
    attachment_seed: np.ndarray,
    source_evidence: np.ndarray,
    envelope: np.ndarray,
    maximum_gap: int,
) -> np.ndarray:
    """Segment anatomy from source pixels, then require seed connectivity."""

    if not np.any(definite_seed):
        raise RuntimeError("Local anatomy segmentation has no definite seed.")
    if not np.any(attachment_seed):
        raise RuntimeError("Local anatomy segmentation has no attachment seed.")
    if not np.any(source_evidence):
        raise RuntimeError("Local anatomy segmentation has no source evidence.")

    local_mask = np.full(
        source_evidence.shape,
        cv2.GC_BGD,
        dtype=np.uint8,
    )
    local_mask[envelope] = cv2.GC_PR_BGD
    local_mask[source_evidence] = cv2.GC_PR_FGD

    local_mask[definite_seed] = cv2.GC_FGD

    background_model = np.zeros((1, 65), dtype=np.float64)
    foreground_model = np.zeros((1, 65), dtype=np.float64)
    cv2.grabCut(
        bgr,
        local_mask,
        None,
        background_model,
        foreground_model,
        3,
        cv2.GC_INIT_WITH_MASK,
    )
    segmented = (
        ((local_mask == cv2.GC_FGD) | (local_mask == cv2.GC_PR_FGD))
        & envelope
    )

    # Retain only components produced by local GrabCut and connected or
    # proximal to its unambiguous seeds. The envelope is never admitted.
    count, labels, stats, _ = cv2.connectedComponentsWithStats(
        segmented.astype(np.uint8),
        8,
    )
    accepted = attachment_seed.copy()
    pending: list[np.ndarray] = []
    for label in range(1, count):
        component = labels == label
        _, _, width, height, area = map(int, stats[label])
        if area <= 2 or (width >= 80 and height <= 4):
            continue
        if np.any(component & accepted):
            accepted |= component
        else:
            pending.append(component)

    changed = True
    while changed:
        changed = False
        remaining = []
        for component in pending:
            if _component_gap(accepted, component, maximum_gap) <= maximum_gap:
                accepted |= component
                changed = True
            else:
                remaining.append(component)
        pending = remaining

    accepted &= envelope
    if not np.any(accepted & attachment_seed):
        raise RuntimeError("Local anatomy segmentation lost its attachment seed.")
    return accepted


def _retain_person_component(
    mask: np.ndarray,
    torso_mask: np.ndarray,
    anatomy_references: tuple[np.ndarray, ...],
    anatomy_envelopes: tuple[np.ndarray, ...],
    floor_start_y: int,
    maximum_gap: int,
) -> np.ndarray:
    count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
    if count <= 1:
        raise RuntimeError("Foreground extraction produced no components.")

    candidates: list[tuple[int, int]] = []
    for label in range(1, count):
        component = labels == label
        torso_overlap = int(np.count_nonzero(component & (torso_mask != 0)))
        anatomy_overlap = sum(
            int(np.count_nonzero(component & reference))
            for reference in anatomy_references
        )
        if torso_overlap:
            candidates.append((torso_overlap * 100 + anatomy_overlap, label))

    if not candidates:
        raise RuntimeError("No foreground component intersects the torso seed.")

    person_label = max(candidates)[1]
    torso_component = labels == person_label
    person = torso_component.copy()
    rows = np.indices(mask.shape)[0]

    # A shoe or garment tail can be separated from its matching cuff/hem by
    # source-black antialiasing. Extra components require an independent
    # source reference, must stay inside that reference's tight envelope, and
    # must remain within a bounded gap of the already approved anatomy.
    pending_components: list[np.ndarray] = []
    for label in range(1, count):
        if label == person_label:
            continue
        component = labels == label
        if not np.any(component & (rows >= floor_start_y)):
            pending_components.append(component)
            continue
        for reference, envelope in zip(
            anatomy_references,
            anatomy_envelopes,
            strict=True,
        ):
            if not np.any(component & reference):
                continue
            if np.any(
                component
                & (rows >= floor_start_y)
                & ~envelope
            ):
                continue
            pending_components.append(component)
            break

    changed = True
    while changed:
        changed = False
        remaining = []
        for component in pending_components:
            attachment_gap = max(
                maximum_gap,
                MINIMUM_ANATOMY_ATTACHMENT_GAP,
            )
            if _component_gap(person, component, attachment_gap) <= attachment_gap:
                person |= component
                changed = True
            else:
                remaining.append(component)
        pending_components = remaining

    if not np.any(person):
        raise RuntimeError("Foreground extraction lost the person component.")
    return person.astype(np.uint8)


def isolate_character(
    canvas_rgba: np.ndarray,
    config: CutoutConfig,
    source_exclusion_mask: np.ndarray | None = None,
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
    if source_exclusion_mask is None:
        source_exclusion = np.zeros((height, width), dtype=bool)
    else:
        if source_exclusion_mask.shape != (height, width):
            raise RuntimeError(
                f"{config.name}: source exclusion shape is "
                f"{source_exclusion_mask.shape}; expected {(height, width)}."
            )
        source_exclusion = source_exclusion_mask != 0
    hsv = cv2.cvtColor(bgr, cv2.COLOR_BGR2HSV)
    saturation = hsv[:, :, 1]
    value = hsv[:, :, 2]

    person_region = _rect_mask((height, width), config.person_rect)
    torso_region = _rect_mask((height, width), config.torso_rect)
    shoe_regions = tuple(
        _rect_mask((height, width), rect) for rect in config.shoe_rects
    )

    # Bright cloth/skin and saturated green are unambiguous foreground.
    # Lower-body references are built separately from source evidence so a
    # permissive support region can never become foreground by itself.
    source_high_confidence = (
        ((value >= 72) | ((saturation >= 62) & (value >= 34)))
        & (person_region != 0)
        & ~source_exclusion
    )
    row_indices = np.indices((height, width))[0]
    high_confidence = (
        source_high_confidence
        & (row_indices < config.floor_start_y)
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

    neutral_or_warm = (
        (saturation <= 35)
        | (
            bgr[:, :, 0].astype(np.int16)
            <= bgr[:, :, 2].astype(np.int16) + 4
        )
    )
    shoe_reference_masks: list[np.ndarray] = []
    shoe_segmentations: list[np.ndarray] = []
    shoe_envelopes: list[np.ndarray] = []
    for shoe_region in shoe_regions:
        allowed_shoe_region = (shoe_region != 0) & ~source_exclusion
        seed, attachment_seed, evidence, envelope = _lower_anatomy_evidence(
            source_high_confidence,
            neutral_or_warm,
            value,
            allowed_shoe_region,
            config.floor_start_y,
            config.maximum_component_gap,
        )
        shoe_envelopes.append(envelope)
        segmentation = _segment_local_anatomy(
            bgr,
            seed,
            attachment_seed,
            evidence,
            envelope,
            config.maximum_component_gap,
        )
        shoe_segmentations.append(segmentation)
        shoe_reference_masks.append(
            segmentation
            & evidence
            & (value >= SHOE_VISIBLE_VALUE_MIN)
        )

    garment_regions = tuple(
        _rect_mask((height, width), rect) for rect in config.garment_rects
    )
    garment_segmentations: list[np.ndarray] = []
    garment_envelopes: list[np.ndarray] = []
    for garment_region in garment_regions:
        reference = (
            source_high_confidence
            & (garment_region != 0)
        )
        if not np.any(reference):
            raise RuntimeError(
                f"{config.name}: garment region has no source evidence."
            )
        envelope = cv2.dilate(
            reference.astype(np.uint8),
            cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (7, 7)),
            iterations=1,
        ) != 0
        envelope &= garment_region != 0
        garment_envelopes.append(envelope)
        garment_segmentations.append(
            _segment_local_anatomy(
                bgr,
                reference,
                reference,
                (garment_region != 0)
                & ~source_exclusion
                & (value >= LOWER_SOURCE_VALUE_MIN),
                envelope & ~source_exclusion,
                config.maximum_component_gap,
            )
        )

    anatomy_references = tuple(
        (*shoe_segmentations, *garment_segmentations)
    )
    anatomy_envelopes = tuple((*shoe_envelopes, *garment_envelopes))

    # Dilated trusted color reaches dark hair, beard edges, and cuffs while
    # the calibrated person rectangle excludes neighboring panels.
    probable_foreground = cv2.dilate(
        high_confidence.astype(np.uint8),
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (31, 31)),
        iterations=1,
    ) != 0
    probable_foreground &= person_region != 0
    probable_foreground &= value >= 4
    probable_foreground &= ~source_exclusion

    grabcut_mask = np.full((height, width), cv2.GC_BGD, dtype=np.uint8)
    grabcut_mask[person_region != 0] = cv2.GC_PR_BGD
    grabcut_mask[probable_foreground] = cv2.GC_PR_FGD
    grabcut_mask[definite_foreground] = cv2.GC_FGD
    grabcut_mask[source_exclusion] = cv2.GC_BGD

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

    # Lower anatomy is added only from its own seeded segmentation. The
    # rectangles and envelopes bound that segmentation but never become
    # foreground merely by membership.
    for segmentation in (*shoe_segmentations, *garment_segmentations):
        grabcut_foreground |= segmentation.astype(np.uint8)
    grabcut_foreground[source_exclusion] = 0

    # Below the calibrated floor line, source-derived envelopes may reject
    # segmented foreground but never add pixels rejected by both segmenters.
    lower_envelope = np.zeros((height, width), dtype=bool)
    for envelope in shoe_envelopes:
        lower_envelope |= envelope
    grabcut_foreground[config.floor_start_y :] &= lower_envelope[
        config.floor_start_y :
    ].astype(np.uint8)
    hard_rejection = np.zeros((height, width), dtype=bool)
    for rect in config.floor_reject_rects:
        reject_region = _rect_mask((height, width), rect) != 0
        hard_rejection |= reject_region & (value <= 40)
    grabcut_foreground[hard_rejection] = 0

    cool_rejection = np.zeros((height, width), dtype=bool)
    for rect in config.cool_reject_rects:
        reject_region = _rect_mask((height, width), rect) != 0
        cool_reflection = (
            reject_region
            & (hsv[:, :, 0] >= 80)
            & (hsv[:, :, 0] <= 140)
            & (saturation >= 20)
            & (value >= 10)
        )
        cool_rejection |= cool_reflection
        grabcut_foreground[cool_reflection] = 0
    shoe_reference_masks = [
        reference
        & ~cool_rejection
        & ~hard_rejection
        & ~source_exclusion
        for reference in shoe_reference_masks
    ]

    matte = _retain_person_component(
        grabcut_foreground,
        torso_region != 0,
        anatomy_references,
        anatomy_envelopes,
        config.floor_start_y,
        config.maximum_component_gap,
    )

    output = np.zeros_like(canvas_rgba)
    output[:, :, 3] = 255
    output[:, :, :3][matte != 0] = bgr[matte != 0]
    result = validate_cutout(
        canvas_rgba,
        output,
        matte,
        config,
        tuple(shoe_reference_masks),
        tuple(shoe_envelopes),
        anatomy_envelopes,
    )
    return result


def validate_cutout(
    source_rgba: np.ndarray,
    output_rgba: np.ndarray,
    matte: np.ndarray,
    config: CutoutConfig,
    shoe_reference_masks: tuple[np.ndarray, ...],
    shoe_envelopes: tuple[np.ndarray, ...],
    anatomy_envelopes: tuple[np.ndarray, ...],
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
    if np.any(changed_rgb):
        raise RuntimeError(
            f"{config.name}: retained RGB differs from source RGB."
        )
    if np.any(output_rgb[(matte != 0) & (np.max(source_rgb, axis=2) == 0)]):
        raise RuntimeError(
            f"{config.name}: invisible matte bridge pixels became visible."
        )
    if np.any(output_rgba[0, :, :3]) or np.any(output_rgba[-1, :, :3]):
        raise RuntimeError(f"{config.name}: non-black pixels touch a horizontal edge.")
    if np.any(output_rgba[:, 0, :3]) or np.any(output_rgba[:, -1, :3]):
        raise RuntimeError(f"{config.name}: non-black pixels touch a vertical edge.")

    components, labels, stats, _ = cv2.connectedComponentsWithStats(matte, 8)
    if components <= 1:
        raise RuntimeError(f"{config.name}: matte has no person component.")
    torso_mask = _rect_mask(matte.shape, config.torso_rect) != 0
    torso_labels = {
        int(label)
        for label in np.unique(labels[torso_mask])
        if label != 0
    }
    if len(torso_labels) != 1:
        raise RuntimeError(
            f"{config.name}: expected one torso-connected component, "
            f"got labels {sorted(torso_labels)}."
        )
    torso_label = next(iter(torso_labels))
    approved_anatomy = np.zeros(matte.shape, dtype=bool)
    for envelope in anatomy_envelopes:
        approved_anatomy |= envelope
    for label in range(1, components):
        if label == torso_label:
            continue
        component = labels == label
        if not np.any(component & approved_anatomy):
            raise RuntimeError(
                f"{config.name}: component {label} has no approved anatomy."
            )
        if np.any(
            component
            & (np.indices(matte.shape)[0] >= config.floor_start_y)
            & ~approved_anatomy
        ):
            raise RuntimeError(
                f"{config.name}: component {label} exceeds anatomy envelope."
            )

    hsv = cv2.cvtColor(source_rgba[:, :, :3], cv2.COLOR_BGR2HSV)
    high_confidence = (
        (hsv[:, :, 2] >= 72)
        | ((hsv[:, :, 1] >= 62) & (hsv[:, :, 2] >= 34))
    )
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
    if (
        len(shoe_reference_masks) != len(config.shoe_rects)
        or len(shoe_envelopes) != len(config.shoe_rects)
    ):
        raise RuntimeError(
            f"{config.name}: shoe validation mask count changed."
        )
    for rect, required_shoe, shoe_envelope in zip(
        config.shoe_rects,
        shoe_reference_masks,
        shoe_envelopes,
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
        if retention < config.minimum_shoe_retention:
            missing_shoe = required_shoe & (matte == 0)
            missing_components, _, missing_stats, _ = (
                cv2.connectedComponentsWithStats(
                    missing_shoe.astype(np.uint8),
                    8,
                )
            )
            missing_bounds = [
                tuple(map(int, missing_stats[label]))
                for label in range(1, missing_components)
            ]
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{retention:.4f} of calibrated source support; "
                f"missing_components={missing_bounds}."
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
        if dark_retention < config.minimum_dark_shoe_retention:
            missing_dark = required_dark & (matte == 0)
            missing_components, _, missing_stats, _ = (
                cv2.connectedComponentsWithStats(
                    missing_dark.astype(np.uint8),
                    8,
                )
            )
            missing_bounds = [
                tuple(map(int, missing_stats[label]))
                for label in range(1, missing_components)
            ]
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{dark_retention:.4f} of dark source support; "
                f"missing_components={missing_bounds}."
            )

        required_columns = np.flatnonzero(
            np.count_nonzero(required_shoe, axis=0) >= 2
        )
        retained_columns = np.flatnonzero(
            np.count_nonzero(retained_shoe, axis=0) >= 2
        )
        required_extent = int(required_columns[-1] - required_columns[0] + 1)
        retained_extent = int(retained_columns[-1] - retained_columns[0] + 1)
        extent_retention = retained_extent / required_extent
        shoe_extent_retention.append(extent_retention)
        if required_extent < 20:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} source support spans only "
                f"{required_extent}px."
            )
        if extent_retention < 0.95:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} retained only "
                f"{retained_extent}/{required_extent}px horizontal extent."
            )

        required_visible = (
            required_shoe
            & (source_value >= SHOE_VISIBLE_VALUE_MIN)
        )
        required_visible_count = int(np.count_nonzero(required_visible))
        if required_visible_count == 0:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has no naturally visible "
                "source support."
            )
        visible_shoe = required_visible & (matte != 0)
        visible_count = int(np.count_nonzero(visible_shoe))
        visible_retention = visible_count / required_visible_count
        shoe_visible_retention.append(visible_retention)
        if visible_retention < config.minimum_visible_shoe_retention:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has only "
                f"{visible_retention:.4f} retention of naturally visible "
                f"source support at value >= {SHOE_VISIBLE_VALUE_MIN}."
            )

        required_visible_columns = np.flatnonzero(
            np.count_nonzero(required_visible, axis=0) >= 2
        )
        visible_columns = np.flatnonzero(
            np.count_nonzero(visible_shoe, axis=0) >= 2
        )
        if visible_columns.size == 0:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} has no visible output."
            )
        required_visible_extent = int(
            required_visible_columns[-1]
            - required_visible_columns[0]
            + 1
        )
        visible_extent = int(visible_columns[-1] - visible_columns[0] + 1)
        visible_extent_retention = visible_extent / required_visible_extent
        shoe_visible_extent_retention.append(visible_extent_retention)
        if visible_extent_retention < 0.95:
            raise RuntimeError(
                f"{config.name}: shoe region {rect} visibly spans only "
                f"{visible_extent}/{required_visible_extent}px source extent."
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

    sole_union = np.zeros(matte.shape, dtype=bool)
    for shoe_envelope in shoe_envelopes:
        sole_union |= shoe_envelope
    lower_overrun = (
        (matte != 0)
        & (np.indices(matte.shape)[0] >= config.floor_start_y)
        & ~sole_union
    )
    if np.any(lower_overrun):
        raise RuntimeError(
            f"{config.name}: retained lower pixels exceed all sole envelopes."
        )

    visible_mask = np.max(output_rgb, axis=2) > 0
    visible_components, visible_labels, visible_stats, _ = (
        cv2.connectedComponentsWithStats(
            visible_mask.astype(np.uint8),
            8,
        )
    )
    for label in range(1, visible_components):
        component = visible_labels == label
        if np.any(component & torso_mask):
            continue
        if not np.any(component & approved_anatomy):
            area = int(visible_stats[label, cv2.CC_STAT_AREA])
            raise RuntimeError(
                f"{config.name}: visible component {label} area {area} "
                "does not map to approved anatomy."
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
        visible_component_count=visible_components - 1,
    )
