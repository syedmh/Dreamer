from pathlib import Path

import cv2
import numpy as np


ROOT = Path(__file__).parents[1]
SOURCES = (
    (ROOT / "BoyWalk1.png", 3, 100_000),
    (ROOT / "BoyWalk2.png", 3, 100_000),
    (ROOT / "BoyWalk3.png", 6, 40_000)
)
OUTPUT = ROOT / "assets" / "boy-walk"
MARGIN = 12
SIZE_MATCH_FRAME_INDEXES = (4, 5)


def foreground_mask(image):
    hsv = cv2.cvtColor(image, cv2.COLOR_BGR2HSV)
    brightness = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    subject = np.where(
        (hsv[:, :, 1] > 12) | (brightness < 150),
        255,
        0
    ).astype(np.uint8)
    return subject


def clean_subject(subject):
    subject = cv2.morphologyEx(
        subject,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    )
    inverse = 255 - subject
    hole_count, hole_labels, hole_stats, _ = cv2.connectedComponentsWithStats(inverse, 8)
    for label in range(1, hole_count):
        x, y, width, height, area = hole_stats[label]
        touches_edge = (
            x == 0 or y == 0 or
            x + width == subject.shape[1] or
            y + height == subject.shape[0]
        )
        if not touches_edge and area < 250:
            subject[hole_labels == label] = 255
    subject = cv2.dilate(
        subject,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
        iterations=1
    )
    return cv2.GaussianBlur(subject, (3, 3), 0.45)


def main():
    extracted = []
    for source, expected_subjects, minimum_area in SOURCES:
        sheet = cv2.imread(str(source), cv2.IMREAD_COLOR)
        if sheet is None:
            raise RuntimeError(f"Unable to read {source}")
        if sheet.shape[:2] != (1024, 1536):
            raise RuntimeError(f"{source.name} must be 1536x1024")
        mask = foreground_mask(sheet)
        count, labels, stats, _ = cv2.connectedComponentsWithStats(mask, 8)
        subjects = [
            label
            for label in range(1, count)
            if stats[label, cv2.CC_STAT_AREA] > minimum_area
        ]
        if len(subjects) != expected_subjects:
            raise RuntimeError(
                f"Expected {expected_subjects} subjects in {source.name}, found {len(subjects)}"
            )
        if expected_subjects == 3:
            subjects.sort(key=lambda label: stats[label, cv2.CC_STAT_LEFT])
        else:
            subjects.sort(key=lambda label: stats[label, cv2.CC_STAT_TOP])
            top_row = sorted(
                subjects[:3],
                key=lambda label: stats[label, cv2.CC_STAT_LEFT]
            )
            bottom_row = sorted(
                subjects[3:],
                key=lambda label: stats[label, cv2.CC_STAT_LEFT]
            )
            subjects = top_row + bottom_row

        for label in subjects:
            x, y, width, height, _ = stats[label]
            alpha = clean_subject(np.where(labels == label, 255, 0).astype(np.uint8))
            points = cv2.findNonZero(alpha)
            x, y, width, height = cv2.boundingRect(points)
            color = sheet[y:y + height, x:x + width]
            extracted.append((color, alpha[y:y + height, x:x + width]))

    target_height = round(float(np.median([color.shape[0] for color, _ in extracted[:3]])))
    for index in SIZE_MATCH_FRAME_INDEXES:
        color, alpha = extracted[index]
        scale = target_height / color.shape[0]
        target_width = round(color.shape[1] * scale)
        extracted[index] = (
            cv2.resize(color, (target_width, target_height), interpolation=cv2.INTER_AREA),
            cv2.resize(alpha, (target_width, target_height), interpolation=cv2.INTER_AREA)
        )

    walk_01_height = extracted[0][0].shape[0]
    for index in range(6, len(extracted)):
        color, alpha = extracted[index]
        scale = walk_01_height / color.shape[0]
        target_width = round(color.shape[1] * scale)
        extracted[index] = (
            cv2.resize(color, (target_width, walk_01_height), interpolation=cv2.INTER_AREA),
            cv2.resize(alpha, (target_width, walk_01_height), interpolation=cv2.INTER_AREA)
        )

    canvas_width = max(color.shape[1] for color, _ in extracted) + MARGIN * 2
    canvas_height = max(color.shape[0] for color, _ in extracted) + MARGIN * 2
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for index, (color, alpha) in enumerate(extracted, start=1):
        frame = np.zeros((canvas_height, canvas_width, 4), dtype=np.uint8)
        body = cv2.cvtColor(color, cv2.COLOR_BGR2BGRA)
        body[:, :, 3] = alpha
        x = (canvas_width - body.shape[1]) // 2
        y = canvas_height - MARGIN - body.shape[0]
        frame[y:y + body.shape[0], x:x + body.shape[1]] = body
        destination = OUTPUT / f"walk_{index:02d}.png"
        if not cv2.imwrite(str(destination), frame):
            raise RuntimeError(f"Unable to write {destination}")

    print(
        f"Extracted {len(extracted)} source frames in sheet order to {OUTPUT}: "
        "BoyWalk1 frames 1-3, BoyWalk2 frames 4-6, then BoyWalk3 frames 7-12"
    )


if __name__ == "__main__":
    main()
