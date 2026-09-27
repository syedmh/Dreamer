from __future__ import annotations

import math
import re
import time
from dataclasses import dataclass
from enum import Enum
from pathlib import Path
from typing import BinaryIO, Callable, Literal, Protocol

from .redaction import sanitize_text


class ErrorCode(str, Enum):
    CONFIG_INVALID = "CONFIG_INVALID"
    CREDENTIAL_MISSING = "CREDENTIAL_MISSING"
    AUTHENTICATION_FAILED = "AUTHENTICATION_FAILED"
    SOURCE_OUTSIDE_ROOT = "SOURCE_OUTSIDE_ROOT"
    SOURCE_CHANGED = "SOURCE_CHANGED"
    UNSUPPORTED_FILE = "UNSUPPORTED_FILE"
    INVALID_IMAGE = "INVALID_IMAGE"
    IMAGE_LIMIT_EXCEEDED = "IMAGE_LIMIT_EXCEEDED"
    OUTPUT_LIMIT_EXCEEDED = "OUTPUT_LIMIT_EXCEEDED"
    PROVIDER_RETRYABLE = "PROVIDER_RETRYABLE"
    PROVIDER_PERMANENT = "PROVIDER_PERMANENT"
    PROVIDER_AMBIGUOUS = "PROVIDER_AMBIGUOUS"
    OUTPUT_INVALID = "OUTPUT_INVALID"
    PUBLICATION_FAILED = "PUBLICATION_FAILED"
    STATE_FAILED = "STATE_FAILED"
    SHUTDOWN_INTERRUPTED = "SHUTDOWN_INTERRUPTED"


class JobStatus(str, Enum):
    READY = "READY"
    READY_RETRY = "READY_RETRY"
    DISPATCHING = "DISPATCHING"
    RESPONSE_STAGED = "RESPONSE_STAGED"
    OUTPUT_VERIFIED = "OUTPUT_VERIFIED"
    PUBLISHED = "PUBLISHED"
    SUCCEEDED = "SUCCEEDED"
    FAILED = "FAILED"
    AMBIGUOUS = "AMBIGUOUS"


class AttemptState(str, Enum):
    DISPATCHING = "dispatching"
    SUCCEEDED = "succeeded"
    RETRYABLE = "retryable"
    PERMANENT = "permanent"
    AMBIGUOUS = "ambiguous"


class WorkerOutcome(str, Enum):
    SUCCEEDED = "succeeded"
    RETRYABLE = "retryable"
    PERMANENT = "permanent"
    AMBIGUOUS = "ambiguous"


class AppError(Exception):
    """Application error containing only an allowlisted code and safe message."""

    def __init__(self, code: ErrorCode, safe_message: str) -> None:
        self.code = code
        self.safe_message = sanitize_text(safe_message)
        super().__init__(self.safe_message)


class ProviderError(AppError):
    outcome: WorkerOutcome

    def __init__(
        self, code: ErrorCode, safe_message: str, *,
        retry_after_seconds: float | None = None,
    ) -> None:
        self.retry_after_seconds = validate_retry_after(retry_after_seconds)
        super().__init__(code, safe_message)


def validate_retry_after(value: float | None) -> float | None:
    if value is not None and (
        type(value) not in (int, float)
        or not 0 <= value <= 86_400
        or not math.isfinite(value)
    ):
        raise ValueError("retry_after_seconds must be finite and between 0 and 86400")
    return value


class RetryableProviderError(ProviderError):
    outcome = WorkerOutcome.RETRYABLE


class PermanentProviderError(ProviderError):
    outcome = WorkerOutcome.PERMANENT


class AmbiguousProviderError(ProviderError):
    outcome = WorkerOutcome.AMBIGUOUS


@dataclass(frozen=True)
class SourceSnapshot:
    path: Path
    normalized_path: str
    size: int
    mtime_ns: int
    sha256: str
    staged_path: Path


@dataclass(frozen=True)
class TransformRequest:
    job_id: str
    source: SourceSnapshot
    prompt: str
    model: str
    output_format: Literal["png"] = "png"
    max_output_bytes: int = 52_428_800
    max_input_bytes: int = 52_428_800
    max_width: int = 7680
    max_height: int = 7680
    max_pixels: int = 40_000_000
    variant: str = ""
    azure_response_retries: bool = False


@dataclass(frozen=True)
class ProviderResult:
    provider_request_id: str | None
    output_size: int
    media_type: str


class ImageProvider(Protocol):
    def transform(
        self,
        request: TransformRequest,
        output: BinaryIO,
    ) -> ProviderResult: ...


@dataclass(frozen=True)
class RequestIdentity:
    provider: str
    model: str
    prompt_hash: str
    variant: str = ""


@dataclass(frozen=True)
class AdmissionResult:
    job_id: str
    admitted: bool
    status: JobStatus


@dataclass(frozen=True)
class ImageInfo:
    format: str
    width: int
    height: int
    size_bytes: int


@dataclass(frozen=True)
class StableCandidate:
    path: Path
    size: int
    mtime_ns: int


@dataclass(frozen=True)
class FailureRecord:
    job_id: str
    source_name: str
    size: int
    mtime_ns: int
    sha256: str
    stage: str
    error_code: ErrorCode
    safe_message: str
    attempts: int
    first_seen_at: str
    failed_at: str
    variant: str = ""


def valid_variant(value: object) -> bool:
    return (
        type(value) is str
        and len(value) <= 32
        and re.fullmatch(r"[a-z][a-z0-9]*(?:-[a-z0-9]+)*", value) is not None
        and value.upper() not in {
            "CON", "PRN", "AUX", "NUL",
            *(f"COM{i}" for i in range(1, 10)),
            *(f"LPT{i}" for i in range(1, 10)),
        }
    )


@dataclass(frozen=True)
class ShutdownToken:
    is_set: Callable[[], bool]
    wait: Callable[[float], bool] | None = None

    def requested(self) -> bool:
        return bool(self.is_set())

    def wait_for(self, timeout_seconds: float) -> bool:
        timeout = max(0.0, float(timeout_seconds))
        if self.wait is not None:
            return bool(self.wait(timeout))
        if timeout == 0:
            return self.requested()
        time.sleep(timeout)
        return self.requested()
