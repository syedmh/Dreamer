from pathlib import Path

import cv2
import numpy as np


SOURCE = Path(__file__).parents[1] / "BoyTurning.png"
OUTPUT = Path(__file__).parents[1] / "assets" / "boy-turn"
X_EDGES = (0, 341, 683, 1024)
Y_EDGES = (0, 512, 1024, 1536)


def extract_subject(cell):
    rgb = cell[:, :, :3]
    hsv = cv2.cvtColor(rgb, cv2.COLOR_BGR2HSV)
    brightness = cv2.cvtColor(rgb, cv2.COLOR_BGR2GRAY)
    subject = np.where(
        (hsv[:, :, 1] > 12) | (brightness < 150),
        255,
        0
    ).astype(np.uint8)

    count, labels, stats, _ = cv2.connectedComponentsWithStats(subject, 8)
    largest = 1 + np.argmax(stats[1:, cv2.CC_STAT_AREA])
    subject = np.where(labels == largest, 255, 0).astype(np.uint8)
    subject = cv2.morphologyEx(
        subject,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (5, 5))
    )
    contours, _ = cv2.findContours(subject, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    cv2.drawContours(subject, contours, -1, 255, cv2.FILLED)
    subject = cv2.dilate(
        subject,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
        iterations=1
    )
    return cv2.GaussianBlur(subject, (3, 3), 0.45)


def main():
    sheet = cv2.imread(str(SOURCE), cv2.IMREAD_COLOR)
    if sheet is None:
        raise RuntimeError(f"Unable to read {SOURCE}")

    extracted = []
    bounds = []
    for row in range(3):
        for column in range(3):
            cell = sheet[
                Y_EDGES[row]:Y_EDGES[row + 1],
                X_EDGES[column]:X_EDGES[column + 1]
            ]
            alpha = extract_subject(cell)
            points = cv2.findNonZero(alpha)
            if points is None:
                raise RuntimeError(f"No subject found in frame {len(extracted) + 1}")
            x, y, width, height = cv2.boundingRect(points)
            extracted.append((cell, alpha))
            bounds.append((x, y, x + width, y + height))

    left = min(bound[0] for bound in bounds)
    top = min(bound[1] for bound in bounds)
    right = max(bound[2] for bound in bounds)
    bottom = max(bound[3] for bound in bounds)
    OUTPUT.mkdir(parents=True, exist_ok=True)

    for index, (cell, alpha) in enumerate(extracted, start=1):
        color = cell[top:bottom, left:right]
        frame_alpha = alpha[top:bottom, left:right]
        frame = cv2.cvtColor(color, cv2.COLOR_BGR2BGRA)
        frame[:, :, 3] = frame_alpha
        destination = OUTPUT / f"turn_{index:02d}.png"
        if not cv2.imwrite(str(destination), frame):
            raise RuntimeError(f"Unable to write {destination}")

    print(
        f"Extracted {len(extracted)} frames to {OUTPUT} "
        f"with shared crop ({left}, {top})-({right}, {bottom})"
    )


if __name__ == "__main__":
    main()
