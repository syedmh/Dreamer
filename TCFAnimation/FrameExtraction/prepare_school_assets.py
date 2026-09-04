"""Prepare deterministic runtime school backgrounds and character overlays."""

from __future__ import annotations

import argparse
import hashlib
import sys
from dataclasses import dataclass
from pathlib import Path

sys.dont_write_bytecode = True

ROOT = Path(__file__).resolve().parents[1]
SUPPORT_PACKAGES = ROOT / ".tools" / "python"
for package_path in (Path(__file__).resolve().parent, SUPPORT_PACKAGES):
    if package_path.is_dir():
        sys.path.insert(0, str(package_path))

import cv2  # type: ignore  # noqa: E402
import numpy as np  # type: ignore  # noqa: E402
from PIL import Image  # type: ignore  # noqa: E402

import validate_release  # noqa: E402
from file_integrity import sha256_file  # noqa: E402


OVERLAY_ROOT = ROOT / "Frames" / "SchoolCharacter"


@dataclass(frozen=True)
class SchoolSource:
    number: int
    sha256: str
    size_bytes: int
    dimensions: tuple[int, int]

    @property
    def source_path(self) -> Path:
        return ROOT / f"School{self.number}.png"

    @property
    def background_path(self) -> Path:
        return (
            ROOT
            / "Frames"
            / "Backgrounds"
            / f"school{self.number}.png"
        )


SCHOOL_SOURCES = (
    SchoolSource(
        1,
        "2FED5C0AD5D5927BF22272634F2E879703436291962A46CBB5CB96C4434A986A",
        1_186_592,
        (1908, 824),
    ),
    SchoolSource(
        2,
        "81468B2FA3E392F0EDDBC6F4FA9C5A961597C99F83ACA7669EAAC9938CC00A4C",
        1_156_503,
        (1536, 1024),
    ),
    SchoolSource(
        3,
        "D231C7EC01DC340313DF1F3E261F5EDDD138118F80DDA935E3599A1895212727",
        1_208_533,
        (1540, 1021),
    ),
    SchoolSource(
        4,
        "036F8ACC3084CF2FFEF366E0E939F7802F082E2D21A4A7A236C5488FAB7C7190",
        1_182_762,
        (1540, 1021),
    ),
    SchoolSource(
        5,
        "5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1",
        1_138_687,
        (1540, 1021),
    ),
    SchoolSource(
        6,
        "5F6CE380A5B9198B7D5B18AAF91258E9EDC57AFD78B11931E9E9ADE4B9AB7AB1",
        1_138_687,
        (1540, 1021),
    ),
)


def png_bytes_from_rgb(rgb: np.ndarray) -> bytes:
    image = Image.fromarray(rgb, mode="RGB")
    from io import BytesIO

    output = BytesIO()
    image.save(output, format="PNG", optimize=False, compress_level=9)
    return output.getvalue()


def png_bytes_from_bgra(bgra: np.ndarray) -> bytes:
    success, encoded = cv2.imencode(
        ".png",
        bgra,
        [cv2.IMWRITE_PNG_COMPRESSION, 9],
    )
    if not success:
        raise RuntimeError("OpenCV could not encode a school character PNG.")
    return bytes(encoded)


def validate_source(source: SchoolSource) -> tuple[np.ndarray, str]:
    path = source.source_path
    if not path.is_file():
        raise FileNotFoundError(f"Missing school source: {path}")

    source_hash = sha256_file(path, source.size_bytes)
    if source_hash != source.sha256:
        raise RuntimeError(
            f"{path.name} SHA-256 is {source_hash}; "
            f"expected {source.sha256}."
        )

    with Image.open(path) as image:
        if (
            image.format != "JPEG"
            or image.mode != "RGB"
            or image.size != source.dimensions
        ):
            raise RuntimeError(
                f"{path.name} is {image.format} {image.mode} "
                f"{image.size}; expected JPEG RGB {source.dimensions}."
            )
        rgb = np.asarray(image, dtype=np.uint8).copy()

    print(
        f"school_source={path.name} format=JPEG mode=RGB "
        f"dimensions={source.dimensions[0]}x{source.dimensions[1]} "
        f"bytes={source.size_bytes} sha256={source_hash}"
    )
    return rgb, source_hash


def expected_outputs() -> dict[Path, bytes]:
    outputs: dict[Path, bytes] = {}
    source_hashes: dict[Path, str] = {}
    for source in SCHOOL_SOURCES:
        rgb, source_hash = validate_source(source)
        outputs[source.background_path] = png_bytes_from_rgb(rgb)
        source_hashes[source.source_path] = source_hash

    regenerated = validate_release.regenerate_in_memory()
    for relative_path, (frame, matte) in regenerated.items():
        overlay = frame.copy()
        overlay[:, :, 3] = (matte != 0).astype(np.uint8) * 255
        suffix = Path(relative_path).relative_to("Frames")
        outputs[OVERLAY_ROOT / suffix] = png_bytes_from_bgra(overlay)

    for source in SCHOOL_SOURCES:
        if (
            sha256_file(source.source_path, source.size_bytes)
            != source_hashes[source.source_path]
        ):
            raise RuntimeError(
                f"{source.source_path.name} changed while preparing "
                "runtime assets."
            )
    return outputs


def write_or_check(outputs: dict[Path, bytes], check: bool) -> None:
    mismatches: list[str] = []
    for path, expected in outputs.items():
        if check:
            if not path.is_file() or path.read_bytes() != expected:
                mismatches.append(path.relative_to(ROOT).as_posix())
            continue

        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(expected)

    if mismatches:
        raise RuntimeError(
            "Prepared school assets differ from deterministic output: "
            + ", ".join(mismatches)
        )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--check",
        action="store_true",
        help="Validate existing outputs without writing files.",
    )
    args = parser.parse_args()

    outputs = expected_outputs()
    write_or_check(outputs, args.check)
    total_bytes = sum(len(payload) for payload in outputs.values())
    digest = hashlib.sha256()
    for path, payload in sorted(
        outputs.items(),
        key=lambda item: item[0].as_posix(),
    ):
        digest.update(path.relative_to(ROOT).as_posix().encode("utf-8"))
        digest.update(b"\0")
        digest.update(payload)
    print(
        f"SCHOOL_ASSET_PREP_PASS mode={'check' if args.check else 'write'} "
        f"outputs={len(outputs)} bytes={total_bytes} "
        f"aggregate_sha256={digest.hexdigest().upper()}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
