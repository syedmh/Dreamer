from __future__ import annotations

import json
import logging
import os
import re
import stat
import sys
from datetime import UTC, datetime
from logging.handlers import RotatingFileHandler
from pathlib import Path

from .config import LoggingConfig
from .domain import AppError, ErrorCode
from .path_safety import contained_leaf, reject_reparse_components
from .redaction import sanitize_text

_FIELDS = (
    "event",
    "job_id",
    "source_name",
    "source_sha12",
    "provider",
    "model",
    "attempt_no",
    "duration_ms",
    "result",
    "error_code",
    "message",
    "pending_count",
    "output_name",
    "max_attempts",
    "variant",
)
_LOG_BACKUP = re.compile(r"^tcfcomic\.jsonl\.([1-9][0-9]*)$")


def _safe_log_value(value: object) -> object:
    if type(value) in (bool, int, float):
        return value
    return sanitize_text(value)


class JsonFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "timestamp": datetime.now(UTC).isoformat(),
            "level": record.levelname,
        }
        for field in _FIELDS:
            value = (
                getattr(record, "safe_message", None)
                if field == "message"
                else getattr(record, field, None)
            )
            if value is not None:
                payload[field] = _safe_log_value(value)
        if "message" not in payload and record.getMessage():
            payload["message"] = sanitize_text(record.getMessage())
        return json.dumps(payload, sort_keys=True, separators=(",", ":"))


class TextFormatter(JsonFormatter):
    def format(self, record: logging.LogRecord) -> str:
        payload = json.loads(super().format(record))
        message = payload.get("message") or payload.get("event", "")
        variant = f" [{payload['variant']}]" if payload.get("variant") else ""
        return f"{payload['timestamp']} {payload['level']}{variant} {message}"


def configure_logging(config: LoggingConfig, destination: Path) -> logging.Logger:
    logger = logging.getLogger("tcfcomic")
    for handler in tuple(logger.handlers):
        logger.removeHandler(handler)
        handler.close()

    logs = destination / ".tcfcomic" / "logs"
    if logs.exists():
        reject_reparse_components(logs)
    logs.mkdir(parents=True, exist_ok=True)
    reject_reparse_components(logs)
    log_path = contained_leaf(logs, "tcfcomic.jsonl")

    try:
        entries = list(os.scandir(logs))
    except OSError:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Log backups could not be enumerated safely.",
        ) from None
    logs_abs = Path(os.path.abspath(logs))
    for entry in entries:
        match = _LOG_BACKUP.fullmatch(entry.name)
        if match is None or int(match.group(1)) <= config.backup_count:
            continue
        candidate = Path(entry.path)
        if Path(os.path.abspath(candidate.parent)) != logs_abs:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A log backup escaped its configured directory.",
            )
        try:
            info = entry.stat(follow_symlinks=False)
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A stale log backup could not be inspected safely.",
            ) from None
        is_reparse = bool(
            getattr(info, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )
        if not stat.S_ISREG(info.st_mode) or is_reparse or entry.is_symlink():
            continue
        try:
            safe = contained_leaf(logs, entry.name, must_exist=True)
            reject_reparse_components(logs)
            safe.unlink()
        except FileNotFoundError:
            continue
        except AppError:
            raise
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A stale log backup could not be removed.",
            ) from None

    logger.setLevel(getattr(logging, config.level))
    logger.propagate = False
    formatter = JsonFormatter()

    console = logging.StreamHandler(sys.stderr)
    console.setFormatter(TextFormatter() if config.console_format == "text" else formatter)
    logger.addHandler(console)

    file_handler = RotatingFileHandler(
        log_path,
        maxBytes=config.rotate_bytes,
        backupCount=config.backup_count,
        encoding="utf-8",
    )
    file_handler.setFormatter(formatter)
    logger.addHandler(file_handler)
    return logger


def log_event(logger: logging.Logger, level: int, event: str, **fields: object) -> None:
    safe = {
        key: _safe_log_value(value)
        for key, value in fields.items()
        if key in _FIELDS and value is not None
    }
    message = safe.pop("message", None)
    extra = {"event": sanitize_text(event), **safe}
    if message is not None:
        extra["safe_message"] = message
    logger.log(level, "", extra=extra)
