from __future__ import annotations

import os
import re
import stat
from pathlib import Path

from .domain import AppError, ErrorCode

_WINDOWS_FORBIDDEN = re.compile(r'[<>:"|?*\x00-\x1f]')
_RESERVED = {
    "CON",
    "PRN",
    "AUX",
    "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
    "COM¹",
    "COM²",
    "COM³",
    "LPT¹",
    "LPT²",
    "LPT³",
}


def _lexists(path: Path) -> bool:
    return os.path.lexists(path)


def is_reparse_or_link(path: Path) -> bool:
    try:
        info = path.stat(follow_symlinks=False)
    except OSError:
        return True
    return path.is_symlink() or bool(
        getattr(info, "st_file_attributes", 0)
        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    )


def reject_reparse_components(path: Path, *, include_leaf: bool = True) -> None:
    """Reject existing symlink/reparse components without resolving through them."""

    absolute = Path(os.path.abspath(path))
    parts = absolute.parts
    if not parts:
        return
    current = Path(parts[0])
    stop = len(parts) if include_leaf else max(1, len(parts) - 1)
    for part in parts[1:stop]:
        current /= part
        if _lexists(current) and is_reparse_or_link(current):
            raise AppError(
                ErrorCode.SOURCE_OUTSIDE_ROOT,
                "A configured or recovered path contains a reparse point.",
            )


def validated_leaf_name(leaf: str, *, suffix: str | None = None) -> str:
    """Validate one Windows-safe direct-child leaf without changing it."""

    device_name = leaf.split(".", 1)[0].upper() if leaf else ""
    if (
        not leaf
        or Path(leaf).is_absolute()
        or Path(leaf).name != leaf
        or "/" in leaf
        or "\\" in leaf
        or _WINDOWS_FORBIDDEN.search(leaf) is not None
        or leaf.endswith((" ", "."))
        or device_name in _RESERVED
        or (suffix is not None and not leaf.lower().endswith(suffix.lower()))
    ):
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Recovered state contains an invalid internal filename.",
        )
    return leaf


def contained_leaf(
    root: Path,
    leaf: str,
    *,
    suffix: str | None = None,
    must_exist: bool = False,
) -> Path:
    """Build a direct child from untrusted durable state without resolving it."""

    validated_leaf_name(leaf, suffix=suffix)
    root_abs = Path(os.path.abspath(root))
    reject_reparse_components(root_abs)
    candidate = root_abs / leaf
    if Path(os.path.abspath(candidate.parent)) != root_abs:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Recovered state escaped the configured internal directory.",
        )
    present = _lexists(candidate)
    if must_exist and not present:
        raise AppError(ErrorCode.STATE_FAILED, "A recovered internal file is missing.")
    if present and is_reparse_or_link(candidate):
        raise AppError(
            ErrorCode.STATE_FAILED,
            "A recovered internal file is a reparse point.",
        )
    return candidate


def private_file_stat(path: Path) -> os.stat_result:
    reject_reparse_components(path)
    info = path.stat(follow_symlinks=False)
    if not stat.S_ISREG(info.st_mode) or info.st_nlink != 1:
        raise AppError(ErrorCode.STATE_FAILED, "An internal state or lock file is not a private regular file.")
    return info
