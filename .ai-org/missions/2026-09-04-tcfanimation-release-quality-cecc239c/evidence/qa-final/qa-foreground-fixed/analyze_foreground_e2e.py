from __future__ import annotations

import hashlib
import json
import re
import sys
from collections import Counter
from pathlib import Path
from typing import Any

import cv2
import numpy as np
from PIL import Image


FIXED_ROOT = Path(__file__).resolve().parent
PROJECT = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation")
EXE = PROJECT / "Build" / "TCFAnimation.exe"
CAPTURE_RESULTS = FIXED_ROOT / "foreground-e2e-capture-results.json"
RESULTS = FIXED_ROOT / "foreground-e2e-results.json"
REPORT = FIXED_ROOT / "qa-foreground-results.md"
EXPECTED_EXE_SHA256 = "6C1EEA789E89897BECFCD6A90A5ABDB4B591E162EF1C1E780FDAD82E3B83C96F"


class Analyzer:
    def __init__(self, capture_results: Path):
        self.data = json.loads(capture_results.read_text(encoding="utf-8"))
        self.screens = {row["name"]: row for row in self.data["screenshots"]}
        self.processes = {row["name"]: row for row in self.data["processes"]}
        self.assertions: list[dict[str, Any]] = []
        self._templates: dict[str, np.ndarray] = {}

    def add(
        self,
        name: str,
        expected: Any,
        actual: Any,
        passed: bool,
        evidence: str | Path | None = None,
    ) -> None:
        row = {
            "name": name,
            "expected": expected,
            "actual": actual,
            "passed": bool(passed),
        }
        if evidence is not None:
            evidence_path = Path(evidence)
            row["evidencePath"] = str(evidence_path)
            if evidence_path.is_file():
                row["evidenceSha256"] = hashlib.sha256(
                    evidence_path.read_bytes()
                ).hexdigest().upper()
        self.assertions.append(row)
        print(
            f"ASSERT {'PASS' if passed else 'FAIL'} name={name} "
            f"expected={json.dumps(expected, ensure_ascii=False)} "
            f"actual={json.dumps(actual, ensure_ascii=False)}"
        )

    def screen_path(self, name: str) -> Path:
        return Path(self.screens[name]["path"])

    def image(self, name: str) -> np.ndarray:
        return np.asarray(Image.open(self.screen_path(name)).convert("RGB"))

    @staticmethod
    def delta(a: np.ndarray, b: np.ndarray, tolerance: int = 4) -> dict[str, Any]:
        if a.shape != b.shape:
            return {
                "sameShape": False,
                "leftShape": list(a.shape),
                "rightShape": list(b.shape),
            }
        values = np.abs(a.astype(np.int16) - b.astype(np.int16))
        changed = np.any(values > tolerance, axis=2)
        return {
            "sameShape": True,
            "changedPixels": int(changed.sum()),
            "meanAbsoluteDelta": round(float(values.mean()), 5),
            "maxChannelDelta": int(values.max()),
        }

    @staticmethod
    def component_metrics(image: np.ndarray) -> dict[str, Any]:
        mask = (image.max(axis=2) > 18).astype(np.uint8)
        mask[:10, :] = 0
        mask[-10:, :] = 0
        mask[:, :10] = 0
        mask[:, -10:] = 0
        count, labels, stats, centroids = cv2.connectedComponentsWithStats(mask, 8)
        candidates: list[tuple[int, int, int, int, int, int]] = []
        height, width = mask.shape
        for index in range(1, count):
            x, y, w, h, area = map(int, stats[index])
            if (
                area > 8_000
                and h > height * 0.45
                and w < width * 0.45
                and y < height * 0.40
            ):
                candidates.append((area, x, y, w, h, index))
        if not candidates:
            raise AssertionError("No character-sized foreground component found")
        area, x, y, w, h, index = max(candidates)
        cx, cy = map(float, centroids[index])
        background_ratio = float(np.all(image <= 8, axis=2).mean())
        return {
            "area": area,
            "bbox": [x, y, w, h],
            "centroid": [round(cx, 2), round(cy, 2)],
            "centroidRatio": [round(cx / width, 5), round(cy / height, 5)],
            "heightRatio": round(h / height, 5),
            "backgroundBlackRatio": round(background_ratio, 5),
            "mask": mask,
        }

    @staticmethod
    def normalized_mask(image: np.ndarray, screen: bool) -> np.ndarray:
        mask = (image.max(axis=2) > 18).astype(np.uint8)
        if screen:
            metrics = Analyzer.component_metrics(image)
            x, y, w, h = metrics["bbox"]
            x0, x1 = max(0, x - 15), min(mask.shape[1], x + w + 15)
            y0, y1 = max(0, y - 10), min(mask.shape[0], y + h + 10)
            crop = mask[y0:y1, x0:x1]
        else:
            crop = mask
        ys, xs = np.where(crop)
        if not len(xs):
            raise AssertionError("Empty foreground mask")
        crop = crop[ys.min() : ys.max() + 1, xs.min() : xs.max() + 1]
        return cv2.resize(crop, (256, 512), interpolation=cv2.INTER_NEAREST)

    def template(self, group: str, prefix: str, index: int) -> np.ndarray:
        key = f"{group}/{prefix}_{index:02d}"
        if key not in self._templates:
            file_index = str(index) if prefix == "turn" else f"{index:02d}"
            path = PROJECT / "Frames" / group / f"{prefix}_{file_index}.png"
            image = np.asarray(Image.open(path).convert("RGB"))
            self._templates[key] = self.normalized_mask(image, screen=False)
        return self._templates[key]

    def classify(
        self,
        screen_name: str,
        candidates: list[tuple[str, str, int]],
    ) -> dict[str, Any]:
        actual = self.normalized_mask(self.image(screen_name), screen=True)
        scores: list[dict[str, Any]] = []
        for group, prefix, index in candidates:
            template = self.template(group, prefix, index)
            intersection = int(np.logical_and(actual, template).sum())
            union = int(np.logical_or(actual, template).sum())
            score = intersection / union
            scores.append(
                {
                    "group": group,
                    "frame": index,
                    "score": round(score, 5),
                }
            )
        return max(scores, key=lambda row: row["score"])

    @staticmethod
    def light_ui_metrics(image: np.ndarray, above_ratio: float = 0.65) -> dict[str, Any]:
        height, width, _ = image.shape
        light = np.all(image > 220, axis=2).astype(np.uint8)
        count, _labels, stats, centroids = cv2.connectedComponentsWithStats(light, 8)
        candidates: list[tuple[int, int, int, int, int, float, float]] = []
        for index in range(1, count):
            x, y, w, h, area = map(int, stats[index])
            if area > 3_000 and y < height * above_ratio:
                cx, cy = map(float, centroids[index])
                candidates.append((area, x, y, w, h, cx, cy))
        if not candidates:
            return {"present": False}
        area, x, y, w, h, cx, cy = max(candidates)
        return {
            "present": True,
            "area": area,
            "bbox": [x, y, w, h],
            "centroid": [round(cx, 2), round(cy, 2)],
            "inside": x >= 0 and y >= 0 and x + w <= width and y + h <= height,
        }

    def assert_classification(
        self,
        assertion_name: str,
        screen_name: str,
        expected_group: str,
        expected_frame: int,
        candidates: list[tuple[str, str, int]],
        minimum_score: float = 0.94,
    ) -> dict[str, Any]:
        result = self.classify(screen_name, candidates)
        passed = (
            result["group"] == expected_group
            and result["frame"] == expected_frame
            and result["score"] >= minimum_score
        )
        self.add(
            assertion_name,
            {
                "group": expected_group,
                "frame": expected_frame,
                "minimumScore": minimum_score,
            },
            result,
            passed,
            self.screen_path(screen_name),
        )
        return result

    def run(self) -> None:
        actual_sha = hashlib.sha256(EXE.read_bytes()).hexdigest().upper()
        self.add(
            "release.executable.sha256",
            EXPECTED_EXE_SHA256,
            actual_sha,
            actual_sha == EXPECTED_EXE_SHA256,
            EXE,
        )

        idle = self.component_metrics(self.image("directional-idle"))
        self.add(
            "baseline.visible-character-on-black",
            {
                "characterAreaGreaterThan": 40_000,
                "heightRatioGreaterThan": 0.70,
                "backgroundBlackRatioGreaterThan": 0.85,
            },
            {k: v for k, v in idle.items() if k != "mask"},
            idle["area"] > 40_000
            and idle["heightRatio"] > 0.70
            and idle["backgroundBlackRatio"] > 0.85,
            self.screen_path("directional-idle"),
        )
        turn_candidates = [
            (side, "turn", frame)
            for side in ("LeftTurn", "RightTurn")
            for frame in range(3)
        ]
        self.assert_classification(
            "baseline.front-idle-pose",
            "directional-idle",
            "LeftTurn",
            0,
            turn_candidates,
        )

        directional_names = [
            "directional-idle",
            "directional-left-turn",
            "directional-left-walk",
            "directional-left-edge",
            "directional-left-return",
            "directional-right-walk-before-reversal",
            "directional-reversal",
            "directional-both-held-neutral",
            "directional-right-edge",
            "directional-right-return",
        ]
        dm = {name: self.component_metrics(self.image(name)) for name in directional_names}
        width = self.image("directional-idle").shape[1]
        self.add(
            "directional.left-traversal-and-edge",
            "centroid moves left and reaches left 20% edge zone",
            {
                name: dm[name]["centroid"][0]
                for name in (
                    "directional-idle",
                    "directional-left-walk",
                    "directional-left-edge",
                )
            },
            dm["directional-left-walk"]["centroid"][0]
            < dm["directional-idle"]["centroid"][0] - 80
            and dm["directional-left-edge"]["centroid"][0] < width * 0.20,
            self.screen_path("directional-left-edge"),
        )
        self.add(
            "directional.left-release-returns-front-at-edge",
            "front pose remains in calibrated left edge zone",
            {
                "centroidX": dm["directional-left-return"]["centroid"][0],
                "classification": self.classify(
                    "directional-left-return", turn_candidates
                ),
            },
            dm["directional-left-return"]["centroid"][0] < width * 0.20
            and self.classify("directional-left-return", turn_candidates)["frame"] == 0,
            self.screen_path("directional-left-return"),
        )
        self.add(
            "directional.reversal-and-both-held-neutral",
            "reversal changes pose; both-held settles to front without edge jump",
            {
                "rightWalkHash": self.screens[
                    "directional-right-walk-before-reversal"
                ]["sha256"],
                "reversalHash": self.screens["directional-reversal"]["sha256"],
                "bothHeldClassification": self.classify(
                    "directional-both-held-neutral", turn_candidates
                ),
                "reversalX": dm["directional-reversal"]["centroid"][0],
                "bothHeldX": dm["directional-both-held-neutral"]["centroid"][0],
            },
            self.screens["directional-right-walk-before-reversal"]["sha256"]
            != self.screens["directional-reversal"]["sha256"]
            and self.classify("directional-both-held-neutral", turn_candidates)[
                "frame"
            ]
            == 0
            and abs(
                dm["directional-reversal"]["centroid"][0]
                - dm["directional-both-held-neutral"]["centroid"][0]
            )
            < 50,
            self.screen_path("directional-both-held-neutral"),
        )
        self.add(
            "directional.right-traversal-and-edge",
            "centroid reaches right 80% edge zone and returns to front",
            {
                "edgeX": dm["directional-right-edge"]["centroid"][0],
                "returnX": dm["directional-right-return"]["centroid"][0],
                "returnClassification": self.classify(
                    "directional-right-return", turn_candidates
                ),
            },
            dm["directional-right-edge"]["centroid"][0] > width * 0.80
            and dm["directional-right-return"]["centroid"][0] > width * 0.80
            and self.classify("directional-right-return", turn_candidates)["frame"] == 0,
            self.screen_path("directional-right-return"),
        )

        speed_names = ["speed-min", "speed-min-walk", "speed-max", "speed-max-walk"]
        sm = {name: self.component_metrics(self.image(name)) for name in speed_names}
        min_distance = abs(
            sm["speed-min-walk"]["centroid"][0] - sm["speed-min"]["centroid"][0]
        )
        max_distance = abs(
            sm["speed-max-walk"]["centroid"][0] - sm["speed-max"]["centroid"][0]
        )
        speed_stdout = self.processes["speed-w-noop"]["stdout"]
        self.add(
            "speed.runtime-log-boundaries",
            ["WALK_SPEED multiplier=0.25x movement=60px/s walk_fps=1.5",
             "WALK_SPEED multiplier=3.00x movement=720px/s walk_fps=18"],
            [
                line
                for line in speed_stdout.splitlines()
                if line.startswith("WALK_SPEED")
            ],
            "WALK_SPEED multiplier=0.25x movement=60px/s walk_fps=1.5"
            in speed_stdout
            and "WALK_SPEED multiplier=3.00x movement=720px/s walk_fps=18"
            in speed_stdout,
        )
        self.add(
            "speed.measurable-traversal-difference",
            "maximum-speed displacement is greater than 4x minimum-speed displacement",
            {
                "minimumDistancePixels": round(min_distance, 2),
                "maximumDistancePixels": round(max_distance, 2),
                "ratio": round(max_distance / max(min_distance, 0.001), 2),
            },
            min_distance > 20
            and max_distance > 250
            and max_distance > min_distance * 4,
            self.screen_path("speed-max-walk"),
        )
        w_delta = self.delta(self.image("w-noop-before"), self.image("w-noop-after"))
        self.add(
            "regression.w-is-visual-noop",
            {"changedPixels": 0, "maxChannelDelta": 0},
            w_delta,
            w_delta.get("changedPixels") == 0
            and w_delta.get("maxChannelDelta") == 0,
            self.screen_path("w-noop-after"),
        )

        clap_candidates = [("Clap", "clap", index) for index in range(6)]
        expected_clap = [0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0]
        clap_sample_names = sorted(
            name for name in self.screens if name.startswith("clap-sample-")
        )
        clap_samples = [
            self.classify(name, clap_candidates + turn_candidates)
            for name in clap_sample_names
        ]
        clap_actual: list[dict[str, Any]] = []
        for row in clap_samples:
            if row["group"] != "Clap" or row["score"] < 0.94:
                continue
            if (
                not clap_actual
                or clap_actual[-1]["frame"] != row["frame"]
            ):
                clap_actual.append(row)
        # The front turn pose can be marginally closer to clap_00 after the
        # animation ends. Stop at the required fifteen gesture transitions.
        clap_actual = clap_actual[:15]
        self.add(
            "clap.exact-15-step-asset-order",
            expected_clap,
            clap_actual,
            [row["frame"] for row in clap_actual] == expected_clap
            and all(row["score"] >= 0.94 for row in clap_actual),
            self.screen_path(clap_sample_names[0]),
        )
        self.add(
            "clap.return-and-directional-interruption",
            "returns to front; right input interrupts into directional pose; C is ignored while moving",
            {
                "return": self.classify("clap-return-front", turn_candidates),
                "interrupted": self.classify(
                    "clap-direction-interrupted",
                    turn_candidates
                    + [
                        ("RightWalk", "walk", index)
                        for index in range(6)
                    ],
                ),
                "cWhileMoving": self.classify(
                    "clap-c-ignored-moving",
                    clap_candidates
                    + turn_candidates
                    + [
                        ("RightWalk", "walk", index)
                        for index in range(6)
                    ],
                ),
            },
            self.classify("clap-return-front", turn_candidates)["frame"] == 0
            and self.classify(
                "clap-direction-interrupted",
                turn_candidates
                + [("RightWalk", "walk", index) for index in range(6)],
            )["group"]
            in ("RightTurn", "RightWalk")
            and self.classify(
                "clap-c-ignored-moving",
                clap_candidates
                + turn_candidates
                + [("RightWalk", "walk", index) for index in range(6)],
            )["group"]
            != "Clap",
            self.screen_path("clap-direction-interrupted"),
        )

        cross_candidates = [("CrossArm", "cross", index) for index in range(3)]
        release_candidates = [
            ("CrossArmRelease", "release", index) for index in range(6)
        ]
        entry_actual = [
            self.classify(f"cross-entry-{index:02d}", cross_candidates)
            for index in range(3)
        ]
        release_actual = [
            self.classify(f"cross-release-{index:02d}", release_candidates)
            for index in range(6)
        ]
        self.add(
            "cross.entry-three-frames",
            [0, 1, 2],
            entry_actual,
            [row["frame"] for row in entry_actual] == [0, 1, 2]
            and all(row["score"] >= 0.94 for row in entry_actual),
            self.screen_path("cross-entry-00"),
        )
        held_delta = self.delta(
            self.image("cross-held"), self.image("cross-held-inputs-blocked")
        )
        self.add(
            "cross.held-pose-stable-and-inputs-blocked",
            {"frame": 2, "changedPixels": 0},
            {
                "classification": self.classify("cross-held", cross_candidates),
                "deltaAfterCAndHeldLeft": held_delta,
            },
            self.classify("cross-held", cross_candidates)["frame"] == 2
            and held_delta.get("changedPixels") == 0,
            self.screen_path("cross-held-inputs-blocked"),
        )
        self.add(
            "cross.release-six-frames",
            [0, 1, 2, 3, 4, 5],
            release_actual,
            [row["frame"] for row in release_actual] == [0, 1, 2, 3, 4, 5]
            and all(row["score"] >= 0.94 for row in release_actual),
            self.screen_path("cross-release-00"),
        )
        still = self.component_metrics(self.image("cross-release-arrow-still-held"))
        fresh = self.component_metrics(self.image("cross-arrow-repress-moves"))
        self.add(
            "cross.held-arrow-not-queued-fresh-arrow-moves",
            "release completes at front without queued motion; fresh Left moves",
            {
                "stillClassification": self.classify(
                    "cross-release-arrow-still-held", turn_candidates
                ),
                "stillX": still["centroid"][0],
                "freshX": fresh["centroid"][0],
                "freshClassification": self.classify(
                    "cross-arrow-repress-moves",
                    turn_candidates
                    + [("LeftWalk", "walk", index) for index in range(6)],
                ),
            },
            self.classify("cross-release-arrow-still-held", turn_candidates)[
                "frame"
            ]
            == 0
            and fresh["centroid"][0] < still["centroid"][0] - 25,
            self.screen_path("cross-arrow-repress-moves"),
        )

        observations = self.data.get("observations", {})
        exact_text = "ASCII w/W | العربية | 😀🧪"
        self.add(
            "dialogue.exact-unicode-input-roundtrip",
            exact_text,
            observations.get("dialogueExactClipboard"),
            observations.get("dialogueExactClipboard") == exact_text,
            self.screen_path("dialogue-exact-input"),
        )
        self.add(
            "dialogue.p-is-typeable-while-editing",
            "p (replaces the Ctrl+A selection left by the round-trip check)",
            observations.get("dialoguePTypeableClipboard"),
            observations.get("dialoguePTypeableClipboard") == "p",
            self.screen_path("dialogue-exact-input"),
        )
        empty_to_exact = self.delta(
            self.image("dialogue-empty-input"), self.image("dialogue-exact-input")
        )
        self.add(
            "dialogue.input-panel-and-text-visibly-render",
            "input panel is visible and exact text changes rendered pixels",
            {
                "emptyPanel": self.light_ui_metrics(
                    self.image("dialogue-empty-input"), above_ratio=1.0
                ),
                "changedPixels": empty_to_exact.get("changedPixels"),
                "exactClipboard": observations.get("dialogueExactClipboard"),
            },
            self.light_ui_metrics(
                self.image("dialogue-empty-input"), above_ratio=1.0
            )["present"]
            and empty_to_exact.get("changedPixels", 0) > 1_000
            and observations.get("dialogueExactClipboard") == exact_text,
            self.screen_path("dialogue-exact-input"),
        )
        control_before = self.component_metrics(self.image("dialogue-controls-before"))
        control_after = self.component_metrics(self.image("dialogue-controls-after"))
        control_process = self.processes["dialogue-text-lifecycle"]["stdout"]
        self.add(
            "dialogue.editing-suppresses-character-controls-and-speed",
            "same character pose/location and no WALK_SPEED log",
            {
                "before": {
                    "bbox": control_before["bbox"],
                    "centroid": control_before["centroid"],
                },
                "after": {
                    "bbox": control_after["bbox"],
                    "centroid": control_after["centroid"],
                },
                "walkSpeedLogPresent": "WALK_SPEED" in control_process,
            },
            control_before["bbox"] == control_after["bbox"]
            and abs(
                control_before["centroid"][0] - control_after["centroid"][0]
            )
            < 1
            and "WALK_SPEED" not in control_process,
            self.screen_path("dialogue-controls-after"),
        )
        submitted_bubble = self.light_ui_metrics(
            self.image("dialogue-exact-submitted")
        )
        hidden_bubble = self.light_ui_metrics(self.image("dialogue-hidden-p"))
        whitespace_delta = self.delta(
            self.image("dialogue-exact-submitted"),
            self.image("dialogue-whitespace-submitted"),
        )
        escape_delta = self.delta(
            self.image("dialogue-exact-submitted"),
            self.image("dialogue-cancelled"),
        )
        self.add(
            "dialogue.submit-whitespace-escape-and-hide-semantics",
            "submit shows bubble; whitespace and Escape preserve it; P hides it",
            {
                "submittedBubble": submitted_bubble,
                "whitespaceDelta": whitespace_delta,
                "escapeDelta": escape_delta,
                "hiddenBubble": hidden_bubble,
                "whitespaceClipboard": observations.get(
                    "dialogueWhitespaceClipboard"
                ),
            },
            submitted_bubble["present"]
            and whitespace_delta.get("changedPixels") == 0
            and escape_delta.get("changedPixels") == 0
            and not hidden_bubble["present"],
            self.screen_path("dialogue-hidden-p"),
        )

        value500 = observations.get("dialogue500Clipboard", "")
        value501 = observations.get("dialogue501Clipboard", "")
        scalar_delta = self.delta(
            self.image("dialogue-500-submitted"),
            self.image("dialogue-501-submitted"),
        )
        self.add(
            "dialogue.500-and-501-unicode-scalar-policy",
            {
                "500InputScalars": 500,
                "501InputBoundedScalars": 500,
                "501InputDropsFinalTestTube": True,
                "submittedVisualsEqual": True,
            },
            {
                "500Scalars": len(value500),
                "501Scalars": len(value501),
                "500AllEmoji": value500 == "😀" * 500,
                "501BoundedValueMatches": value501 == "😀" * 500,
                "submittedDelta": scalar_delta,
            },
            value500 == "😀" * 500
            and value501 == "😀" * 500
            and scalar_delta.get("changedPixels") == 0,
            self.screen_path("dialogue-501-submitted"),
        )

        fullscreen_states = {
            name: [
                self.screens[name]["width"],
                self.screens[name]["height"],
            ]
            for name in (
                "dialogue-f11-open-fullscreen",
                "dialogue-alt-enter-open-windowed",
                "dialogue-before-first-escape-fullscreen",
                "dialogue-first-escape-cancel-only",
                "dialogue-second-escape-exits-fullscreen",
            )
        }
        full_a = fullscreen_states["dialogue-f11-open-fullscreen"]
        windowed = fullscreen_states["dialogue-alt-enter-open-windowed"]
        first_escape = fullscreen_states["dialogue-first-escape-cancel-only"]
        second_escape = fullscreen_states["dialogue-second-escape-exits-fullscreen"]
        self.add(
            "fullscreen.alt-enter-closed-dialogue",
            "Alt+Enter enters fullscreen and returns windowed while dialogue is closed",
            {
                "initial": [
                    self.screens["dialogue-alt-enter-closed-windowed"]["width"],
                    self.screens["dialogue-alt-enter-closed-windowed"]["height"],
                ],
                "fullscreen": [
                    self.screens["dialogue-alt-enter-closed-fullscreen"]["width"],
                    self.screens["dialogue-alt-enter-closed-fullscreen"]["height"],
                ],
                "backWindowed": [
                    self.screens["dialogue-alt-enter-closed-back-windowed"]["width"],
                    self.screens["dialogue-alt-enter-closed-back-windowed"]["height"],
                ],
            },
            [
                self.screens["dialogue-alt-enter-closed-windowed"]["width"],
                self.screens["dialogue-alt-enter-closed-windowed"]["height"],
            ]
            == [1280, 720]
            and self.screens["dialogue-alt-enter-closed-fullscreen"]["width"] > 1280
            and [
                self.screens["dialogue-alt-enter-closed-back-windowed"]["width"],
                self.screens["dialogue-alt-enter-closed-back-windowed"]["height"],
            ]
            == [1280, 720],
            self.screen_path("dialogue-alt-enter-closed-fullscreen"),
        )
        self.add(
            "dialogue.f11-and-escape-arbitration",
            "F11 enters fullscreen with editor open, focused, and exact text retained; first Escape cancels editor only; second Escape exits fullscreen",
            {
                "sizes": fullscreen_states,
                "f11Panel": self.light_ui_metrics(
                    self.image("dialogue-f11-open-fullscreen"), above_ratio=1.0
                )["present"],
                "clipboardBeforeFullscreen": observations.get(
                    "dialogueClipboardBeforeFullscreen"
                ),
                "clipboardAfterF11": observations.get(
                    "dialogueClipboardAfterF11"
                ),
                "firstEscapePanel": self.light_ui_metrics(
                    self.image("dialogue-first-escape-cancel-only"),
                    above_ratio=1.0,
                )["present"],
            },
            full_a[0] > 1280
            and first_escape[0] > 1280
            and second_escape == [1280, 720]
            and self.light_ui_metrics(
                self.image("dialogue-f11-open-fullscreen"), above_ratio=1.0
            )["present"]
            and observations.get("dialogueClipboardBeforeFullscreen")
            == "fullscreen w/W 😀"
            and observations.get("dialogueClipboardAfterF11")
            == "fullscreen w/W 😀"
            and not self.light_ui_metrics(
                self.image("dialogue-first-escape-cancel-only"),
                above_ratio=1.0,
            )["present"],
            self.screen_path("dialogue-first-escape-cancel-only"),
        )
        self.add(
            "dialogue.alt-enter-while-editing",
            {
                "expectedTransitions": [
                    [1280, 720],
                    ["fullscreen"],
                    [1280, 720],
                    ["fullscreen"],
                ],
                "dialogueRemainsOpen": True,
                "exactTextRetained": True,
                "lineEditFocusRetained": True,
                "exactlyOneTogglePerChord": True,
            },
            {
                "actualTransitions": observations.get(
                    "dialogueFocusedFullscreenTransitions"
                ),
                "dialogueAltEnterEditingSucceeded": observations.get(
                    "dialogueAltEnterEditingSucceeded"
                ),
                "clipboardAfterF11": observations.get(
                    "dialogueClipboardAfterF11"
                ),
                "clipboardAfterAltEnterToWindowed": observations.get(
                    "dialogueClipboardAfterAltEnterToWindowed"
                ),
                "clipboardAfterAltEnterToFullscreen": observations.get(
                    "dialogueClipboardAfterAltEnterToFullscreen"
                ),
                "inputPanel": self.light_ui_metrics(
                    self.image("dialogue-alt-enter-open-fullscreen"),
                    above_ratio=1.0,
                ),
            },
            observations.get("dialogueFocusedFullscreenTransitions")
            == [
                [1280, 720],
                full_a,
                [1280, 720],
                full_a,
            ]
            and observations.get("dialogueAltEnterEditingSucceeded") is True
            and observations.get("dialogueClipboardAfterF11")
            == "fullscreen w/W 😀"
            and observations.get("dialogueClipboardAfterAltEnterToWindowed")
            == "fullscreen w/W 😀"
            and observations.get("dialogueClipboardAfterAltEnterToFullscreen")
            == "fullscreen w/W 😀"
            and self.light_ui_metrics(
                self.image("dialogue-alt-enter-open-fullscreen"),
                above_ratio=1.0,
            )["present"],
            self.screen_path("dialogue-alt-enter-open-fullscreen"),
        )

        for side in ("left", "right"):
            for mode in ("windowed", "fullscreen"):
                name = f"dialogue-{side}-edge-{mode}"
                image = self.image(name)
                character = self.component_metrics(image)
                bubble = self.light_ui_metrics(image)
                expected_zone = (
                    character["centroidRatio"][0] < 0.20
                    if side == "left"
                    else character["centroidRatio"][0] > 0.80
                )
                bubble_x = bubble.get("centroid", [None])[0]
                tracks_side = (
                    bubble_x is not None
                    and (
                        bubble_x < image.shape[1] * 0.45
                        if side == "left"
                        else bubble_x > image.shape[1] * 0.55
                    )
                )
                self.add(
                    f"dialogue.{side}-edge-bubble-{mode}",
                    "character in calibrated edge zone; bubble inside viewport and tracks character side",
                    {
                        "character": {
                            "bbox": character["bbox"],
                            "centroidRatio": character["centroidRatio"],
                        },
                        "bubble": bubble,
                        "imageSize": [image.shape[1], image.shape[0]],
                    },
                    expected_zone
                    and bubble.get("present", False)
                    and bubble.get("inside", False)
                    and tracks_side,
                    self.screen_path(name),
                )

        for requested in ("1000x800", "1280x720", "1536x864", "1536x960"):
            expected_width, expected_height = map(int, requested.split("x"))
            center_name = f"viewport-{requested}-center"
            edge_name = f"viewport-{requested}-right-dialogue"
            center_row = self.screens[center_name]
            center_image = self.image(center_name)
            edge_image = self.image(edge_name)
            center_character = self.component_metrics(center_image)
            edge_character = self.component_metrics(edge_image)
            bubble = self.light_ui_metrics(edge_image)
            scale = min(expected_width / 1920.0, expected_height / 1080.0) * 1.25
            expected_character_height = 661 * scale
            actual_character_height = center_character["bbox"][3]
            self.add(
                f"viewport.{requested}.scaled-center-and-right-dialogue",
                {
                    "clientSize": [expected_width, expected_height],
                    "centerCharacterHeightApprox": round(expected_character_height, 2),
                    "rightCharacterZoneGreaterThan": 0.80,
                    "bubbleInside": True,
                },
                {
                    "clientSize": [center_row["width"], center_row["height"]],
                    "centerCharacter": {
                        "bbox": center_character["bbox"],
                        "centroidRatio": center_character["centroidRatio"],
                    },
                    "rightCharacter": {
                        "bbox": edge_character["bbox"],
                        "centroidRatio": edge_character["centroidRatio"],
                    },
                    "bubble": bubble,
                },
                [center_row["width"], center_row["height"]]
                == [expected_width, expected_height]
                and abs(
                    center_character["centroid"][0] - expected_width / 2
                )
                < 5
                and abs(actual_character_height - expected_character_height) < 8
                and edge_character["centroidRatio"][0] > 0.80
                and bubble.get("present", False)
                and bubble.get("inside", False),
                self.screen_path(edge_name),
            )

        for process in self.data["processes"]:
            stderr = process["stderr"]
            self.add(
                f"process.{process['name']}.clean-exit",
                {"exitCode": 0, "runtimeErrors": 0},
                {
                    "exitCode": process["exitCode"],
                    "stderr": stderr,
                    "stdoutErrorLines": [
                        line
                        for line in process["stdout"].splitlines()
                        if re.search(r"\b(ERROR|EXCEPTION|FAIL)\b", line, re.I)
                    ],
                },
                process["exitCode"] == 0
                and not stderr.strip()
                and not re.search(
                    r"\b(ERROR|EXCEPTION|FAIL)\b", process["stdout"], re.I
                ),
            )

        sequence_groups = {
            "directional": [
                name for name in directional_names
            ],
            "clap15": [f"clap-step-{index:02d}" for index in range(15)],
            "crossEntry": [f"cross-entry-{index:02d}" for index in range(3)],
            "crossRelease": [f"cross-release-{index:02d}" for index in range(6)],
            "dialogue": [
                name
                for name in self.screens
                if name.startswith("dialogue-")
            ],
        }
        expected_uniques = {
            "directional": 7,
            "clap15": 5,
            "crossEntry": 3,
            "crossRelease": 6,
            "dialogue": 10,
        }
        sequence_groups["clap15"] = clap_sample_names
        for group, names in sequence_groups.items():
            hashes = [self.screens[name]["sha256"] for name in names]
            unique = len(set(hashes))
            self.add(
                f"stale-capture.{group}.unique-hashes",
                {"minimumUnique": expected_uniques[group], "captures": len(names)},
                {
                    "unique": unique,
                    "captures": len(names),
                    "hashCounts": Counter(hashes).most_common(),
                },
                unique >= expected_uniques[group],
            )

        focus_failures = [
            row for row in self.data["focusChecks"] if not row["passed"]
        ]
        self.add(
            "foreground.focus-verification",
            {"failedChecks": 0},
            {
                "checks": len(self.data["focusChecks"]),
                "failedChecks": focus_failures,
            },
            not focus_failures,
        )

    def write(self) -> bool:
        failed = [row for row in self.assertions if not row["passed"]]
        scenario_names = sorted(
            {
                row["name"].split(".", 1)[0]
                for row in self.assertions
            }
        )
        unique_hashes = len(
            {row["sha256"] for row in self.data["screenshots"]}
        )
        current_sha = hashlib.sha256(EXE.read_bytes()).hexdigest().upper()
        result = {
            **self.data,
            "assertions": self.assertions,
            "summary": {
                "status": "PASS" if not failed else "FAIL",
                "scenarioGroups": len(scenario_names),
                "assertions": len(self.assertions),
                "assertionsPassed": len(self.assertions) - len(failed),
                "assertionsFailed": len(failed),
                "screenshots": len(self.data["screenshots"]),
                "uniqueScreenshotHashes": unique_hashes,
                "focusChecks": len(self.data["focusChecks"]),
                "processes": len(self.data["processes"]),
                "releaseSha256After": current_sha,
            },
        }
        RESULTS.write_text(
            json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8"
        )
        lines = [
            "# Corrected Foreground E2E / Visual QA",
            "",
            f"Status: **{result['summary']['status']}**",
            "",
            f"- Assertions: {len(self.assertions)} "
            f"({len(self.assertions) - len(failed)} passed, {len(failed)} failed)",
            f"- Screenshots: {len(self.data['screenshots'])} "
            f"({unique_hashes} unique SHA-256 values)",
            f"- Focus checks: {len(self.data['focusChecks'])}",
            f"- Processes: {len(self.data['processes'])}",
            f"- Release SHA-256 after run: `{current_sha}`",
            "",
            "## Assertions",
            "",
        ]
        for row in self.assertions:
            lines.extend(
                [
                    f"### {'PASS' if row['passed'] else 'FAIL'} — `{row['name']}`",
                    "",
                    f"- Expected: `{json.dumps(row['expected'], ensure_ascii=False)}`",
                    f"- Actual: `{json.dumps(row['actual'], ensure_ascii=False)}`",
                    *(
                        [f"- Evidence: `{row['evidencePath']}` "
                         f"SHA-256 `{row.get('evidenceSha256', '')}`"]
                        if row.get("evidencePath")
                        else []
                    ),
                    "",
                ]
            )
        REPORT.write_text("\n".join(lines), encoding="utf-8")
        print(
            f"FOREGROUND_ASSERTIONS_{result['summary']['status']} "
            f"assertions={len(self.assertions)} passed={len(self.assertions)-len(failed)} "
            f"failed={len(failed)} screenshots={len(self.data['screenshots'])} "
            f"unique_hashes={unique_hashes} processes={len(self.data['processes'])}"
        )
        if failed:
            print(
                "FAILED_ASSERTIONS "
                + json.dumps(
                    [row["name"] for row in failed], ensure_ascii=False
                )
            )
        return not failed


def main() -> int:
    capture_results = (
        Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else CAPTURE_RESULTS
    )
    analyzer = Analyzer(capture_results)
    try:
        analyzer.run()
    except Exception as exception:
        analyzer.add(
            "harness.analysis-completed",
            "analysis completes without exception",
            f"{type(exception).__name__}: {exception}",
            False,
        )
    return 0 if analyzer.write() else 1


if __name__ == "__main__":
    raise SystemExit(main())
