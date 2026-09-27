from __future__ import annotations

import os
import re
from contextlib import contextmanager
from threading import RLock

_secrets: dict[str, int] = {}
_secrets_lock = RLock()
_REQUEST_ID = re.compile(
    r"(?:[0-9a-fA-F]{32}|"
    r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|"
    r"req_[0-9a-fA-F]{32})"
)


def validated_request_id(value: object) -> str | None:
    """Omit invalid metadata; never turn provider prose into an identifier."""
    if type(value) is str and len(value) <= 36 and _REQUEST_ID.fullmatch(value):
        return value if sanitize_text(value) == value else None
    return None


@contextmanager
def redact_secret(secret: str):
    """Register a secret only while its owning operation/session needs it."""
    with _secrets_lock:
        _secrets[secret] = _secrets.get(secret, 0) + 1
    try:
        yield
    finally:
        with _secrets_lock:
            if _secrets[secret] == 1:
                del _secrets[secret]
            else:
                _secrets[secret] -= 1

_CREDENTIAL_PATTERNS = (
    re.compile(r"\bsk-[A-Za-z0-9_-]{6,}\b", re.IGNORECASE),
    re.compile(
        r"(?i)\b(api[_-]?key|authorization|bearer|token|secret|password)\b"
        r"\s*[:=]\s*[^\s,;]+"
    ),
)
_BASE64_PATTERN = re.compile(r"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{80,}={0,2}(?![A-Za-z0-9+/])")
_CONTROL_PATTERN = re.compile(r"[\x00-\x1f\x7f]")


def sanitize_text(value: object, *, limit: int = 500) -> str:
    """Return bounded text safe for logs and durable diagnostic artifacts."""

    text = str(value)
    keys = set()
    with _secrets_lock:
        keys.update(_secrets)
    for name in ("OPENAI_API_KEY", "AZURE_OPENAI_API_KEY"):
        raw = os.environ.get(name, "")
        keys.update(value for value in (raw, raw.strip()) if value)
    for key in sorted(keys, key=len, reverse=True):
        text = text.replace(key, "[REDACTED]")
    for pattern in _CREDENTIAL_PATTERNS:
        text = pattern.sub("[REDACTED]", text)
    text = _BASE64_PATTERN.sub("[REDACTED_PAYLOAD]", text)
    text = _CONTROL_PATTERN.sub("?", text)
    return text[:limit]
