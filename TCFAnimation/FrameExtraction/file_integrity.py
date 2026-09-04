"""Bounded exact-size integrity checks for immutable release inputs."""

from __future__ import annotations

import hashlib
import os
import stat
from pathlib import Path


def _is_reparse_point(path_stat: os.stat_result) -> bool:
    attributes = getattr(path_stat, "st_file_attributes", 0)
    reparse_flag = getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    return bool(attributes & reparse_flag)


def sha256_file(
    path: Path,
    expected_size: int,
    chunk_size: int = 1 << 20,
) -> str:
    """Fail on non-regular/size mismatch, then return uppercase streaming SHA-256."""

    if isinstance(expected_size, bool) or expected_size <= 0:
        raise ValueError("expected_size must be a positive integer.")
    if isinstance(chunk_size, bool) or chunk_size <= 0:
        raise ValueError("chunk_size must be a positive integer.")

    try:
        initial = path.lstat()
    except FileNotFoundError:
        raise FileNotFoundError(f"Missing integrity input: {path}") from None
    if path.is_symlink() or _is_reparse_point(initial):
        raise RuntimeError(f"Integrity input is a link/reparse point: {path}")
    if not stat.S_ISREG(initial.st_mode):
        raise RuntimeError(f"Integrity input is not a regular file: {path}")
    if initial.st_size != expected_size:
        raise RuntimeError(
            f"{path.name} byte size is {initial.st_size}; "
            f"expected {expected_size}."
        )

    digest = hashlib.sha256()
    with path.open("rb") as stream:
        opened = os.fstat(stream.fileno())
        if not stat.S_ISREG(opened.st_mode) or opened.st_size != expected_size:
            raise RuntimeError(
                f"{path.name} changed before integrity hashing."
            )
        remaining = expected_size
        while remaining:
            block = stream.read(min(chunk_size, remaining))
            if not block:
                raise RuntimeError(
                    f"{path.name} ended before {expected_size} bytes."
                )
            digest.update(block)
            remaining -= len(block)
        if stream.read(1):
            raise RuntimeError(
                f"{path.name} exceeds the expected {expected_size} bytes."
            )
        final_open = os.fstat(stream.fileno())
        if final_open.st_size != expected_size:
            raise RuntimeError(
                f"{path.name} changed during integrity hashing."
            )

    final_path = path.lstat()
    if (
        path.is_symlink()
        or _is_reparse_point(final_path)
        or not stat.S_ISREG(final_path.st_mode)
        or final_path.st_size != expected_size
    ):
        raise RuntimeError(f"{path.name} changed after integrity hashing.")
    return digest.hexdigest().upper()
