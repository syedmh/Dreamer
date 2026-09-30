from __future__ import annotations

import csv
import hashlib
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont


PROJECT = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation")
OUT = Path(
    r"C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions"
    r"\2026-09-04-tcfanimation-release-quality-cecc239c\evidence\qa"
)

GROUPS = {
    "LeftTurn": [f"turn_{i}.png" for i in range(3)],
    "RightTurn": [f"turn_{i}.png" for i in range(3)],
    "LeftWalk": [f"walk_{i:02d}.png" for i in range(6)],
    "RightWalk": [f"walk_{i:02d}.png" for i in range(6)],
    "Clap": [f"clap_{i:02d}.png" for i in range(6)],
    "CrossArm": [f"cross_{i:02d}.png" for i in range(3)],
    "CrossArmRelease": [f"release_{i:02d}.png" for i in range(6)],
}

CLAP_SEQUENCE = [0, 1, 2, 3, 4, 3, 2, 1, 2, 3, 4, 3, 2, 1, 0]


def load(group: str, name: str) -> Image.Image:
    return Image.open(PROJECT / "Frames" / group / name).convert("RGBA")


def mask_for(image: Image.Image) -> np.ndarray:
    data = np.asarray(image)
    return np.any(data[:, :, :3] != 0, axis=2)


def metrics(group: str, name: str, image: Image.Image) -> dict[str, object]:
    data = np.asarray(image)
    mask = mask_for(image)
    ys, xs = np.where(mask)
    x0, x1 = int(xs.min()), int(xs.max())
    y0, y1 = int(ys.min()), int(ys.max())
    height = y1 - y0 + 1

    head_limit = y0 + max(1, round(height * 0.20))
    head_ys, head_xs = np.where(mask & (np.indices(mask.shape)[0] <= head_limit))
    torso_top = y0 + round(height * 0.35)
    torso_bottom = y0 + round(height * 0.62)
    rows = np.indices(mask.shape)[0]
    torso_ys, torso_xs = np.where(
        mask & (rows >= torso_top) & (rows <= torso_bottom)
    )
    foot_band = mask[max(y0, y1 - 7) : y1 + 1]
    foot_ys, foot_xs = np.where(foot_band)

    alpha = data[:, :, 3]
    nonblack_pixels = int(mask.sum())
    return {
        "group": group,
        "frame": name,
        "sha256": hashlib.sha256(
            (PROJECT / "Frames" / group / name).read_bytes()
        ).hexdigest().upper(),
        "width": image.width,
        "height": image.height,
        "alpha_min": int(alpha.min()),
        "alpha_max": int(alpha.max()),
        "nonblack_pixels": nonblack_pixels,
        "black_pixels": int(mask.size - nonblack_pixels),
        "bbox_left": x0,
        "bbox_top": y0,
        "bbox_right": x1,
        "bbox_bottom": y1,
        "bbox_width": x1 - x0 + 1,
        "bbox_height": height,
        "head_anchor_x": round(float(head_xs.mean()), 2),
        "head_anchor_y": round(float(head_ys.mean()), 2),
        "torso_anchor_x": round(float(torso_xs.mean()), 2),
        "torso_anchor_y": round(float(torso_ys.mean()), 2),
        "lowest_foot_y": y1,
        "lowest_foot_x": round(float(foot_xs.mean()), 2),
    }


def annotate(image: Image.Image, row: dict[str, object]) -> Image.Image:
    result = image.copy()
    draw = ImageDraw.Draw(result)
    box = (
        int(row["bbox_left"]),
        int(row["bbox_top"]),
        int(row["bbox_right"]),
        int(row["bbox_bottom"]),
    )
    draw.rectangle(box, outline=(255, 0, 255, 255), width=2)
    for x_key, y_key, color in (
        ("head_anchor_x", "head_anchor_y", (255, 255, 0, 255)),
        ("torso_anchor_x", "torso_anchor_y", (0, 255, 255, 255)),
        ("lowest_foot_x", "lowest_foot_y", (255, 80, 80, 255)),
    ):
        x, y = float(row[x_key]), float(row[y_key])
        draw.line((x - 8, y, x + 8, y), fill=color, width=2)
        draw.line((x, y - 8, x, y + 8), fill=color, width=2)
    return result


def labeled_tile(
    image: Image.Image, label: str, width: int = 512, height: int = 904
) -> Image.Image:
    tile = Image.new("RGB", (width, height), (38, 38, 38))
    tile.paste(image.convert("RGB"), (0, 40))
    draw = ImageDraw.Draw(tile)
    draw.text((8, 12), label, fill=(255, 255, 255), font=ImageFont.load_default())
    return tile


def sheet(tiles: list[Image.Image], columns: int, path: Path) -> None:
    rows = (len(tiles) + columns - 1) // columns
    width = max(tile.width for tile in tiles)
    height = max(tile.height for tile in tiles)
    canvas = Image.new("RGB", (columns * width, rows * height), (80, 80, 80))
    for index, tile in enumerate(tiles):
        x = (index % columns) * width
        y = (index // columns) * height
        canvas.paste(tile, (x, y))
    path.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(path)


def zoom_tile(image: Image.Image, label: str) -> Image.Image:
    mask = mask_for(image)
    ys, xs = np.where(mask)
    margin = 8
    x0 = max(0, int(xs.min()) - margin)
    y0 = max(0, int(ys.min()) - margin)
    x1 = min(image.width, int(xs.max()) + margin + 1)
    y1 = min(image.height, int(ys.max()) + margin + 1)
    crop = image.crop((x0, y0, x1, y1)).resize(
        ((x1 - x0) * 2, (y1 - y0) * 2), Image.Resampling.NEAREST
    )
    tile = Image.new("RGB", (max(700, crop.width), crop.height + 46), (38, 38, 38))
    tile.paste(crop.convert("RGB"), ((tile.width - crop.width) // 2, 46))
    ImageDraw.Draw(tile).text(
        (8, 14),
        f"{label} crop=({x0},{y0})-({x1 - 1},{y1 - 1}) 2x nearest",
        fill=(255, 255, 255),
        font=ImageFont.load_default(),
    )
    return tile


def boundary_sheet(
    name: str, entries: list[tuple[str, str, str]], columns: int = 4
) -> None:
    tiles = [
        labeled_tile(load(group, frame), label)
        for label, group, frame in entries
    ]
    sheet(tiles, columns, OUT / "boundaries" / f"{name}.png")


def delta(a: dict[str, object], b: dict[str, object], label: str) -> dict[str, object]:
    return {
        "boundary": label,
        "from": f"{a['group']}/{a['frame']}",
        "to": f"{b['group']}/{b['frame']}",
        "bbox_height_delta": int(b["bbox_height"]) - int(a["bbox_height"]),
        "bbox_top_delta": int(b["bbox_top"]) - int(a["bbox_top"]),
        "bbox_bottom_delta": int(b["bbox_bottom"]) - int(a["bbox_bottom"]),
        "head_dx": round(float(b["head_anchor_x"]) - float(a["head_anchor_x"]), 2),
        "head_dy": round(float(b["head_anchor_y"]) - float(a["head_anchor_y"]), 2),
        "torso_dx": round(
            float(b["torso_anchor_x"]) - float(a["torso_anchor_x"]), 2
        ),
        "torso_dy": round(
            float(b["torso_anchor_y"]) - float(a["torso_anchor_y"]), 2
        ),
        "foot_dx": round(float(b["lowest_foot_x"]) - float(a["lowest_foot_x"]), 2),
        "foot_dy": int(b["lowest_foot_y"]) - int(a["lowest_foot_y"]),
    }


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    rows: list[dict[str, object]] = []
    lookup: dict[tuple[str, str], dict[str, object]] = {}

    for group, names in GROUPS.items():
        native_tiles: list[Image.Image] = []
        zoom_tiles: list[Image.Image] = []
        foot_tiles: list[Image.Image] = []
        for name in names:
            image = load(group, name)
            row = metrics(group, name, image)
            rows.append(row)
            lookup[(group, name)] = row
            annotated = annotate(image, row)
            annotated_path = OUT / "frames-annotated" / group / name
            annotated_path.parent.mkdir(parents=True, exist_ok=True)
            annotated.save(annotated_path)
            native_tiles.append(labeled_tile(annotated, f"{group}/{name}"))
            zoom_tiles.append(zoom_tile(image, f"{group}/{name}"))
            foot_top = max(0, int(row["bbox_bottom"]) - 190)
            foot_crop = image.crop((0, foot_top, 512, 864)).resize(
                (1024, (864 - foot_top) * 2),
                Image.Resampling.NEAREST,
            )
            foot_tile = Image.new(
                "RGB", (1024, foot_crop.height + 46), (38, 38, 38)
            )
            foot_tile.paste(foot_crop.convert("RGB"), (0, 46))
            ImageDraw.Draw(foot_tile).text(
                (8, 14),
                f"{group}/{name} lower crop y={foot_top}..863 at 2x",
                fill=(255, 255, 255),
                font=ImageFont.load_default(),
            )
            foot_tiles.append(foot_tile)
        sheet(native_tiles, 3, OUT / "contact-sheets-native" / f"{group}.png")
        sheet(zoom_tiles, 2, OUT / "contact-sheets-zoom" / f"{group}.png")
        sheet(foot_tiles, 2, OUT / "contact-sheets-feet" / f"{group}.png")

    with (OUT / "frame-metrics.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)
    (OUT / "frame-metrics.json").write_text(
        json.dumps(rows, indent=2), encoding="utf-8"
    )

    deltas: list[dict[str, object]] = []

    def add_boundary(
        label: str, left: tuple[str, str], right: tuple[str, str]
    ) -> None:
        deltas.append(delta(lookup[left], lookup[right], label))

    for side in ("Left", "Right"):
        add_boundary(
            f"{side} front_to_turn1",
            (f"{side}Turn", "turn_0.png"),
            (f"{side}Turn", "turn_1.png"),
        )
        add_boundary(
            f"{side} turn1_to_turn2",
            (f"{side}Turn", "turn_1.png"),
            (f"{side}Turn", "turn_2.png"),
        )
        add_boundary(
            f"{side} turn2_to_walk0",
            (f"{side}Turn", "turn_2.png"),
            (f"{side}Walk", "walk_00.png"),
        )
        for i in range(6):
            add_boundary(
                f"{side} walk{i:02d}_to_{(i + 1) % 6:02d}",
                (f"{side}Walk", f"walk_{i:02d}.png"),
                (f"{side}Walk", f"walk_{(i + 1) % 6:02d}.png"),
            )

    add_boundary(
        "front_handoff_left_to_right",
        ("LeftTurn", "turn_0.png"),
        ("RightTurn", "turn_0.png"),
    )
    add_boundary(
        "front_to_clap00",
        ("LeftTurn", "turn_0.png"),
        ("Clap", "clap_00.png"),
    )
    for i, (a, b) in enumerate(zip(CLAP_SEQUENCE, CLAP_SEQUENCE[1:])):
        add_boundary(
            f"clap_step_{i:02d}_{a}_to_{b}",
            ("Clap", f"clap_{a:02d}.png"),
            ("Clap", f"clap_{b:02d}.png"),
        )
    add_boundary(
        "clap_final_to_front",
        ("Clap", "clap_00.png"),
        ("LeftTurn", "turn_0.png"),
    )
    add_boundary(
        "front_to_cross00",
        ("LeftTurn", "turn_0.png"),
        ("CrossArm", "cross_00.png"),
    )
    add_boundary(
        "cross00_to_cross01",
        ("CrossArm", "cross_00.png"),
        ("CrossArm", "cross_01.png"),
    )
    add_boundary(
        "cross01_to_cross02",
        ("CrossArm", "cross_01.png"),
        ("CrossArm", "cross_02.png"),
    )
    add_boundary(
        "cross02_to_release00",
        ("CrossArm", "cross_02.png"),
        ("CrossArmRelease", "release_00.png"),
    )
    for i in range(5):
        add_boundary(
            f"release{i:02d}_to_{i + 1:02d}",
            ("CrossArmRelease", f"release_{i:02d}.png"),
            ("CrossArmRelease", f"release_{i + 1:02d}.png"),
        )
    add_boundary(
        "release05_to_front",
        ("CrossArmRelease", "release_05.png"),
        ("LeftTurn", "turn_0.png"),
    )

    with (OUT / "boundary-deltas.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(deltas[0].keys()))
        writer.writeheader()
        writer.writerows(deltas)
    (OUT / "boundary-deltas.json").write_text(
        json.dumps(deltas, indent=2), encoding="utf-8"
    )

    boundary_sheet(
        "left-directional",
        [
            ("front", "LeftTurn", "turn_0.png"),
            ("turn 45", "LeftTurn", "turn_1.png"),
            ("turn 90", "LeftTurn", "turn_2.png"),
            *[
                (f"walk {i}", "LeftWalk", f"walk_{i:02d}.png")
                for i in range(6)
            ],
            ("walk loop 0", "LeftWalk", "walk_00.png"),
            ("return 90", "LeftTurn", "turn_2.png"),
            ("return 45", "LeftTurn", "turn_1.png"),
            ("front", "LeftTurn", "turn_0.png"),
        ],
    )
    boundary_sheet(
        "right-directional",
        [
            ("front", "RightTurn", "turn_0.png"),
            ("turn 45", "RightTurn", "turn_1.png"),
            ("turn 90", "RightTurn", "turn_2.png"),
            *[
                (f"walk {i}", "RightWalk", f"walk_{i:02d}.png")
                for i in range(6)
            ],
            ("walk loop 0", "RightWalk", "walk_00.png"),
            ("return 90", "RightTurn", "turn_2.png"),
            ("return 45", "RightTurn", "turn_1.png"),
            ("front", "RightTurn", "turn_0.png"),
        ],
    )
    boundary_sheet(
        "front-handoff",
        [
            ("Left front", "LeftTurn", "turn_0.png"),
            ("Right front", "RightTurn", "turn_0.png"),
            ("Left front", "LeftTurn", "turn_0.png"),
            ("Right front", "RightTurn", "turn_0.png"),
        ],
        columns=2,
    )
    boundary_sheet(
        "clap-15-step",
        [
            ("front entry", "LeftTurn", "turn_0.png"),
            *[
                (f"step {i:02d} = {frame}", "Clap", f"clap_{frame:02d}.png")
                for i, frame in enumerate(CLAP_SEQUENCE)
            ],
            ("front return", "LeftTurn", "turn_0.png"),
        ],
    )
    boundary_sheet(
        "cross-hold-release",
        [
            ("front entry", "LeftTurn", "turn_0.png"),
            ("cross 0", "CrossArm", "cross_00.png"),
            ("cross 1", "CrossArm", "cross_01.png"),
            ("cross 2 hold", "CrossArm", "cross_02.png"),
            ("cross 2 held", "CrossArm", "cross_02.png"),
            *[
                (f"release {i}", "CrossArmRelease", f"release_{i:02d}.png")
                for i in range(6)
            ],
            ("front return", "LeftTurn", "turn_0.png"),
        ],
    )

    mirror_rows = []
    mirror_tiles: list[Image.Image] = []
    for i in range(6):
        left = np.asarray(load("LeftWalk", f"walk_{i:02d}.png"))
        right = np.asarray(load("RightWalk", f"walk_{i:02d}.png"))
        expected = np.flip(right, axis=1)
        diff = np.abs(left.astype(np.int16) - expected.astype(np.int16))
        mismatch = int(np.any(diff != 0, axis=2).sum())
        max_delta = int(diff.max())
        mirror_rows.append(
            {"frame": i, "mismatch_pixels": mismatch, "max_channel_delta": max_delta}
        )
        diff_image = Image.fromarray(
            np.where(np.any(diff != 0, axis=2), 255, 0).astype(np.uint8),
            mode="L",
        )
        mirror_tiles.extend(
            [
                labeled_tile(Image.fromarray(left), f"LeftWalk/{i:02d}"),
                labeled_tile(
                    Image.fromarray(expected), f"mirror(RightWalk/{i:02d})"
                ),
                labeled_tile(diff_image.convert("RGBA"), f"difference/{i:02d}"),
            ]
        )
    sheet(mirror_tiles, 3, OUT / "boundaries" / "walk-mirror-proof.png")
    (OUT / "walk-mirror-results.json").write_text(
        json.dumps(mirror_rows, indent=2), encoding="utf-8"
    )

    summary = {
        "frames": len(rows),
        "groups": {group: len(names) for group, names in GROUPS.items()},
        "all_dimensions_512x864": all(
            row["width"] == 512 and row["height"] == 864 for row in rows
        ),
        "all_opaque": all(
            row["alpha_min"] == 255 and row["alpha_max"] == 255 for row in rows
        ),
        "walk_mirror_mismatch_pixels": sum(
            row["mismatch_pixels"] for row in mirror_rows
        ),
        "boundary_count": len(deltas),
    }
    (OUT / "visual-summary.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8"
    )
    print(
        "VISUAL_EVIDENCE_GENERATED "
        f"frames={summary['frames']} groups={len(GROUPS)} "
        f"boundaries={summary['boundary_count']} "
        f"mirror_mismatches={summary['walk_mirror_mismatch_pixels']}"
    )


if __name__ == "__main__":
    main()
