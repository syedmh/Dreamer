from __future__ import annotations

import multiprocessing
import logging
import os
import queue
import signal
import time
from dataclasses import dataclass
from contextlib import nullcontext
from pathlib import Path
from threading import Lock
from typing import Callable, Literal, Protocol

from ..config import ProviderConfig
from ..authentication import AccessToken
from ..redaction import redact_secret, sanitize_text, validated_request_id
from ..domain import (
    ErrorCode,
    ImageProvider,
    PermanentProviderError,
    ProviderError,
    TransformRequest,
    WorkerOutcome,
    validate_retry_after,
)
from .fake import FakeProvider
from .openai import OpenAIProvider


@dataclass(frozen=True)
class WorkerResult:
    outcome: Literal["succeeded", "retryable", "permanent", "ambiguous"]
    provider_request_id: str | None = None
    output_size: int | None = None
    media_type: str | None = None
    error_code: ErrorCode | None = None
    safe_message: str | None = None
    retry_after_seconds: float | None = None

    def __post_init__(self) -> None:
        validate_retry_after(self.retry_after_seconds)


class ShutdownDeadline:
    """Lazily derives one monotonic deadline when shutdown is first requested."""

    def __init__(self, is_requested: Callable[[], bool], timeout_seconds: float) -> None:
        self._is_requested = is_requested
        self._timeout_seconds = timeout_seconds
        self._deadline: float | None = None
        self._lock = Lock()

    def current(self) -> float | None:
        if self._deadline is not None:
            return self._deadline
        if not self._is_requested():
            return None
        with self._lock:
            if self._deadline is None:
                self._deadline = time.monotonic() + self._timeout_seconds
        return self._deadline


class AttemptRunner(Protocol):
    def run(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
    ) -> WorkerResult: ...

    def run_authenticated(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
        access_token: AccessToken,
    ) -> WorkerResult: ...


def _provider_from_config(
    config: ProviderConfig, access_token: AccessToken | None = None, *,
    azure_response_retries: bool = False,
) -> ImageProvider:
    if config.authentication == "interactive":
        if config.name != "azure_openai" or access_token is None:
            raise PermanentProviderError(
                ErrorCode.AUTHENTICATION_FAILED,
                "Interactive authentication requires a signed-in Azure session.",
            )
    elif access_token is not None or config.authentication != "api_key":
        raise PermanentProviderError(
            ErrorCode.AUTHENTICATION_FAILED, "The provider authentication mode is invalid."
        )
    if config.name != "azure_openai" and config.endpoint is not None:
        raise PermanentProviderError(
            ErrorCode.PROVIDER_PERMANENT,
            "An endpoint is only supported for the Azure OpenAI provider.",
        )
    if config.name == "fake":
        return FakeProvider()
    if config.name == "openai":
        return OpenAIProvider(config.request_timeout_seconds)
    if config.name == "azure_openai":
        if config.endpoint is None or not config.model.strip():
            raise PermanentProviderError(
                ErrorCode.PROVIDER_PERMANENT,
                "The Azure OpenAI provider requires an endpoint and deployment name.",
            )
        return OpenAIProvider(
            config.request_timeout_seconds, azure_endpoint=config.endpoint,
            **({"access_token": access_token} if access_token is not None else {}),
            **({"azure_response_retries": True} if azure_response_retries else {}),
        )
    raise PermanentProviderError(
        ErrorCode.PROVIDER_PERMANENT, "The configured provider is not supported."
    )


class _BoundedOutput:
    def __init__(self, output, limit: int) -> None:
        self._output = output
        self._limit = limit
        self._written = 0

    def write(self, payload: bytes) -> int:
        if self._written + len(payload) > self._limit:
            raise PermanentProviderError(
                ErrorCode.OUTPUT_LIMIT_EXCEEDED,
                "The provider output exceeds the configured byte limit.",
            )
        count = self._output.write(payload)
        self._written += count
        return count

    def __getattr__(self, name: str):
        return getattr(self._output, name)


def _remove_temp_best_effort(temp_path: Path) -> None:
    try:
        temp_path.unlink(missing_ok=True)
    except OSError:
        pass


def _execute(provider: ImageProvider, request: TransformRequest, temp_path: Path) -> WorkerResult:
    try:
        with temp_path.open("xb") as output:
            result = provider.transform(
                request, _BoundedOutput(output, request.max_output_bytes)
            )
            output.flush()
            os.fsync(output.fileno())
        return WorkerResult(
            outcome=WorkerOutcome.SUCCEEDED.value,
            provider_request_id=validated_request_id(result.provider_request_id),
            output_size=result.output_size,
            media_type=result.media_type,
        )
    except ProviderError as exc:
        _remove_temp_best_effort(temp_path)
        return WorkerResult(
            outcome=exc.outcome.value,
            error_code=exc.code,
            safe_message=exc.safe_message,
            retry_after_seconds=exc.retry_after_seconds,
        )
    except Exception:
        _remove_temp_best_effort(temp_path)
        return WorkerResult(
            outcome=WorkerOutcome.PERMANENT.value,
            error_code=ErrorCode.PROVIDER_PERMANENT,
            safe_message="The provider worker failed safely.",
        )


def _execute_config(
    provider_config: ProviderConfig,
    request: TransformRequest,
    temp_path: Path,
    access_token: AccessToken | None = None,
) -> WorkerResult:
    try:
        provider = _provider_from_config(
            provider_config, access_token,
            **({"azure_response_retries": True} if request.azure_response_retries else {}),
        )
    except ProviderError as exc:
        return WorkerResult(
            outcome=exc.outcome.value,
            error_code=exc.code,
            safe_message=exc.safe_message,
            retry_after_seconds=exc.retry_after_seconds,
        )
    except Exception:
        return WorkerResult(
            outcome=WorkerOutcome.PERMANENT.value,
            error_code=ErrorCode.PROVIDER_PERMANENT,
            safe_message="The provider worker could not be initialized safely.",
        )
    return _execute(provider, request, temp_path)


class InProcessAttemptRunner:
    def __init__(self, provider: ImageProvider) -> None:
        self.provider = provider

    def run(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
    ) -> WorkerResult:
        del provider, shutdown_deadline
        return _execute(self.provider, request, temp_path)


def _child_entry(
    provider_config: ProviderConfig,
    request: TransformRequest,
    temp_path: Path,
    result_queue: multiprocessing.Queue,
    access_token: AccessToken | None = None,
) -> None:
    if access_token is not None:
        logging.disable(logging.CRITICAL)
    with redact_secret(access_token.token) if access_token is not None else nullcontext():
        result_queue.put(_execute_config(provider_config, request, temp_path, access_token))


def _target_entry(
    target,
    provider_config: ProviderConfig,
    request: TransformRequest,
    temp_path: Path,
    result_queue: multiprocessing.Queue,
    dispatch_event,
    access_token: AccessToken | None = None,
) -> None:
    if dispatch_event is not None:
        dispatch_event.set()
    if access_token is None:
        target(provider_config, request, temp_path, result_queue)
    else:
        logging.disable(logging.CRITICAL)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        if hasattr(signal, "SIGBREAK"):
            signal.signal(signal.SIGBREAK, signal.SIG_IGN)
        with redact_secret(access_token.token):
            target(provider_config, request, temp_path, result_queue, access_token)


class SubprocessAttemptRunner:
    def __init__(
        self, *, target=_child_entry, dispatch_event=None,
        on_wait: Callable[[], None] | None = None,
    ) -> None:
        self._target = target
        self._dispatch_event = dispatch_event
        self.on_wait = on_wait

    @staticmethod
    def _deadline(value: ShutdownDeadline | float | None) -> float | None:
        return value.current() if isinstance(value, ShutdownDeadline) else value

    @staticmethod
    def _stop_process(process: multiprocessing.Process, deadline: float) -> None:
        if not process.is_alive():
            return
        process.terminate()
        remaining = max(0.0, deadline - time.monotonic())
        process.join(timeout=min(0.1, remaining / 2))
        if process.is_alive():
            process.kill()
            process.join(timeout=max(0.0, deadline - time.monotonic()))

    def run(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
    ) -> WorkerResult:
        return self._run(provider, request, temp_path, shutdown_deadline)

    def run_authenticated(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
        access_token: AccessToken,
    ) -> WorkerResult:
        with redact_secret(access_token.token):
            return self._run(provider, request, temp_path, shutdown_deadline, access_token)

    def _run(
        self,
        provider: ProviderConfig,
        request: TransformRequest,
        temp_path: Path,
        shutdown_deadline: ShutdownDeadline | float | None,
        access_token: AccessToken | None = None,
    ) -> WorkerResult:
        if access_token is not None and self._deadline(shutdown_deadline) is not None:
            return WorkerResult(
                outcome=WorkerOutcome.PERMANENT.value,
                error_code=ErrorCode.SHUTDOWN_INTERRUPTED,
                safe_message="Processing stopped before the provider subprocess started.",
            )
        context = multiprocessing.get_context("spawn")
        result_queue = context.Queue(maxsize=1)
        process = context.Process(
            target=_target_entry,
            args=(
                self._target,
                provider,
                request,
                temp_path,
                result_queue,
                self._dispatch_event,
                access_token,
            ),
            daemon=False,
        )
        keep_temp = False
        try:
            process.start()
            timeout_at = time.monotonic() + provider.request_timeout_seconds + 5.0
            try:
                while process.is_alive():
                    requested_deadline = self._deadline(shutdown_deadline)
                    if requested_deadline is not None:
                        self._stop_process(process, requested_deadline)
                        break
                    effective_deadline = (
                        min(timeout_at, requested_deadline)
                        if requested_deadline is not None
                        else timeout_at
                    )
                    if time.monotonic() >= effective_deadline:
                        break
                    if self.on_wait is not None:
                        self.on_wait()
                    process.join(timeout=0.05)
            except KeyboardInterrupt:
                self._stop_process(process, time.monotonic() + 2.0)
                raise
            if process.is_alive():
                requested_deadline = self._deadline(shutdown_deadline)
                stop_deadline = requested_deadline or (time.monotonic() + 2.0)
                self._stop_process(process, stop_deadline)
            if self._deadline(shutdown_deadline) is not None:
                return WorkerResult(
                    outcome=WorkerOutcome.AMBIGUOUS.value,
                    error_code=ErrorCode.PROVIDER_AMBIGUOUS,
                    safe_message="The provider attempt exceeded its safe execution deadline.",
                )
            try:
                result = result_queue.get(timeout=1)
                keep_temp = result.outcome == WorkerOutcome.SUCCEEDED.value
                return result
            except queue.Empty:
                return WorkerResult(
                    outcome=WorkerOutcome.AMBIGUOUS.value,
                    error_code=ErrorCode.PROVIDER_AMBIGUOUS,
                    safe_message="The provider worker exited without a definitive result.",
                )
        finally:
            if process.pid is not None and process.is_alive():
                self._stop_process(process, time.monotonic() + 0.25)
            result_queue.close()
            result_queue.join_thread()
            if process.pid is not None and not process.is_alive():
                process.close()
            if not keep_temp:
                _remove_temp_best_effort(temp_path)
