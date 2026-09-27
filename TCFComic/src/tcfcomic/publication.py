from __future__ import annotations

import hashlib
import os
import re
import uuid
from pathlib import Path

from .domain import AppError, ErrorCode, SourceSnapshot, valid_variant
from .path_safety import contained_leaf

_INVALID = re.compile(r'[<>:"/\\|?*\x00-\x1f]')
_RESERVED = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}


def stream_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def safe_source_stem(source_name: str) -> str:
    stem = Path(source_name).stem
    stem = _INVALID.sub("_", stem).rstrip(" .")
    if not stem or stem.upper() in _RESERVED:
        stem = "image"
    return stem[:80].rstrip(" .") or "image"


def initial_output_leaf(snapshot: SourceSnapshot, job_id: str, variant: str = "") -> str:
    if variant and not valid_variant(variant):
        raise AppError(ErrorCode.STATE_FAILED, "Invalid output prompt variant.")
    suffix = f"__{variant}" if variant else ""
    return f"{safe_source_stem(snapshot.path.name)}__{snapshot.sha256[:12]}__{job_id[:8]}{suffix}.png"


def rename_no_overwrite(temp_path: Path, final_path: Path) -> None:
    if Path(os.path.abspath(final_path.parent)) != Path(os.path.abspath(temp_path.parent)):
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "Temporary and final output must share the destination directory.",
        )
    if final_path.exists():
        raise FileExistsError(str(final_path))
    try:
        os.rename(temp_path, final_path)
    except FileExistsError:
        raise
    except OSError:
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The validated output could not be published.",
        ) from None


def prepare_publication(
    temp_path: Path,
    destination: Path,
    snapshot: SourceSnapshot,
    job_id: str,
    variant: str = "",
) -> tuple[Path, str]:
    """Choose a collision-free leaf and hash the validated temp before rename."""

    safe_temp = contained_leaf(destination, temp_path.name, suffix=".tmp", must_exist=True)
    if safe_temp != Path(os.path.abspath(temp_path)):
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The temporary output is outside the configured destination.",
        )
    try:
        digest = stream_sha256(safe_temp)
    except OSError:
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The validated temporary output cannot be read.",
        ) from None

    base = initial_output_leaf(snapshot, job_id, variant)
    for index in range(100):
        leaf = base if index == 0 else base[:-4] + f"__{uuid.uuid4().hex[:8]}.png"
        final_path = contained_leaf(destination, leaf, suffix=".png")
        if not final_path.exists():
            return final_path, digest
    raise AppError(
        ErrorCode.PUBLICATION_FAILED,
        "A unique destination filename could not be allocated.",
    )


def promote_planned_output(
    temp_path: Path,
    final_path: Path,
    expected_sha256: str,
) -> Path:
    """Promote the durably planned output and verify the final artifact."""

    try:
        digest = stream_sha256(temp_path)
    except OSError:
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The validated temporary output cannot be read.",
        ) from None
    if digest != expected_sha256:
        raise AppError(
            ErrorCode.OUTPUT_INVALID,
            "The validated temporary output does not match durable state.",
        )
    rename_no_overwrite(temp_path, final_path)
    try:
        final_digest = stream_sha256(final_path)
    except OSError:
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "The published output cannot be verified.",
        ) from None
    if final_digest != expected_sha256:
        raise AppError(
            ErrorCode.OUTPUT_INVALID,
            "The published output does not match durable state.",
        )
    return final_path
