from __future__ import annotations

import hashlib
import json
import logging
import math
import os
import re
import shutil
import stat
import threading
import time
import uuid
from dataclasses import dataclass, replace
from datetime import datetime, timedelta
from pathlib import Path
from typing import Callable

from .azure_endpoint import AZURE_API_VERSION, canonical_azure_endpoint
from .authentication import (
    AuthenticationBroker, AuthenticationSession, DEFAULT_CLIENT, DEFAULT_TENANT,
    TOKEN_ALLOWANCE_SECONDS, authentication_error,
)
from .config import AppConfig, require_failed_variant_retry_policy
from .domain import (
    AppError,
    ErrorCode,
    FailureRecord,
    JobStatus,
    RequestIdentity,
    ShutdownToken,
    SourceSnapshot,
    StableCandidate,
    TransformRequest,
    WorkerOutcome,
    valid_variant,
)
from .image_io import validate_input, validate_output_png
from .logging_setup import log_event
from .path_safety import (
    contained_leaf,
    reject_reparse_components,
    validated_leaf_name,
)
from .providers.worker import (
    AttemptRunner,
    ShutdownDeadline,
    SubprocessAttemptRunner,
)
from .providers.openai import _CONTENT_FILTER_CODES
from .publication import initial_output_leaf, prepare_publication, promote_planned_output
from .quarantine import (
    preserve_mpo_failure_audit, read_mpo_failure_provenance,
    preserve_mpo_request_transition_audit,
    reconcile_quarantine_temps, write_failure_record,
    read_response_failure_provenance, preserve_response_retry_audits,
    _authorize_replay_history,
    PUBLISHED_OUTPUT_MISSING, read_published_failure_provenance,
    preserve_published_restoration_audits,
)
from .scanner import FolderScanner, is_supported_path, stage_candidate, wait_until_stable
from .scheduler import SchedulerClock
from .runtime import DestinationSession, RuntimeLock
from .state import (
    JobRecord,
    StateStore,
    require_valid_job_id,
)

_MAX_STAGED_JOBS = 1_000
_MIN_FREE_SPACE_RESERVE = 64 * 1024 * 1024
_ACKNOWLEDGED_WATCH_ADMISSION_ERRORS = frozenset(
    {
        ErrorCode.IMAGE_LIMIT_EXCEEDED,
        ErrorCode.INVALID_IMAGE,
        ErrorCode.UNSUPPORTED_FILE,
    }
)
# Provider-side rejections that make a configured fallback variant eligible. PROVIDER_RETRYABLE
# only reaches a terminal FAILED job once its bounded retries are exhausted.
_FALLBACK_TRIGGER_CODES = frozenset(
    {
        ErrorCode.PROVIDER_PERMANENT,
        ErrorCode.PROVIDER_RETRYABLE,
    }
)
_TERMINAL_STATUSES = frozenset({JobStatus.SUCCEEDED, JobStatus.FAILED, JobStatus.AMBIGUOUS})
_STAGED_NONTERMINAL_STATUSES = {
    JobStatus.READY,
    JobStatus.READY_RETRY,
    JobStatus.DISPATCHING,
    JobStatus.RESPONSE_STAGED,
    JobStatus.OUTPUT_VERIFIED,
    JobStatus.PUBLISHED,
}


class _StagedCleanupOwnershipError(AppError):
    pass


class _MpoRecoveryEvidenceError(AppError):
    pass


class _ResponseRecoveryEvidenceError(AppError):
    pass


def _path_key(path: Path | str) -> str:
    return os.path.normcase(os.path.normpath(os.path.abspath(path)))


def _fallback_trigger(job: JobRecord) -> ErrorCode | None:
    if job.status != JobStatus.FAILED or job.error_code is None:
        return None
    try:
        code = ErrorCode(job.error_code)
    except ValueError:
        return None
    return code if code in _FALLBACK_TRIGGER_CODES else None


def _file_sha256(path: Path, expected_size: int) -> str | None:
    """SHA-256 of a regular, non-link file of exactly expected_size bytes, else None.

    Links, reparse points and non-regular files are refused before opening; the opened file must
    be the one inspected, and reading is bounded to expected_size + 1 bytes.
    """
    try:
        before = os.lstat(path)
        if not Processor._is_safe_regular_staged_stat(before) or before.st_size != expected_size:
            return None
        digest = hashlib.sha256()
        remaining = expected_size + 1
        with open(path, "rb") as stream:
            opened = os.fstat(stream.fileno())
            if (
                not Processor._is_safe_regular_staged_stat(opened)
                or (opened.st_dev, opened.st_ino) != (before.st_dev, before.st_ino)
                or opened.st_size != expected_size
            ):
                return None
            total = 0
            while remaining > 0 and (chunk := stream.read(min(1024 * 1024, remaining))):
                remaining -= len(chunk)
                total += len(chunk)
                digest.update(chunk)
    except OSError:
        return None
    if total != expected_size:
        return None
    return digest.hexdigest()


def _source_matches_job(path: Path, job: JobRecord) -> bool:
    """True when the file on disk is the exact version (size, mtime, SHA-256) recorded for job."""
    try:
        info = os.stat(path)
    except OSError:
        return False
    if (info.st_size, info.st_mtime_ns) != (job.size, job.mtime_ns):
        return False
    return _file_sha256(path, job.size) == job.sha256


@dataclass(frozen=True)
class ProcessResult:
    job_id: str
    status: JobStatus
    output_path: Path | None
    attempts: int
    error_code: ErrorCode | None = None
    variant: str = ""


class Processor:
    def __init__(
        self,
        config: AppConfig,
        logger: logging.Logger,
        *,
        runner: AttemptRunner | None = None,
        sleep: Callable[[float], None] = time.sleep,
        publication_hook: Callable[[str], None] | None = None,
        state_transition_hook: Callable[[str], None] | None = None,
        auth_session: AuthenticationSession | None = None,
        clock: SchedulerClock | None = None,
        runtime: DestinationSession | None = None,
    ) -> None:
        self.config = config
        self.clock = clock or SchedulerClock()
        self._logged_rate_deadline: datetime | None = None
        self._next_heartbeat = self.clock.monotonic() + config.watch.heartbeat_seconds
        self._active_progress: tuple[JobRecord, int, float] | None = None
        self._reported_terminal: set[tuple[str, JobStatus]] = set()
        # Fallbacks waiting on a non-terminal primary: (path key, size, mtime, fallback) ->
        # (candidate, primary job id). Config-derived and in-memory only; restarts rediscover them.
        self._pending_fallbacks: dict[tuple[str, int, int, str], tuple[StableCandidate, str]] = {}
        # Consecutive failed admissions of a pending fallback version; repeats are logged on a
        # power-of-two schedule so a permanently locked source does not flood the log.
        self._pending_fallback_failures: dict[tuple[str, int, int, str], int] = {}
        self._fallback_notes: set[tuple[str, str]] = set()
        self._queue_note: str | None = None
        self._published_integrity_messages: dict[str, str] = {}
        self._local_restoration_checked: set[str] = set()
        self._reported_input_identity_mismatches: set[str] = set()
        self.logger = logger
        self.runner = runner or SubprocessAttemptRunner()
        self.auth_session = auth_session
        self.sleep = sleep
        self._publication_hook = publication_hook
        self.internal = config.paths.destination / ".tcfcomic"
        self.staging = self.internal / "staging"
        self.runtime = runtime or DestinationSession(config.paths.destination)
        self._owns_runtime = runtime is None
        if self._owns_runtime:
            self.runtime.__enter__()
        opened_state = None
        try:
            if not self.runtime.active or self.runtime.destination != Path(os.path.abspath(config.paths.destination)):
                raise AppError(ErrorCode.STATE_FAILED, "Processor destination coordination is invalid.")
            self.runtime.preflight(named=config.provider.prompts is not None)
            if config.provider.authentication == "interactive":
                if auth_session is None or not auth_session.started:
                    raise authentication_error("Start interactive sign-in before constructing the processor.")
                if not callable(getattr(self.runner, "run_authenticated", None)):
                    raise authentication_error("The provider runner does not support interactive authentication.")
            self.runtime.lock_internal()
            self.lock = self.runtime.inner
            self.staging.mkdir(parents=True, exist_ok=True)
            config.paths.quarantine.mkdir(parents=True, exist_ok=True)
            reject_reparse_components(self.staging)
            reject_reparse_components(config.paths.quarantine)
            self.state = opened_state = StateStore(
                contained_leaf(self.internal, "state.db"),
                transition_hook=state_transition_hook, now=self.clock.timestamp,
                named=config.provider.prompts is not None,
            )
            self.state.initialize()
            reconcile_quarantine_temps(config.paths.quarantine)
            recovery = self.state.recover()
            self._validate_provider_temp_references()
            self._quarantine_recovered_dispatches(recovery.ambiguous_job_ids)
            self._recover_completed_artifacts()
            self._reconcile_orphan_artifacts()
        except BaseException:
            if opened_state is not None:
                opened_state.close()
            if self._owns_runtime:
                self.runtime.__exit__(None, None, None)
            raise
        self._closed = False

    def close(self) -> None:
        if self._closed:
            return
        try:
            self.state.close()
        finally:
            if self._owns_runtime:
                self.runtime.__exit__(None, None, None)
            self._closed = True

    def __enter__(self) -> Processor:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()

    def _request_identity(self, variant: str = "") -> RequestIdentity:
        prompt = self.config.provider.prompt_for(variant)
        if self.config.provider.name == "azure_openai":
            provider = self.config.provider
            interactive = provider.authentication == "interactive"
            prompt = json.dumps(
                [
                    "azure_openai.identity.v2" if interactive else "azure_openai.identity.v1",
                    canonical_azure_endpoint(self.config.provider.endpoint),
                    AZURE_API_VERSION,
                    prompt,
                    *([
                        "interactive",
                        (provider.tenant_id or DEFAULT_TENANT).lower(),
                        (provider.client_id or DEFAULT_CLIENT).lower(),
                        provider.redirect_uri or "dynamic-loopback",
                    ] if interactive else []),
                ],
                ensure_ascii=False,
                separators=(",", ":"),
            )
        return RequestIdentity(
            provider=self.config.provider.name,
            model=self.config.provider.model,
            prompt_hash=hashlib.sha256(
                prompt.encode("utf-8")
            ).hexdigest(),
            variant=variant,
        )

    def _validate_source_location(self, path: Path) -> Path:
        reject_reparse_components(path)
        absolute = Path(os.path.abspath(path))
        source = Path(os.path.abspath(self.config.paths.source))
        if _path_key(absolute.parent) != _path_key(source):
            raise AppError(
                ErrorCode.SOURCE_OUTSIDE_ROOT,
                "The input image must be a direct child of the configured source directory.",
            )
        if not is_supported_path(absolute):
            raise AppError(ErrorCode.UNSUPPORTED_FILE, "The input file type is not supported.")
        return absolute

    def _recovered_staged_path(self, job: JobRecord) -> Path:
        return self._staged_path_from_value(Path(job.staged_path))

    def _provider_temp_path(
        self, job: JobRecord, *, must_exist: bool = False
    ) -> Path:
        if job.temp_name is None:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Recovered state does not identify the provider output file.",
            )
        return contained_leaf(
            self.config.paths.destination,
            job.temp_name,
            suffix=".tmp",
            must_exist=must_exist,
        )

    def _validate_provider_temp_references(self) -> None:
        references: dict[str, str] = {}
        for job in self.state.list_jobs():
            if job.temp_name is None:
                continue
            path_key = _path_key(self._provider_temp_path(job))
            if path_key in references:
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "Recovered provider scratch cleanup ownership is ambiguous.",
                )
            references[path_key] = job.job_id

    def _validated_provider_temp_cleanup_path(self, job: JobRecord) -> Path:
        temp_path = self._provider_temp_path(job)
        target_key = _path_key(temp_path)
        owners = []
        for candidate in self.state.list_jobs():
            if candidate.temp_name is None:
                continue
            if _path_key(self._provider_temp_path(candidate)) == target_key:
                owners.append(candidate.job_id)
        if owners != [job.job_id]:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch cleanup ownership is ambiguous.",
            )
        return temp_path

    def _cleanup_current_provider_temp(self, job_id: str) -> None:
        job = self.state.get_job(job_id)
        if job.temp_name is None:
            return
        temp_name = job.temp_name
        temp_path = self._validated_provider_temp_cleanup_path(job)
        self._remove_temp_file(temp_path)
        if not self.state.clear_current_temp_name(
            job.job_id,
            temp_name,
            job.status,
        ):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch cleanup state changed unexpectedly.",
            )

    def _staged_path_from_value(
        self, raw: Path, *, must_exist: bool = False
    ) -> Path:
        absolute = Path(os.path.abspath(raw))
        if _path_key(absolute.parent) != _path_key(self.staging):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Recovered staging state is outside the configured staging directory.",
            )
        return contained_leaf(
            self.staging,
            absolute.name,
            suffix=".input",
            must_exist=must_exist,
        )

    def _validated_staged_cleanup_path(
        self,
        job_id: str,
        path: Path,
    ) -> Path:
        require_valid_job_id(job_id)
        try:
            staged_key = _path_key(path)
            references = self.state.staged_path_references(path, job_id)
            expected_name_key = _path_key(
                self.staging / f"{job_id}.input"
            )
        except (AppError, OSError, TypeError, ValueError):
            raise _StagedCleanupOwnershipError(
                ErrorCode.STATE_FAILED,
                "The staged input cleanup ownership could not be verified.",
            ) from None

        expected_references = [
            reference
            for reference in references
            if reference[0] == job_id
        ]
        other_nonterminal_references = [
            reference
            for reference in references
            if reference[0] != job_id
            and reference[1] in _STAGED_NONTERMINAL_STATUSES
        ]
        if len(expected_references) > 1 or other_nonterminal_references:
            raise _StagedCleanupOwnershipError(
                ErrorCode.STATE_FAILED,
                "The staged input cleanup ownership is ambiguous.",
            )
        has_expected_reference = len(expected_references) == 1
        if (
            not has_expected_reference
            and (references or staged_key != expected_name_key)
        ):
            raise _StagedCleanupOwnershipError(
                ErrorCode.STATE_FAILED,
                "The staged input does not belong to the expected job.",
            )
        try:
            return self._staged_path_from_value(path)
        except AppError:
            raise _StagedCleanupOwnershipError(
                ErrorCode.STATE_FAILED,
                "The staged input cleanup ownership could not be verified.",
            ) from None

    def _remove_job_staged_file(self, job_id: str, path: Path) -> None:
        staged_path = self._validated_staged_cleanup_path(job_id, path)
        self._remove_staged_file(staged_path)

    def _record_staged_cleanup_ownership_failure(
        self,
        job: JobRecord,
        error: _StagedCleanupOwnershipError,
        *,
        stage: str,
    ) -> None:
        self._quarantine(
            job,
            stage,
            error,
            self.state.attempt_count(job.job_id),
        )
        if job.status == JobStatus.SUCCEEDED:
            self.state.reconcile_succeeded_integrity_failure(
                job.job_id, ErrorCode.STATE_FAILED
            )
        elif job.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
            self.state.reconcile_terminal_staged_integrity_failure(job.job_id)
        else:
            self.state.mark_failed(job.job_id, ErrorCode.STATE_FAILED)

    @staticmethod
    def _staged_metadata(info: os.stat_result) -> tuple[object, ...]:
        return (
            info.st_mode,
            info.st_ino,
            info.st_dev,
            info.st_nlink,
            getattr(info, "st_uid", None),
            getattr(info, "st_gid", None),
            info.st_size,
            info.st_mtime_ns,
            info.st_ctime_ns,
            getattr(info, "st_file_attributes", None),
            getattr(info, "st_reparse_tag", None),
        )

    @staticmethod
    def _staged_identity(info: os.stat_result) -> tuple[object, ...]:
        return (
            info.st_mode,
            info.st_ino,
            info.st_dev,
            info.st_nlink,
            getattr(info, "st_uid", None),
            getattr(info, "st_gid", None),
            info.st_size,
            info.st_mtime_ns,
            getattr(info, "st_file_attributes", None),
            getattr(info, "st_reparse_tag", None),
        )

    @staticmethod
    def _is_safe_regular_staged_stat(info: os.stat_result) -> bool:
        is_reparse = bool(
            getattr(info, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )
        return stat.S_ISREG(info.st_mode) and not is_reparse

    def _stream_verified_staged_identity(
        self,
        staged_path: Path,
        expected: os.stat_result,
        job: JobRecord,
        *,
        enforce_current_limit: bool = True,
    ) -> None:
        max_input_bytes = self.config.limits.max_input_bytes
        if expected.st_size != job.size:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input does not match durable state.",
            )
        if enforce_current_limit and job.size > max_input_bytes:
            raise AppError(
                ErrorCode.IMAGE_LIMIT_EXCEEDED,
                "The input image exceeds the configured byte limit.",
            )

        digest = hashlib.sha256()
        streamed_size = 0
        try:
            with staged_path.open("rb") as stream:
                opened = os.fstat(stream.fileno())
                if (
                    not self._is_safe_regular_staged_stat(opened)
                    or self._staged_identity(opened)
                    != self._staged_identity(expected)
                ):
                    raise AppError(
                        ErrorCode.STATE_FAILED,
                        "The staged input changed before verification.",
                    )
                while True:
                    remaining = job.size - streamed_size
                    chunk = stream.read(min(1024 * 1024, remaining + 1))
                    if not chunk:
                        break
                    streamed_size += len(chunk)
                    if streamed_size > job.size:
                        raise AppError(
                            ErrorCode.STATE_FAILED,
                            "The staged input does not match durable state.",
                        )
                    digest.update(chunk)
                after_read = os.fstat(stream.fileno())
                after_path = os.stat(staged_path, follow_symlinks=False)
        except AppError:
            raise
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input could not be read safely.",
            ) from None

        if (
            self._staged_metadata(after_read) != self._staged_metadata(opened)
            or not self._is_safe_regular_staged_stat(after_path)
            or self._staged_identity(after_path)
            != self._staged_identity(after_read)
            or self._staged_metadata(after_path)
            != self._staged_metadata(expected)
        ):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input changed during verification.",
            )
        if streamed_size != job.size or digest.hexdigest() != job.sha256:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input does not match durable state.",
            )

    def _verify_staged_input(
        self,
        job: JobRecord,
        candidate_stage_path: Path | None = None,
        *,
        enforce_current_limit: bool = True,
    ) -> SourceSnapshot:
        raw = (
            Path(job.staged_path)
            if candidate_stage_path is None
            else Path(candidate_stage_path)
        )
        try:
            staged_path = self._staged_path_from_value(raw, must_exist=True)
            before = os.stat(staged_path, follow_symlinks=False)
        except (AppError, OSError):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input could not be verified safely.",
            ) from None
        if not self._is_safe_regular_staged_stat(before):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input is not a safe regular file.",
            )

        expected_metadata = self._staged_metadata(before)
        self._stream_verified_staged_identity(
            staged_path,
            before,
            job,
            enforce_current_limit=enforce_current_limit,
        )
        validation_error: AppError | None = None
        try:
            validation_limits = (
                self.config.limits
                if enforce_current_limit
                else replace(
                    self.config.limits,
                    max_input_bytes=max(
                        self.config.limits.max_input_bytes,
                        job.size,
                    ),
                )
            )
            validate_input(staged_path, validation_limits, source_name=job.source_name)
        except AppError as exc:
            validation_error = exc

        try:
            after_validation = os.stat(staged_path, follow_symlinks=False)
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input changed during validation.",
            ) from None
        if (
            not self._is_safe_regular_staged_stat(after_validation)
            or self._staged_metadata(after_validation) != expected_metadata
        ):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input changed during validation.",
            )
        self._stream_verified_staged_identity(
            staged_path,
            after_validation,
            job,
            enforce_current_limit=enforce_current_limit,
        )
        if validation_error is not None:
            if validation_error.code in {
                ErrorCode.INVALID_IMAGE,
                ErrorCode.IMAGE_LIMIT_EXCEEDED,
            }:
                raise validation_error
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input could not be validated safely.",
            ) from None

        return SourceSnapshot(
            path=Path(job.source_name),
            normalized_path=job.source_path_key,
            size=job.size,
            mtime_ns=job.mtime_ns,
            sha256=job.sha256,
            staged_path=staged_path,
        )

    def _remove_staged_file(self, path: Path) -> None:
        try:
            staged_path = self._staged_path_from_value(path)
            info = os.stat(staged_path, follow_symlinks=False)
        except FileNotFoundError:
            return
        except AppError as exc:
            if exc.code == ErrorCode.STATE_FAILED:
                raise
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input path is unsafe for cleanup.",
            ) from None
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input could not be inspected for cleanup.",
            ) from None
        if not self._is_safe_regular_staged_stat(info):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input is not safe to remove.",
            )
        try:
            staged_path.unlink()
        except FileNotFoundError:
            return
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staged input could not be removed.",
            ) from None

    def _remove_temp_file(self, path: Path) -> None:
        if _path_key(path.parent) != _path_key(self.config.paths.destination):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch output path is unsafe for cleanup.",
            )
        try:
            temp_path = contained_leaf(
                self.config.paths.destination,
                path.name,
                suffix=".tmp",
            )
            info = os.stat(temp_path, follow_symlinks=False)
        except FileNotFoundError:
            return
        except AppError:
            raise
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch output could not be inspected for cleanup.",
            ) from None
        is_reparse = bool(
            getattr(info, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )
        if not stat.S_ISREG(info.st_mode) or is_reparse:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch output is not safe to remove.",
            )
        try:
            temp_path.unlink()
        except FileNotFoundError:
            return
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The provider scratch output could not be removed.",
            ) from None

    def _staging_usage(self) -> tuple[int, int]:
        try:
            reject_reparse_components(self.staging)
        except AppError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "The staging directory could not be inspected safely.",
            ) from None
        try:
            entries = list(os.scandir(self.staging))
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Staging usage could not be enumerated safely.",
            ) from None
        files = 0
        total_size = 0
        for entry in entries:
            if not entry.name.lower().endswith(".input"):
                continue
            candidate = Path(entry.path)
            if Path(os.path.abspath(candidate.parent)) != Path(
                os.path.abspath(self.staging)
            ):
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "A staged input escaped the configured staging directory.",
                )
            try:
                info = entry.stat(follow_symlinks=False)
            except OSError:
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "A staged input could not be inspected safely.",
                ) from None
            if not self._is_safe_regular_staged_stat(info):
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "Staging contains an unsafe input artifact.",
                )
            files += 1
            total_size += info.st_size
        return files, total_size

    def _snapshot_from_job(self, job: JobRecord) -> SourceSnapshot:
        return SourceSnapshot(
            path=Path(job.source_name),
            normalized_path=job.source_path_key,
            size=job.size,
            mtime_ns=job.mtime_ns,
            sha256=job.sha256,
            staged_path=self._recovered_staged_path(job),
        )

    def _result_for(self, job: JobRecord) -> ProcessResult:
        output = (
            contained_leaf(
                self.config.paths.destination, job.output_name, suffix=".png"
            )
            if job.status == JobStatus.SUCCEEDED and job.output_name
            else None
        )
        try:
            error_code = ErrorCode(job.error_code) if job.error_code else None
        except ValueError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Durable state contains an invalid terminal error code.",
            ) from None
        return ProcessResult(
            job.job_id,
            job.status,
            output,
            self.state.attempt_count(job.job_id),
            error_code,
            job.variant,
        )

    def _persisted_result_for(self, job_id: str) -> ProcessResult:
        job = self.state.get_job(job_id)
        if job.status == JobStatus.SUCCEEDED:
            try:
                staged_path = self._validated_staged_cleanup_path(
                    job.job_id, Path(job.staged_path)
                )
                self._verify_published(job)
            except _StagedCleanupOwnershipError as exc:
                self._record_staged_cleanup_ownership_failure(
                    job,
                    exc,
                    stage="recovery",
                )
                job = self.state.get_job(job.job_id)
            except AppError as exc:
                self._published_integrity_messages[job.job_id] = exc.safe_message
                self._quarantine(
                    job,
                    "recovery",
                    exc,
                    self.state.attempt_count(job.job_id),
                )
                self.state.reconcile_succeeded_integrity_failure(
                    job.job_id, exc.code
                )
                job = self.state.get_job(job.job_id)
                self._report_terminal(job.job_id)
                try:
                    self._remove_job_staged_file(
                        job.job_id, Path(job.staged_path)
                    )
                except _StagedCleanupOwnershipError as cleanup_error:
                    self._record_staged_cleanup_ownership_failure(
                        job,
                        cleanup_error,
                        stage="recovery",
                    )
                    job = self.state.get_job(job.job_id)
            else:
                self._remove_staged_file(staged_path)
        elif self._has_published_failure_binding(job):
            self._reconcile_restored_output(job)
            job = self.state.get_job(job.job_id)
        return self._result_for(job)

    @staticmethod
    def _has_published_failure_binding(job: JobRecord) -> bool:
        return (
            job.status == JobStatus.FAILED
            and job.temp_name is None
            and job.error_code in {
                ErrorCode.STATE_FAILED.value, ErrorCode.OUTPUT_INVALID.value,
                ErrorCode.PUBLICATION_FAILED.value, ErrorCode.OUTPUT_LIMIT_EXCEEDED.value,
            }
            and job.output_name is not None and job.output_sha256 is not None
        )

    def _reconcile_restored_output(self, job: JobRecord) -> bool:
        """Verify an exact restored publication without reading input or dispatching."""
        output_name = None
        try:
            if type(job.output_name) is not str:
                raise AppError(ErrorCode.STATE_FAILED, "Restoration skipped: invalid output binding.")
            output_name = validated_leaf_name(job.output_name, suffix=".png")
            if len(output_name) > 255 or (job.variant and not valid_variant(job.variant)):
                raise AppError(ErrorCode.STATE_FAILED, "Restoration skipped: invalid output binding.")
            attempts = self.state.completed_publication_attempts(job.job_id)
            payload = read_published_failure_provenance(job, self.config.paths.quarantine, attempts)
            self._local_restoration_checked.add(job.job_id)
            staged = self._validated_staged_cleanup_path(job.job_id, Path(job.staged_path))
            base = initial_output_leaf(self._snapshot_from_job(job), job.job_id, job.variant)[:-4]
            if re.fullmatch(re.escape(base) + r"(?:__[0-9a-f]{8})?\.png", output_name) is None:
                raise AppError(ErrorCode.STATE_FAILED, "Restoration skipped: output name is not job-owned.")

            def check_evidence() -> bool:
                if (
                    self.state.get_job(job.job_id) != job
                    or self.state.completed_publication_attempts(job.job_id) != attempts
                    or read_published_failure_provenance(
                        job, self.config.paths.quarantine, attempts,
                    ) != payload
                    or self._validated_staged_cleanup_path(job.job_id, Path(job.staged_path)) != staged
                    or os.path.lexists(staged)
                ):
                    raise AppError(
                        ErrorCode.STATE_FAILED,
                        "Restoration skipped: publication evidence or staging ownership changed. No Azure retry performed.",
                    )
                self._verify_published(job)
                preserve_published_restoration_audits(
                    job, self.config.paths.quarantine, payload, attempts,
                )
                # Audit I/O is not authorization: recheck the canonical evidence and
                # exact output again inside the transaction immediately before CAS.
                if read_published_failure_provenance(job, self.config.paths.quarantine, attempts) != payload:
                    return False
                self._verify_published(job)
                return True

            if not self.state.reconcile_restored_publication(job, attempts, check_evidence):
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "Restoration skipped: durable job, attempt history, or output ownership changed. No Azure retry performed.",
                )
        except (AppError, OSError) as exc:
            message = exc.safe_message if isinstance(exc, AppError) else (
                "Restoration skipped: published evidence cannot be read safely. No Azure retry performed."
            )
            if message == "The published output does not match durable state.":
                message = (
                    "Previously generated output checksum mismatch; restore original PNG at expected output name. "
                    "No Azure retry performed."
                )
            self._published_integrity_messages[job.job_id] = message
            log_event(
                self.logger, logging.ERROR, "published_output_restore_required",
                job_id=job.job_id, output_name=output_name if output_name and len(output_name) <= 255 else None,
                variant=job.variant if valid_variant(job.variant) else None,
                error_code=job.error_code, message=message,
            )
            return False
        self._reported_terminal.discard((job.job_id, JobStatus.FAILED))
        self._published_integrity_messages.pop(job.job_id, None)
        log_event(
            self.logger, logging.INFO, "published_output_restored",
            job_id=job.job_id, output_name=output_name, variant=job.variant or None,
            message="Restored output verified; marked successful without a provider request.",
        )
        return True

    def _duplicate_admission_result(
        self,
        existing: JobRecord,
        staged_path: Path,
        *,
        enforce_current_limit: bool = True,
        snapshot: SourceSnapshot | None = None,
        retry_input_rejection: bool = False,
        retry_failed_variants: bool = False,
    ) -> ProcessResult:
        if retry_failed_variants:
            require_failed_variant_retry_policy(self.config)
        if existing.status == JobStatus.SUCCEEDED or self._has_published_failure_binding(existing):
            self._cleanup_response_recovery_stage(existing.job_id, staged_path)
            result = self._persisted_result_for(existing.job_id)
            if retry_failed_variants and existing.status == result.status == JobStatus.SUCCEEDED:
                log_event(
                    self.logger, logging.INFO, "failed_variant_retry_skipped",
                    job_id=existing.job_id, variant=existing.variant or None,
                    message="Successful variant skipped; its request identity and history are unchanged.",
                )
            return result
        if retry_failed_variants:
            if snapshot is not None and existing.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
                if self.state.attempt_count(existing.job_id) > 0:
                    self._reconsider_failed_response(existing, snapshot)
                    return self._result_for(self.state.get_job(existing.job_id))
        retry_options = {"retry_input_rejection": True} if retry_input_rejection else {}
        if snapshot is not None and self._reconsider_mpo_failure(existing, snapshot, **retry_options):
            self._report_queued(existing.job_id)
            return self._persisted_result_for(existing.job_id)
        if existing.status not in {JobStatus.READY, JobStatus.READY_RETRY}:
            self._remove_staged_file(staged_path)
            return self._persisted_result_for(existing.job_id)

        if self._checked_dispatch_history(existing) is None:
            self._cleanup_response_recovery_stage(existing.job_id, staged_path)
            return self._persisted_result_for(existing.job_id)

        try:
            validated_old_staged_path = self._validated_staged_cleanup_path(
                existing.job_id, Path(existing.staged_path)
            )
            self._verify_staged_input(
                existing,
                enforce_current_limit=enforce_current_limit,
            )
        except _StagedCleanupOwnershipError as exc:
            self._remove_staged_file(staged_path)
            self._record_staged_cleanup_ownership_failure(
                existing,
                exc,
                stage="input",
            )
            return self._persisted_result_for(existing.job_id)
        except AppError:
            try:
                self._verify_staged_input(
                    existing,
                    staged_path,
                    enforce_current_limit=enforce_current_limit,
                )
            except AppError as exc:
                self._remove_staged_file(staged_path)
                self._quarantine(
                    existing,
                    "input",
                    exc,
                    self.state.attempt_count(existing.job_id),
                )
                self.state.mark_failed(existing.job_id, exc.code)
                self._remove_job_staged_file(
                    existing.job_id, Path(existing.staged_path)
                )
                return self._persisted_result_for(existing.job_id)
            old_staged_path = Path(existing.staged_path)
            repaired = self.state.compare_and_swap_staged_path(
                existing.job_id,
                old_staged_path,
                staged_path,
            )
            if repaired:
                if _path_key(old_staged_path) != _path_key(staged_path):
                    self._remove_staged_file(validated_old_staged_path)
            else:
                self._remove_staged_file(staged_path)
            existing = self.state.get_job(existing.job_id)
        else:
            self._remove_staged_file(staged_path)
        result = self._persisted_result_for(existing.job_id)
        if result.status in {JobStatus.READY, JobStatus.READY_RETRY}:
            log_event(
                self.logger, logging.INFO, "job_already_queued", job_id=existing.job_id,
                source_name=existing.source_name, pending_count=self.state.pending_count(),
                message=f"Already queued {existing.source_name}; duplicate admission skipped.",
            )
        return result

    def _cleanup_response_recovery_stage(self, job_id: str, path: Path) -> None:
        references = self.state.staged_path_references(path, job_id, include_terminal=True)
        if any(owner != job_id for owner, _ in references):
            raise _ResponseRecoveryEvidenceError(
                ErrorCode.STATE_FAILED, "Fresh recovery input cleanup ownership changed; file retained.",
            )
        if not references:
            self._remove_staged_file(path)

    def _reconsider_failed_response(self, job: JobRecord, snapshot: SourceSnapshot) -> bool:
        require_failed_variant_retry_policy(self.config)

        def skipped(reason: str) -> bool:
            log_event(
                self.logger, logging.INFO, "failed_variant_retry_skipped",
                job_id=job.job_id, source_name=job.source_name, variant=job.variant or None,
                message=reason,
            )
            return False

        try:
            count = self.state.attempt_count(job.job_id)
            budget = self.config.retry.max_attempts
            if count >= budget:
                return skipped(f"Lifetime attempt budget exhausted ({count}/{budget}); variant not queued.")
            if (
                job.provider != "azure_openai"
                or job.status not in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
                or count < 1
                or any(value is not None for value in (job.temp_name, job.output_name, job.output_sha256))
            ):
                return skipped("Recovery requires a failed Azure response with no output or scratch references.")
            attempts = self.state.completed_attempts(job.job_id)
            if len(attempts) != count:
                return skipped("Attempt history changed; recovery skipped.")
            try:
                payload = read_response_failure_provenance(job, self.config.paths.quarantine, attempts)
            except AppError:
                # Saved allowlisted diagnostics explain a refusal; they never
                # authorize retry or replace the stricter provenance exception.
                saved = attempts[-1].safe_message or ""
                if any(f"Provider code {code}." in saved for code in _CONTENT_FILTER_CODES):
                    skipped(
                        "Saved diagnostic records a content-policy rejection; automatic retry is not permitted."
                    )
                else:
                    skipped(
                        "Historical response evidence cannot authorize retry; its classification is unverified or terminal."
                    )
                raise
            identity = self._request_identity(job.variant)

            def artifacts_clear() -> bool:
                if os.path.lexists(self._recovered_staged_path(job)):
                    return False
                base = initial_output_leaf(snapshot, job.job_id, job.variant)[:-4]
                reject_reparse_components(self.config.paths.destination)
                with os.scandir(self.config.paths.destination) as entries:
                    return not any(
                        entry.name.lower().endswith(".tmp")
                        or entry.name.casefold().startswith(base.casefold())
                        for entry in entries
                    )

            def check_source() -> bool:
                source = self._validate_source_location(snapshot.path)
                info = source.stat(follow_symlinks=False)
                if (
                    (str(source), source.name, info.st_size, info.st_mtime_ns)
                    != (job.source_path, job.source_name, job.size, job.mtime_ns)
                    or snapshot.sha256 != job.sha256
                    or not self._is_safe_regular_staged_stat(info)
                    or not artifacts_clear()
                ):
                    return False
                self._stream_verified_staged_identity(source, info, job)
                self._verify_staged_input(job, snapshot.staged_path)
                return True

            if not check_source():
                return skipped("Source identity changed or old stage/publication artifacts remain; recovery skipped.")

            def preserve_audits() -> None:
                preserve_response_retry_audits(
                    job, self.config.paths.quarantine, identity, payload, attempts, budget,
                )

            preserve_audits()

            def check_evidence() -> bool:
                if (
                    self.state.completed_attempts(job.job_id) != attempts
                    or read_response_failure_provenance(
                        job, self.config.paths.quarantine, attempts,
                    ) != payload
                    or not check_source()
                ):
                    return False
                preserve_audits()
                return True

            delay = min(
                self.config.retry.initial_delay_seconds * (2 ** (count - 1)),
                self.config.retry.max_delay_seconds,
            )
            not_before = datetime.fromisoformat(attempts[-1].finished_at) + timedelta(seconds=delay)
            queued = self.state.requeue_failed_response(
                job, snapshot, identity, attempts, check_evidence,
                max_attempts=budget, next_attempt_at=not_before.isoformat(),
                retry_failed_variants=True,
            )
            if not queued:
                return skipped("Recovery state or evidence changed before commit; variant not queued.")
            self._reported_terminal.discard((job.job_id, job.status))
            self._report_queued(job.job_id)
            gate = self.state.provider_not_before(self.config.provider.requests_per_minute)
            deadline = max(not_before, gate) if gate is not None else not_before
            log_event(
                self.logger, logging.INFO, "failed_variant_retry_queued",
                job_id=job.job_id, source_name=job.source_name, variant=job.variant or None,
                attempt_no=count + 1, max_attempts=budget,
                message=(
                    f"Queued retry attempt {count + 1}/{budget} using current request settings; "
                    f"not before {deadline.isoformat()}; shared provider pacing still applies."
                ),
            )
            return True
        except AppError as exc:
            raise _ResponseRecoveryEvidenceError(exc.code, exc.safe_message) from None
        except OSError:
            raise _ResponseRecoveryEvidenceError(
                ErrorCode.STATE_FAILED,
                "Failed-response recovery evidence could not be verified.",
            ) from None
        finally:
            # Includes exceptions raised after COMMIT: never remove an adopted stage.
            self._cleanup_response_recovery_stage(job.job_id, snapshot.staged_path)

    def _reconsider_mpo_failure(
        self, job: JobRecord, snapshot: SourceSnapshot, *, retry_input_rejection: bool = False,
    ) -> bool:
        identity = self._request_identity(job.variant)
        if (
            job.status != JobStatus.FAILED
            or job.error_code != ErrorCode.INVALID_IMAGE.value
            or self.state.attempt_count(job.job_id) != 0
            or any(value is not None for value in (job.temp_name, job.output_name, job.output_sha256))
        ):
            return False
        identity_changed = (
            (job.provider, job.model, job.prompt_hash)
            != (identity.provider, identity.model, identity.prompt_hash)
        )
        if identity_changed and not retry_input_rejection:
            if job.job_id not in self._reported_input_identity_mismatches:
                self._reported_input_identity_mismatches.add(job.job_id)
                log_event(
                    self.logger, logging.ERROR, "input_rejection_settings_mismatch",
                    job_id=job.job_id, source_name=job.source_name,
                    error_code=ErrorCode.INVALID_IMAGE.value,
                    message=(
                        "Current request settings differ from this never-sent input rejection. "
                        "For an eligible JPEG-named MPO only, explicitly retry with current settings: "
                        'python -m tcfcomic process --config "<config-file>" '
                        '--retry-input-rejection "<image-path>". Eligibility checks still apply.'
                    ),
                )
            return False
        try:
            try:
                self._verify_staged_input(job, snapshot.staged_path)
                info = validate_input(snapshot.staged_path, self.config.limits, job.source_name)
            except AppError as exc:
                if exc.code in {ErrorCode.INVALID_IMAGE, ErrorCode.IMAGE_LIMIT_EXCEEDED}:
                    return False
                raise
            if info.format != "MPO":
                return False

            def artifacts_clear() -> bool:
                old_stage = self._recovered_staged_path(job)
                if os.path.lexists(old_stage):
                    return False
                base = initial_output_leaf(snapshot, job.job_id, job.variant)[:-4]
                reject_reparse_components(self.config.paths.destination)
                with os.scandir(self.config.paths.destination) as entries:
                    if any(
                        entry.name.lower().endswith(".tmp")
                        or entry.name.casefold().startswith(base.casefold())
                        for entry in entries
                    ):
                        return False
                return True

            if not artifacts_clear():
                return False
            payload = read_mpo_failure_provenance(job, self.config.paths.quarantine)

            def preserve_audits() -> None:
                preserve_mpo_failure_audit(job, self.config.paths.quarantine, payload)
                if identity_changed:
                    preserve_mpo_request_transition_audit(
                        job, self.config.paths.quarantine, identity, payload,
                    )

            preserve_audits()

            def check_evidence() -> bool:
                source = self._validate_source_location(snapshot.path)
                source_info = source.stat(follow_symlinks=False)
                if (
                    not self._is_safe_regular_staged_stat(source_info)
                    or (source_info.st_size, source_info.st_mtime_ns)
                    != (snapshot.size, snapshot.mtime_ns)
                    or not artifacts_clear()
                    or read_mpo_failure_provenance(job, self.config.paths.quarantine) != payload
                ):
                    return False
                preserve_audits()
                self._stream_verified_staged_identity(source, source_info, job)
                self._verify_staged_input(job, snapshot.staged_path)
                return True

            retry_options = {"retry_input_rejection": True} if retry_input_rejection else {}
            return self.state.requeue_never_sent_mpo(
                job, snapshot, identity, check_evidence, **retry_options,
            )
        except AppError as exc:
            raise _MpoRecoveryEvidenceError(exc.code, exc.safe_message) from None
        finally:
            # A post-commit crash must not delete the newly durable input.
            if self.state.get_job(job.job_id).staged_path != str(snapshot.staged_path):
                if self.state.staged_path_references(snapshot.staged_path, job.job_id):
                    raise _MpoRecoveryEvidenceError(
                        ErrorCode.STATE_FAILED, "The staged input cleanup ownership changed during recovery.",
                    )
                self._remove_staged_file(snapshot.staged_path)

    def _admit(
        self,
        path: Path,
        *,
        candidate: StableCandidate | None,
        shutdown_event: threading.Event,
        retry_input_rejection: bool = False,
        retry_failed_variants: bool = False,
        variant: str = "",
    ) -> ProcessResult:
        if retry_failed_variants:
            require_failed_variant_retry_policy(self.config)
        identity = self._request_identity(variant)
        source_path = self._validate_source_location(path)
        shutdown = ShutdownToken(shutdown_event.is_set, shutdown_event.wait)
        if candidate is None:
            candidate = wait_until_stable(
                source_path,
                self.config.watch.stable_seconds,
                self.config.watch.poll_interval_seconds,
                shutdown,
            )
        oversized_matches: list[JobRecord] = []
        stage_limit = self.config.limits.max_input_bytes
        if candidate.size > stage_limit:
            oversized_matches = self.state.find_jobs_by_source_metadata(
                os.path.normcase(str(source_path)),
                candidate.size,
                candidate.mtime_ns,
                variant=variant,
            )
            if not oversized_matches:
                raise AppError(
                    ErrorCode.IMAGE_LIMIT_EXCEEDED,
                    "The input image exceeds the configured byte limit.",
                )
            stage_limit = candidate.size
        self._ensure_staging_capacity(candidate)
        draft_job_id = uuid.uuid4().hex
        staged_path = contained_leaf(
            self.staging, f"{draft_job_id}.input", suffix=".input"
        )
        snapshot = stage_candidate(
            candidate,
            staged_path,
            stage_limit,
            shutdown,
        )
        if oversized_matches:
            matching_job = next(
                (
                    job
                    for job in oversized_matches
                    if job.sha256 == snapshot.sha256
                ),
                None,
            )
            if matching_job is None:
                self._remove_staged_file(staged_path)
                raise AppError(
                    ErrorCode.IMAGE_LIMIT_EXCEEDED,
                    "The input image exceeds the configured byte limit.",
                )
            return self._duplicate_admission_result(
                matching_job,
                staged_path,
                enforce_current_limit=False,
            )
        admission = self.state.admit(snapshot, identity)
        if not admission.admitted:
            existing = self.state.get_job(admission.job_id)
            retry_options = {"retry_input_rejection": True} if retry_input_rejection else {}
            if retry_failed_variants:
                retry_options["retry_failed_variants"] = True
            return self._duplicate_admission_result(
                existing, staged_path, snapshot=snapshot, **retry_options,
            )

        job = self.state.get_job(admission.job_id)
        try:
            self._validated_staged_cleanup_path(
                job.job_id, snapshot.staged_path
            )
            self._verify_staged_input(job)
        except _StagedCleanupOwnershipError as exc:
            self._record_staged_cleanup_ownership_failure(
                job,
                exc,
                stage="input",
            )
            return self._persisted_result_for(admission.job_id)
        except AppError as exc:
            self._quarantine(job, "input", exc, 0)
            self.state.mark_failed(admission.job_id, exc.code)
            self._remove_job_staged_file(
                admission.job_id, snapshot.staged_path
            )
            return self._persisted_result_for(admission.job_id)
        self._report_queued(admission.job_id)
        return self._persisted_result_for(admission.job_id)

    def _report_queued(self, job_id: str) -> None:
        job = self.state.get_job(job_id)
        pending = self.state.pending_count()
        note, self._queue_note = self._queue_note, None
        if note is not None:
            log_event(
                self.logger, logging.INFO, "fallback_queued", job_id=job_id,
                variant=job.variant or None,
                source_name=job.source_name, pending_count=pending,
                message=f"Queued {job.source_name} as fallback; {note}; {pending} pending.",
            )
            return
        log_event(
            self.logger, logging.INFO, "job_queued", job_id=job_id,
            variant=job.variant or None,
            source_name=job.source_name, pending_count=pending,
            message=f"Queued {job.source_name}; {pending} pending.",
        )

    def _report_terminal(self, job_id: str) -> None:
        job = self.state.get_job(job_id)
        if job.status not in {JobStatus.SUCCEEDED, JobStatus.FAILED, JobStatus.AMBIGUOUS}:
            return
        key = (job_id, job.status)
        if key in self._reported_terminal:
            return
        self._reported_terminal.add(key)
        succeeded = job.status == JobStatus.SUCCEEDED
        output_name = None
        if type(job.output_name) is str and len(job.output_name) <= 255:
            try:
                output_name = validated_leaf_name(job.output_name, suffix=".png")
            except AppError:
                pass
        log_event(
            self.logger, logging.INFO if succeeded else logging.ERROR,
            "job_processed" if succeeded else "job_failed",
            job_id=job_id, source_name=job.source_name, output_name=output_name,
            variant=job.variant if valid_variant(job.variant) else None,
            result=job.status.value, error_code=job.error_code,
            pending_count=self.state.pending_count(),
            message=(
                f"Processed {job.source_name} -> {job.output_name}."
                if succeeded else self._published_integrity_messages.get(
                    job_id, f"Failed {job.source_name}: {job.error_code}.",
                )
            ),
        )

    def _heartbeat(self, *, authentication_started: float | None = None) -> None:
        if not self.logger.isEnabledFor(logging.INFO):
            return
        now = self.clock.monotonic()
        if now < self._next_heartbeat:
            return
        self._next_heartbeat = now + self.config.watch.heartbeat_seconds
        pending = self.state.pending_count()
        fields: dict[str, object] = {"pending_count": pending}
        if authentication_started is not None:
            elapsed = max(0, int(now - authentication_started))
            fields.update(duration_ms=elapsed * 1000, result="authentication")
            message = f"Waiting for sign-in token refresh; elapsed {elapsed}s; {pending} pending."
        elif self._active_progress is not None:
            job, attempt, started = self._active_progress
            elapsed = max(0, int(now - started))
            fields.update(job_id=job.job_id, source_name=job.source_name,
                          variant=job.variant or None,
                          attempt_no=attempt, duration_ms=elapsed * 1000, result="processing")
            message = f"Processing {job.source_name}; elapsed {elapsed}s; {pending} pending."
        elif pending:
            due = self.state.provider_not_before(self.config.provider.requests_per_minute)
            seconds = max(0, math.ceil((due - self.clock.now()).total_seconds())) if due else 0
            if seconds:
                message = f"Waiting; rate cooldown {seconds}s; {pending} pending."
                fields["result"] = "rate_wait"
            else:
                due = self.state.next_ready_at()
                seconds = max(0, math.ceil((due - self.clock.now()).total_seconds())) if due else 0
                message = f"Waiting; retry/queue {seconds}s; {pending} pending."
                fields["result"] = "queued"
        else:
            message = "Watching for images; 0 pending."
            fields["result"] = "idle"
        log_event(self.logger, logging.INFO, "watch_heartbeat", message=message, **fields)

    def _wait_with_heartbeat(self, event: threading.Event, delay: float) -> bool:
        if event.is_set():
            return True
        if self.logger.isEnabledFor(logging.INFO):
            self._heartbeat()
            delay = min(delay, max(0.001, self._next_heartbeat - self.clock.monotonic()))
        return self.clock.wait(event, delay)

    def _ensure_staging_capacity(self, candidate: StableCandidate) -> None:
        staged_jobs, _ = self._staging_usage()
        if staged_jobs >= _MAX_STAGED_JOBS:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Staging backpressure is active because 1,000 jobs are already pending; "
                "retry after pending work completes.",
            )
        try:
            free = shutil.disk_usage(self.config.paths.destination).free
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Destination free space could not be verified safely.",
            ) from None
        required = (
            candidate.size
            + self.config.limits.max_output_bytes
            + _MIN_FREE_SPACE_RESERVE
        )
        if free < required:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Destination free space is below the safe staging reserve; "
                "free space and retry the item.",
            )

    def process_path(
        self,
        path: Path,
        *,
        candidate: StableCandidate | None = None,
        shutdown_event: threading.Event | None = None,
        retry_input_rejection: bool = False,
        retry_failed_variants: bool = False,
        variant: str = "",
    ) -> ProcessResult:
        shutdown_event = shutdown_event or threading.Event()
        retry_options = {"retry_input_rejection": True} if retry_input_rejection else {}
        if retry_failed_variants:
            require_failed_variant_retry_policy(self.config)
            retry_options["retry_failed_variants"] = True
        admitted = self._admit(
            path, candidate=candidate, shutdown_event=shutdown_event,
            **({"variant": variant} if variant else {}), **retry_options,
        )
        if admitted.status not in {JobStatus.READY, JobStatus.READY_RETRY}:
            return admitted
        return self._run_until_terminal(admitted.job_id, shutdown_event)

    def process_variants(
        self, path: Path, *, shutdown_event: threading.Event | None = None,
        retry_input_rejection: bool = False,
        retry_failed_variants: bool = False,
        on_result: Callable[[ProcessResult], None] | None = None,
    ) -> tuple[ProcessResult, ...]:
        event = shutdown_event or threading.Event()
        if retry_failed_variants:
            require_failed_variant_retry_policy(self.config)
        results = []
        provider = self.config.provider
        by_variant: dict[str, ProcessResult] = {}

        def run(
            variant: str, trigger: JobRecord | None = None,
            candidate: StableCandidate | None = None,
        ) -> None:
            if trigger is not None:
                self._queue_note = f"primary {trigger.variant} failed ({trigger.error_code})"
            try:
                result = self.process_path(
                    path, candidate=candidate, shutdown_event=event,
                    retry_input_rejection=retry_input_rejection,
                    variant=variant,
                    **({"retry_failed_variants": True} if retry_failed_variants else {}),
                )
            except AppError as exc:
                if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                    raise
                log_event(self.logger, logging.ERROR, "variant_failed",
                          variant=variant, error_code=exc.code.value, message=exc.safe_message)
                result = ProcessResult("", JobStatus.FAILED, None, 0, exc.code, variant)
            finally:
                self._queue_note = None
            by_variant[variant] = result
            results.append(result)
            if on_result is not None:
                on_result(result)

        for variant in provider.primary_variants:
            run(variant)
        for variant in provider.variants:
            if variant not in provider.fallback_variants:
                continue
            if event.is_set():
                break
            self._process_fallback(path, variant, by_variant, run)
        return tuple(results)

    def _process_fallback(
        self, path: Path, fallback: str, by_variant: dict[str, ProcessResult],
        run: Callable[..., None],
    ) -> None:
        primary_variant = self.config.provider.primary_for(fallback)
        primary_result = by_variant.get(primary_variant) if primary_variant is not None else None
        if primary_result is None or not primary_result.job_id:
            return
        primary = self.state.get_job(primary_result.job_id)
        existing = self.state.find_jobs_by_source_metadata(
            os.path.normcase(primary.source_path),
            primary.size, primary.mtime_ns, variant=fallback,
        )
        if any(job.sha256 == primary.sha256 for job in existing):
            candidate = self._fallback_source_candidate(path, fallback, primary)
            if candidate is not None:
                run(fallback, candidate=candidate)
            return
        if primary.status == JobStatus.SUCCEEDED:
            self._note_fallback(fallback, primary, "succeeded")
            return
        if _fallback_trigger(primary) is None:
            if primary.status in _TERMINAL_STATUSES:
                self._note_fallback(fallback, primary, "skip")
            return
        candidate = self._fallback_source_candidate(path, fallback, primary)
        if candidate is not None:
            run(fallback, primary, candidate)

    def _fallback_source_candidate(
        self, path: Path, fallback: str, primary: JobRecord,
    ) -> StableCandidate | None:
        """Bind a fallback to the exact source version (size, mtime, SHA-256) its primary used."""
        if _source_matches_job(path, primary):
            return StableCandidate(path, primary.size, primary.mtime_ns)
        log_event(
            self.logger, logging.WARNING, "fallback_skipped_source_changed",
            variant=fallback, primary_variant=primary.variant, primary_job_id=primary.job_id,
            message=(
                f"Fallback {fallback} skipped: the source changed after primary "
                f"{primary.variant} ran, so the fallback would not describe the failed version."
            ),
        )
        return None

    def _recover_completed_artifacts(self) -> None:
        for original in self.state.list_jobs():
            if original.status == JobStatus.SUCCEEDED or self._has_published_failure_binding(original):
                self._persisted_result_for(original.job_id)
                continue
            if original.status not in {
                JobStatus.RESPONSE_STAGED,
                JobStatus.OUTPUT_VERIFIED,
                JobStatus.PUBLISHED,
            }:
                continue
            temp_path: Path | None = None
            try:
                job = self.state.get_job(original.job_id)
                staged_path = self._validated_staged_cleanup_path(
                    job.job_id, Path(job.staged_path)
                )
                snapshot = self._snapshot_from_job(job)
                if job.status == JobStatus.RESPONSE_STAGED:
                    temp_path = self._provider_temp_path(job, must_exist=True)
                    validate_output_png(temp_path, self.config.limits)
                    self.state.mark_output_verified(job.job_id)
                    job = self.state.get_job(job.job_id)
                if job.status == JobStatus.OUTPUT_VERIFIED:
                    temp_path = self._provider_temp_path(job)
                    self._publish_verified(job, snapshot)
                    job = self.state.get_job(job.job_id)
                if job.status == JobStatus.PUBLISHED:
                    self._verify_published(job)
            except _StagedCleanupOwnershipError as exc:
                current = self.state.get_job(original.job_id)
                self._record_staged_cleanup_ownership_failure(
                    current,
                    exc,
                    stage="recovery",
                )
                continue
            except AppError as exc:
                current = self.state.get_job(original.job_id)
                self._quarantine(
                    current,
                    "recovery",
                    exc,
                    self.state.attempt_count(current.job_id),
                )
                self.state.mark_failed(current.job_id, exc.code)
                try:
                    self._remove_job_staged_file(
                        current.job_id, Path(current.staged_path)
                    )
                except _StagedCleanupOwnershipError as cleanup_error:
                    current = self.state.get_job(current.job_id)
                    self._record_staged_cleanup_ownership_failure(
                        current,
                        cleanup_error,
                        stage="recovery",
                    )
                self._cleanup_current_provider_temp(current.job_id)
                continue
            if job.status == JobStatus.PUBLISHED:
                self._remove_staged_file(staged_path)
                self.state.mark_succeeded(job.job_id)
                self._cleanup_current_provider_temp(job.job_id)
                self._report_terminal(job.job_id)

    def _verify_published(self, job: JobRecord) -> Path:
        if not job.output_name or not job.output_sha256:
            raise AppError(
                ErrorCode.OUTPUT_INVALID,
                "A published output record is incomplete.",
            )
        final_path = contained_leaf(
            self.config.paths.destination,
            job.output_name,
            suffix=".png",
        )
        if not os.path.lexists(final_path):
            raise AppError(ErrorCode.STATE_FAILED, PUBLISHED_OUTPUT_MISSING)
        try:
            before = final_path.stat(follow_symlinks=False)
            if not self._is_safe_regular_staged_stat(before) or before.st_nlink != 1:
                raise AppError(ErrorCode.STATE_FAILED, "The published output is not a private regular file.")
            if before.st_size > self.config.limits.max_output_bytes:
                raise AppError(ErrorCode.OUTPUT_LIMIT_EXCEEDED, "The image exceeds the configured byte limit.")
            output_identity = replace(job, size=before.st_size, sha256=job.output_sha256)
            self._stream_verified_staged_identity(
                final_path, before, output_identity, enforce_current_limit=False,
            )
            validate_output_png(final_path, self.config.limits)
            contained_leaf(self.config.paths.destination, job.output_name, suffix=".png", must_exist=True)
            self._stream_verified_staged_identity(
                final_path, before, output_identity, enforce_current_limit=False,
            )
        except AppError as exc:
            if exc.code == ErrorCode.STATE_FAILED and exc.safe_message.startswith("The staged input"):
                raise AppError(
                    ErrorCode.OUTPUT_INVALID, "The published output does not match durable state.",
                ) from None
            raise
        except OSError:
            raise AppError(
                ErrorCode.PUBLICATION_FAILED,
                "The published output cannot be verified.",
            ) from None
        return final_path

    def _reconcile_valid_published_output(
        self,
        job_id: str,
    ) -> Path | None:
        while True:
            current = self.state.get_job(job_id)
            if current.status == JobStatus.OUTPUT_VERIFIED:
                try:
                    final_path = self._verify_published(current)
                    self.state.mark_published(job_id)
                except AppError:
                    return None
                continue
            if current.status == JobStatus.PUBLISHED:
                try:
                    return self._verify_published(current)
                except AppError:
                    return None
            if current.status == JobStatus.SUCCEEDED:
                try:
                    return self._verify_published(current)
                except AppError:
                    return None
            return None

    def _publish_verified(
        self,
        job: JobRecord,
        snapshot: SourceSnapshot,
    ) -> Path:
        temp_path = self._provider_temp_path(job)
        if job.output_name and job.output_sha256:
            final_path = contained_leaf(
                self.config.paths.destination, job.output_name, suffix=".png"
            )
            if final_path.exists():
                try:
                    self._verify_published(job)
                except AppError as exc:
                    if exc.code != ErrorCode.OUTPUT_INVALID or not temp_path.exists():
                        raise
                    validate_output_png(temp_path, self.config.limits)
                    final_path, digest = prepare_publication(
                        temp_path,
                        self.config.paths.destination,
                        snapshot,
                        job.job_id,
                        job.variant,
                    )
                    self.state.plan_publication(
                        job.job_id, final_path.name, digest
                    )
                    job = self.state.get_job(job.job_id)
                else:
                    self.state.mark_published(job.job_id)
                    return final_path
        if not temp_path.exists():
            raise AppError(
                ErrorCode.OUTPUT_INVALID,
                "Neither the staged nor final provider output can be recovered.",
            )
        validate_output_png(temp_path, self.config.limits)
        for _ in range(100):
            if not job.output_name or not job.output_sha256:
                final_path, digest = prepare_publication(
                    temp_path, self.config.paths.destination, snapshot, job.job_id, job.variant
                )
                self.state.plan_publication(job.job_id, final_path.name, digest)
                job = self.state.get_job(job.job_id)
            else:
                final_path = contained_leaf(
                    self.config.paths.destination, job.output_name, suffix=".png"
                )
                digest = job.output_sha256
            try:
                if self._publication_hook is not None:
                    self._publication_hook("before_rename")
                promote_planned_output(temp_path, final_path, digest)
                if self._publication_hook is not None:
                    self._publication_hook("after_rename")
                self.state.mark_published(job.job_id)
                return final_path
            except FileExistsError:
                final_path, digest = prepare_publication(
                    temp_path, self.config.paths.destination, snapshot, job.job_id, job.variant
                )
                self.state.plan_publication(job.job_id, final_path.name, digest)
                job = self.state.get_job(job.job_id)
        raise AppError(
            ErrorCode.PUBLICATION_FAILED,
            "A unique destination filename could not be allocated.",
        )

    def _quarantine_recovered_dispatches(
        self, job_ids: tuple[str, ...]
    ) -> None:
        for job_id in job_ids:
            job = self.state.get_job(job_id)
            error = AppError(
                ErrorCode.PROVIDER_AMBIGUOUS,
                "The previous provider attempt ended without a definitive result.",
            )
            attempt = self.state.active_attempt(job_id)
            self._quarantine(
                job,
                "recovery",
                error,
                self.state.attempt_count(job_id),
            )
            self.state.mark_ambiguous(
                job_id,
                attempt.attempt_no,
                0,
                error.safe_message,
            )
            self._cleanup_current_provider_temp(job_id)
            try:
                self._remove_job_staged_file(
                    job.job_id, Path(job.staged_path)
                )
            except _StagedCleanupOwnershipError as cleanup_error:
                current = self.state.get_job(job.job_id)
                self._record_staged_cleanup_ownership_failure(
                    current,
                    cleanup_error,
                    stage="recovery",
                )

    def _reconcile_orphan_artifacts(self) -> None:
        jobs = self.state.list_jobs()
        for job in jobs:
            if job.job_id in self._local_restoration_checked:
                continue
            if job.status == JobStatus.FAILED and job.error_code == ErrorCode.STATE_FAILED.value:
                _, completed = self.state.dispatch_history(job)
                # Denial retains evidence, not scratch. Use the restrictive policy
                # so a later opt-in cannot erase a prior policy-denied history.
                if completed is None or _authorize_replay_history(
                    completed, mode="queued", provider=job.provider,
                    azure_response_retries=False,
                ) is not None:
                    continue
            if job.temp_name is not None:
                self._cleanup_current_provider_temp(job.job_id)
            if job.status not in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
                continue
            try:
                self._remove_job_staged_file(
                    job.job_id, Path(job.staged_path)
                )
            except _StagedCleanupOwnershipError as exc:
                current = self.state.get_job(job.job_id)
                self._record_staged_cleanup_ownership_failure(
                    current,
                    exc,
                    stage="recovery",
                )

        jobs = self.state.list_jobs()
        staged_references = {
            _path_key(job.staged_path)
            for job in jobs
        }
        self._remove_unreferenced_direct_files(
            self.staging, ".input", staged_references
        )

    @staticmethod
    def _remove_unreferenced_direct_files(
        root: Path,
        suffix: str,
        referenced: set[str],
    ) -> None:
        reject_reparse_components(root)
        try:
            entries = list(os.scandir(root))
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Internal recovery artifacts could not be enumerated safely.",
            ) from None
        for entry in entries:
            if not entry.name.lower().endswith(suffix):
                continue
            candidate = Path(entry.path)
            candidate_key = _path_key(candidate)
            if _path_key(candidate.parent) != _path_key(root):
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "An internal recovery artifact escaped its configured directory.",
                )
            if candidate_key in referenced:
                contained_leaf(root, entry.name, suffix=suffix, must_exist=True)
                continue
            try:
                attributes = entry.stat(follow_symlinks=False)
                is_reparse = bool(
                    getattr(attributes, "st_file_attributes", 0)
                    & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
                )
                if not entry.is_file(follow_symlinks=False) and not entry.is_symlink():
                    continue
                if is_reparse or entry.is_symlink():
                    candidate.unlink()
                else:
                    safe = contained_leaf(
                        root, entry.name, suffix=suffix, must_exist=True
                    )
                    reject_reparse_components(root)
                    safe.unlink()
            except AppError:
                raise
            except OSError:
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "An unreferenced recovery artifact could not be removed safely.",
                ) from None

    def _run_until_terminal(
        self, target_job_id: str, shutdown_event: threading.Event
    ) -> ProcessResult:
        while True:
            target = self.state.get_job(target_job_id)
            if target.status not in {JobStatus.READY, JobStatus.READY_RETRY}:
                return self._persisted_result_for(target_job_id)
            if shutdown_event.is_set():
                raise AppError(
                    ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted."
                )
            if self._dispatch_one(shutdown_event, target_job_id):
                continue
            delay = self._dispatch_delay(target_job_id)
            if (
                self.config.provider.requests_per_minute is None
                and self.state.provider_not_before(None) is None
            ):
                delay = min(delay, self.config.watch.poll_interval_seconds)
            if self._wait_with_heartbeat(shutdown_event, delay or self.config.watch.poll_interval_seconds):
                raise AppError(
                    ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted."
                )

    def _dispatch_delay(self, target_job_id: str | None = None) -> float:
        now = self.clock.now()
        provider_due = self.state.provider_not_before(self.config.provider.requests_per_minute)
        job_due = (
            self.state.next_ready_at_for(target_job_id)
            if target_job_id is not None
            else (
                None if self.state.peek_ready(now) is not None
                else self.state.next_ready_at()
            )
        )
        due = max(value for value in (now, provider_due, job_due) if value is not None)
        if provider_due is not None and provider_due > now:
            if provider_due != self._logged_rate_deadline:
                log_event(
                    self.logger, logging.INFO, "rate_limit_wait",
                    job_id=target_job_id, result="queued",
                    duration_ms=int((due - now).total_seconds() * 1000),
                    message=f"Waiting; rate cooldown {math.ceil((provider_due - now).total_seconds())}s.",
                )
                self._logged_rate_deadline = provider_due
        return (due - now).total_seconds()

    def _dispatch_one(
        self, shutdown_event: threading.Event, target_job_id: str | None = None,
    ) -> bool:
        self._dispatched_job_id: str | None = None
        try:
            return self._dispatch_one_impl(shutdown_event, target_job_id)
        finally:
            if self._dispatched_job_id is not None:
                self._report_terminal(self._dispatched_job_id)

    def _checked_dispatch_history(self, selected: JobRecord) -> tuple[tuple, ...] | None:
        """Deny against an unchanged snapshot before any repair or dispatch I/O."""
        history, completed = self.state.dispatch_history(selected)
        denial = (
            "invalid_history" if completed is None else
            _authorize_replay_history(
                completed, mode="queued", provider=selected.provider,
                azure_response_retries=self.config.retry.azure_response_retries,
            )
        )
        if denial is None:
            return history
        if not self.state.deny_queued_replay(selected, history):
            raise AppError(ErrorCode.STATE_FAILED, "The selected dispatch job or history changed.")
        log_event(
            self.logger, logging.ERROR, "provider_replay_denied",
            job_id=selected.job_id, variant=selected.variant or None,
            error_code=ErrorCode.STATE_FAILED.value,
            message="Dispatch denied: " + denial + ".",
        )
        return None

    def _dispatch_one_impl(
        self,
        shutdown_event: threading.Event,
        target_job_id: str | None = None,
    ) -> bool:
        access_token = None
        if shutdown_event.is_set():
            return False
        if self._dispatch_delay(target_job_id) > 0:
            return False
        selected = self.state.peek_ready(self.clock.now(), target_job_id)
        if selected is None:
            return False
        history = self._checked_dispatch_history(selected)
        if history is None:
            self._dispatched_job_id = selected.job_id
            return True
        if self.config.provider.authentication == "interactive":
            if self.state.peek_ready(self.clock.now(), target_job_id) is None:
                return False
            log_event(
                self.logger, logging.INFO, "authentication_wait",
                message="Checking sign-in; waiting for token refresh if required.",
            )
            authentication_started = self.clock.monotonic()
            broker = self.auth_session if isinstance(self.auth_session, AuthenticationBroker) else None
            previous_auth_callback = None
            if broker is not None:
                previous_auth_callback = broker.on_wait

                def on_auth_wait() -> None:
                    self._heartbeat(authentication_started=authentication_started)
                    if previous_auth_callback is not None:
                        previous_auth_callback()

                broker.on_wait = on_auth_wait
            try:
                access_token = self.auth_session.acquire(
                    self.config.provider.request_timeout_seconds + TOKEN_ALLOWANCE_SECONDS,
                    ShutdownDeadline(shutdown_event.is_set, self.config.shutdown.timeout_seconds),
                )
            finally:
                if broker is not None:
                    broker.on_wait = previous_auth_callback
            access_token.require_valid(
                self.config.provider.request_timeout_seconds + TOKEN_ALLOWANCE_SECONDS
            )
            if shutdown_event.is_set():
                raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Processing was interrupted.")
        now = self.clock.now()
        claimed = self.state.claim_ready(selected.job_id, now)
        if claimed is None:
            return False
        self._dispatched_job_id = claimed.job_id
        if replace(claimed, status=selected.status, updated_at=selected.updated_at) != selected:
            raise AppError(ErrorCode.STATE_FAILED, "The authorized dispatch job changed during claim.")
        attempts_before = self.state.attempt_count(claimed.job_id)
        try:
            self._validated_staged_cleanup_path(
                claimed.job_id, Path(claimed.staged_path)
            )
        except _StagedCleanupOwnershipError as exc:
            self._quarantine(claimed, "state", exc, attempts_before)
            self.state.mark_failed(claimed.job_id, exc.code)
            raise
        try:
            identity = self._request_identity(claimed.variant)
        except AppError:
            identity = None
        if (
            identity is None
            or claimed.variant != identity.variant
            or claimed.provider != identity.provider
            or claimed.model != identity.model
            or claimed.prompt_hash != identity.prompt_hash
        ):
            error = AppError(
                ErrorCode.STATE_FAILED,
                "The claimed job request identity does not match current configuration.",
            )
            self._quarantine(claimed, "state", error, attempts_before)
            self.state.mark_failed(claimed.job_id, error.code)
            self._remove_job_staged_file(
                claimed.job_id, Path(claimed.staged_path)
            )
            return True
        if attempts_before >= self.config.retry.max_attempts:
            # Completed history must not become replayable if quarantine fails.
            # Retain its exact last-response provenance for explicit recovery.
            last = self.state.mark_queued_retry_exhausted(claimed, history)
            error = AppError(last.error_code, last.safe_message)
            self._quarantine(claimed, "provider", error, attempts_before)
            self._remove_job_staged_file(
                claimed.job_id, Path(claimed.staged_path)
            )
            return True
        try:
            snapshot = self._verify_staged_input(claimed)
        except AppError as exc:
            self._quarantine(claimed, "input", exc, attempts_before)
            self.state.mark_failed(claimed.job_id, exc.code)
            self._remove_job_staged_file(
                claimed.job_id, Path(claimed.staged_path)
            )
            return True
        attempt = self.state.begin_attempt_checked(claimed, history)
        request = TransformRequest(
            job_id=claimed.job_id,
            source=snapshot,
            prompt=self.config.provider.prompt_for(claimed.variant),
            model=self.config.provider.model,
            max_output_bytes=self.config.limits.max_output_bytes,
            max_input_bytes=self.config.limits.max_input_bytes,
            max_width=self.config.limits.max_width,
            max_height=self.config.limits.max_height,
            max_pixels=self.config.limits.max_pixels,
            variant=claimed.variant,
            **({"azure_response_retries": True} if self.config.retry.azure_response_retries else {}),
        )
        temp_path = contained_leaf(
            self.config.paths.destination,
            f".{uuid.uuid4().hex}.tmp",
            suffix=".tmp",
        )
        self.state.set_current_temp_name(claimed.job_id, temp_path.name)
        started = self.clock.monotonic()
        self._active_progress = (claimed, attempt.attempt_no, started)
        log_event(
            self.logger, logging.INFO, "processing_started",
            job_id=claimed.job_id, source_name=claimed.source_name,
            variant=claimed.variant or None,
            attempt_no=attempt.attempt_no, max_attempts=self.config.retry.max_attempts,
            pending_count=self.state.pending_count(),
            message=f"Processing {claimed.source_name}; attempt {attempt.attempt_no}/{self.config.retry.max_attempts}.",
        )
        deadline = ShutdownDeadline(
            shutdown_event.is_set, self.config.shutdown.timeout_seconds
        )
        previous_callback = None
        if isinstance(self.runner, SubprocessAttemptRunner):
            previous_callback = self.runner.on_wait

            def on_wait() -> None:
                self._heartbeat()
                if previous_callback is not None:
                    previous_callback()

            self.runner.on_wait = on_wait
        try:
            if access_token is None:
                result = self.runner.run(self.config.provider, request, temp_path, deadline)
            else:
                result = self.runner.run_authenticated(
                    self.config.provider, request, temp_path, deadline, access_token,
                )
        except KeyboardInterrupt:
            duration = int((self.clock.monotonic() - started) * 1000)
            message = "The provider attempt was interrupted with an uncertain result."
            self._quarantine(
                claimed,
                "provider",
                AppError(
                    ErrorCode.PROVIDER_AMBIGUOUS,
                    message,
                ),
                attempt.attempt_no,
            )
            self.state.mark_ambiguous(
                claimed.job_id,
                attempt.attempt_no,
                duration,
                message,
            )
            self._cleanup_current_provider_temp(claimed.job_id)
            self._remove_job_staged_file(
                claimed.job_id, snapshot.staged_path
            )
            raise
        finally:
            self._active_progress = None
            if isinstance(self.runner, SubprocessAttemptRunner):
                self.runner.on_wait = previous_callback
        duration = int((self.clock.monotonic() - started) * 1000)
        log_event(
            self.logger,
            logging.INFO,
            "provider_attempt",
            job_id=claimed.job_id,
            variant=claimed.variant or None,
            source_name=claimed.source_name,
            source_sha12=claimed.sha256[:12],
            provider=self.config.provider.name,
            model=self.config.provider.model,
            attempt_no=attempt.attempt_no,
            duration_ms=duration,
            result=result.outcome,
            error_code=result.error_code.value if result.error_code else None,
            message=result.safe_message,
        )
        if result.outcome == WorkerOutcome.SUCCEEDED.value:
            try:
                self.state.mark_response_staged(
                    claimed.job_id,
                    attempt.attempt_no,
                    duration,
                    temp_path.name,
                    result.provider_request_id,
                )
                validate_output_png(temp_path, self.config.limits)
                self.state.mark_output_verified(claimed.job_id)
                job = self.state.get_job(claimed.job_id)
                self._publish_verified(job, snapshot)
            except AppError as exc:
                current = self.state.get_job(claimed.job_id)
                if current.status in {
                    JobStatus.OUTPUT_VERIFIED,
                    JobStatus.PUBLISHED,
                } and self._reconcile_valid_published_output(
                    claimed.job_id
                ):
                    current = self.state.get_job(claimed.job_id)
                    self._cleanup_current_provider_temp(claimed.job_id)
                    if current.status == JobStatus.PUBLISHED:
                        self._remove_job_staged_file(
                            claimed.job_id, snapshot.staged_path
                        )
                        self.state.mark_succeeded(claimed.job_id)
                    elif current.status == JobStatus.SUCCEEDED:
                        self._remove_job_staged_file(
                            claimed.job_id, snapshot.staged_path
                        )
                    return True
                if current.status in {
                    JobStatus.RESPONSE_STAGED,
                    JobStatus.OUTPUT_VERIFIED,
                    JobStatus.PUBLISHED,
                }:
                    self._quarantine(
                        current, "output", exc, attempt.attempt_no
                    )
                    self.state.mark_failed(claimed.job_id, exc.code)
                    self._cleanup_current_provider_temp(claimed.job_id)
                    self._remove_job_staged_file(
                        claimed.job_id, snapshot.staged_path
                    )
                return True
            self._remove_job_staged_file(
                claimed.job_id, snapshot.staged_path
            )
            self.state.mark_succeeded(claimed.job_id)
            self._cleanup_current_provider_temp(claimed.job_id)
            return True

        error = AppError(
            result.error_code or ErrorCode.PROVIDER_PERMANENT,
            result.safe_message or "The provider attempt failed.",
        )
        if result.outcome == WorkerOutcome.RETRYABLE.value:
            delay = min(
                self.config.retry.initial_delay_seconds
                * (2 ** (attempt.attempt_no - 1)),
                self.config.retry.max_delay_seconds,
            )
            cooldown = (
                max(delay, result.retry_after_seconds)
                if result.retry_after_seconds is not None else None
            )
            next_at = (
                self.clock.now() + timedelta(seconds=max(delay, cooldown or 0))
            ).isoformat()
            if attempt.attempt_no < self.config.retry.max_attempts:
                self.state.mark_retryable(
                    claimed.job_id,
                    attempt.attempt_no,
                    duration,
                    next_at,
                    error.code,
                    error.safe_message,
                    retry_after_seconds=cooldown,
                )
                self._cleanup_current_provider_temp(claimed.job_id)
                gate = self.state.provider_not_before(self.config.provider.requests_per_minute)
                not_before = datetime.fromisoformat(next_at)
                if gate is not None:
                    not_before = max(not_before, gate)
                log_event(
                    self.logger, logging.INFO, "provider_retry_queued",
                    job_id=claimed.job_id, variant=claimed.variant or None,
                    attempt_no=attempt.attempt_no + 1, max_attempts=self.config.retry.max_attempts,
                    message=(
                        f"Queued retry attempt {attempt.attempt_no + 1}/{self.config.retry.max_attempts}; "
                        f"not before {not_before.isoformat()}; shared provider pacing still applies."
                    ),
                )
                return True
            # Exhaustion is terminal even if quarantine fails or the configured
            # budget later increases. Commit the outcome and cooldown together.
            if cooldown is not None or self.config.retry.azure_response_retries:
                self.state.mark_retry_exhausted(
                    claimed.job_id, attempt.attempt_no, duration, next_at,
                    error.code, error.safe_message, retry_after_seconds=cooldown,
                )
            else:
                self.state.mark_failed(
                    claimed.job_id, error.code, attempt_no=attempt.attempt_no,
                    duration_ms=duration, safe_message=error.safe_message,
                )
            self._quarantine(claimed, "provider", error, attempt.attempt_no)
            status = JobStatus.FAILED
        elif result.outcome == WorkerOutcome.AMBIGUOUS.value:
            self._quarantine(
                claimed,
                "provider",
                error,
                attempt.attempt_no,
            )
            self.state.mark_ambiguous(
                claimed.job_id, attempt.attempt_no, duration, error.safe_message
            )
            status = JobStatus.AMBIGUOUS
        else:
            # Terminal HTTP cooldown must survive fallible quarantine I/O,
            # without ever granting a READY_RETRY transition to a refusal.
            cooldown = (
                max(
                    min(self.config.retry.initial_delay_seconds * (2 ** (attempt.attempt_no - 1)),
                        self.config.retry.max_delay_seconds),
                    result.retry_after_seconds,
                ) if result.retry_after_seconds is not None else None
            )
            self.state.mark_failed(
                claimed.job_id,
                error.code,
                attempt_no=attempt.attempt_no,
                duration_ms=duration,
                safe_message=error.safe_message,
                retry_after_seconds=cooldown,
            )
            if error.code == ErrorCode.AUTHENTICATION_FAILED:
                try:
                    self._quarantine(claimed, "provider", error, attempt.attempt_no)
                    self._cleanup_current_provider_temp(claimed.job_id)
                    self._remove_job_staged_file(claimed.job_id, snapshot.staged_path)
                except (AppError, OSError):
                    raise AppError(
                        ErrorCode.AUTHENTICATION_FAILED,
                        "Failure artifact cleanup failed; the durable failure is retained. " + error.safe_message,
                    ) from None
                raise error
            self._quarantine(claimed, "provider", error, attempt.attempt_no)
            status = JobStatus.FAILED
        if status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
            self._cleanup_current_provider_temp(claimed.job_id)
            self._remove_job_staged_file(
                claimed.job_id, snapshot.staged_path
            )
        if error.code == ErrorCode.AUTHENTICATION_FAILED:
            raise error
        return True

    def _quarantine(
        self,
        job: JobRecord,
        stage: str,
        error: AppError,
        attempts: int,
    ) -> Path:
        return write_failure_record(
            FailureRecord(
                job_id=job.job_id,
                source_name=job.source_name,
                size=job.size,
                mtime_ns=job.mtime_ns,
                sha256=job.sha256,
                stage=stage,
                error_code=error.code,
                safe_message=error.safe_message,
                attempts=attempts,
                first_seen_at=job.created_at,
                failed_at=self.clock.timestamp(),
                variant=job.variant,
            ),
            self.config.paths.quarantine,
        )

    def watch(
        self,
        *,
        shutdown_event: threading.Event | None = None,
        max_cycles: int | None = None,
        retry_failed_variants: bool = False,
        retry_input_rejection: bool = False,
    ) -> None:
        retry_options = {}
        if retry_failed_variants:
            require_failed_variant_retry_policy(self.config)
            retry_options["retry_failed_variants"] = True
        if retry_input_rejection:
            retry_options["retry_input_rejection"] = True
        log_event(self.logger, logging.INFO, "watch_started", message="Watching for images.")
        try:
            self._watch(shutdown_event=shutdown_event, max_cycles=max_cycles, **retry_options)
        finally:
            log_event(self.logger, logging.INFO, "watch_stopped", message="Watcher stopped.")

    def _admit_watch_variants(
        self, candidate: StableCandidate, event: threading.Event, *,
        retry_failed_variants: bool = False, retry_input_rejection: bool = False,
        reconsidered: set[tuple[str, int, int, str]] | None = None,
    ) -> bool:
        settled = True
        options = {
            "retry_failed_variants": retry_failed_variants,
            "retry_input_rejection": retry_input_rejection,
            "reconsidered": reconsidered,
        }
        for variant in self.config.provider.primary_variants:
            if event.is_set():
                return False
            if not self._admit_watch_variant(candidate, event, variant, **options):
                settled = False
        for variant in self.config.provider.variants:
            if variant not in self.config.provider.fallback_variants:
                continue
            if event.is_set():
                return False
            if not self._consider_watch_fallback(candidate, event, variant, **options):
                settled = False
        return settled

    def _source_jobs(self, candidate: StableCandidate, variant: str) -> list[JobRecord]:
        return self.state.find_jobs_by_source_metadata(
            os.path.normcase(os.path.abspath(candidate.path)),
            candidate.size, candidate.mtime_ns, variant=variant,
        )

    def _fallback_decision(
        self, candidate: StableCandidate, fallback: str
    ) -> tuple[str, JobRecord | None]:
        """Classify a configured fallback for one source version.

        Returns ("existing", job) when a fallback job already exists (normal duplicate handling
        applies), ("trigger", primary) when the primary failed with a provider rejection,
        ("wait", primary) while the primary is not terminal, ("succeeded", primary) when no
        fallback is needed, and ("skip", primary-or-None) otherwise.
        """
        existing = self._source_jobs(candidate, fallback)
        primary_variant = self.config.provider.primary_for(fallback)
        primaries = self._source_jobs(candidate, primary_variant) if primary_variant else []
        if len({job.sha256 for job in primaries}) > 1:
            # Same path/size/mtime seen with different content: the current SHA-256 decides
            # which primary describes the version on disk (creation order is not reliable).
            current = _file_sha256(candidate.path, candidate.size)
            primaries = [job for job in primaries if job.sha256 == current]
            if not primaries:
                return "skip", None
        if primaries:
            # Source version is path + size + mtime + SHA-256: only a fallback job for the same
            # content as the latest primary is "existing".
            existing = [job for job in existing if job.sha256 == primaries[-1].sha256]
        if existing:
            return "existing", existing[-1]
        if primary_variant is None or not primaries:
            return "skip", None
        primary = primaries[-1]
        if primary.status == JobStatus.SUCCEEDED:
            return "succeeded", primary
        if primary.status not in _TERMINAL_STATUSES:
            return "wait", primary
        if _fallback_trigger(primary) is not None:
            return "trigger", primary
        return "skip", primary

    def _note_fallback(self, fallback: str, primary: JobRecord, kind: str) -> None:
        key = (primary.job_id, kind)
        if key in self._fallback_notes:
            return
        self._fallback_notes.add(key)
        if kind == "succeeded":
            log_event(self.logger, logging.INFO, "fallback_not_needed",
                      job_id=primary.job_id, variant=fallback, source_name=primary.source_name,
                      message="Fallback not needed; primary succeeded.")
        else:
            log_event(self.logger, logging.INFO, "fallback_not_eligible",
                      job_id=primary.job_id, variant=fallback, source_name=primary.source_name,
                      error_code=primary.error_code,
                      message=f"Fallback not run; primary {primary.variant} ended "
                              f"{primary.status.value} ({primary.error_code or 'no error code'}), "
                              "which is not a provider rejection.")

    def _consider_watch_fallback(
        self, candidate: StableCandidate, event: threading.Event, fallback: str, *,
        retry_failed_variants: bool = False, retry_input_rejection: bool = False,
        reconsidered: set[tuple[str, int, int, str]] | None = None,
        log_failures: bool = True,
    ) -> bool:
        options = {
            "retry_failed_variants": retry_failed_variants,
            "retry_input_rejection": retry_input_rejection,
            "reconsidered": reconsidered,
            "log_failures": log_failures,
        }
        version = (_path_key(candidate.path), candidate.size, candidate.mtime_ns, fallback)
        try:
            decision, job = self._fallback_decision(candidate, fallback)
        except AppError as exc:
            if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                raise
            if log_failures:
                log_event(self.logger, logging.ERROR, "watch_item_failed",
                          source_name=candidate.path.name, variant=fallback,
                          error_code=exc.code.value, message=exc.safe_message)
            return True
        if decision == "existing":
            self._pending_fallbacks.pop(version, None)
            return self._admit_watch_variant(candidate, event, fallback, **options)
        if decision == "trigger":
            self._pending_fallbacks.pop(version, None)
            return self._admit_watch_variant(candidate, event, fallback, trigger=job, **options)
        if decision == "wait":
            self._pending_fallbacks[version] = (candidate, job.job_id)
        elif decision == "succeeded":
            self._pending_fallbacks.pop(version, None)
            self._note_fallback(fallback, job, "succeeded")
        else:
            self._pending_fallbacks.pop(version, None)
            if job is not None:
                self._note_fallback(fallback, job, "skip")
        return True

    def _admit_pending_fallbacks(self, event: threading.Event) -> None:
        for version, (candidate, primary_job_id) in list(self._pending_fallbacks.items()):
            if event.is_set():
                return
            try:
                primary = self.state.get_job(primary_job_id)
            except AppError as exc:
                if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                    raise
                self._pending_fallbacks.pop(version, None)
                self._pending_fallback_failures.pop(version, None)
                continue
            if primary.status not in _TERMINAL_STATUSES:
                continue
            self._pending_fallbacks.pop(version, None)
            failures = self._pending_fallback_failures.pop(version, 0)
            attempt = failures + 1
            # Log attempts 1, 2, 4, 8, ...: the first failure is always visible, repeats of the
            # same unchanged version are bounded to O(log n) entries while retrying every cycle.
            log_failure = attempt & (attempt - 1) == 0
            if self._consider_watch_fallback(
                candidate, event, version[3], log_failures=log_failure,
            ):
                if version not in self._pending_fallbacks and failures:
                    log_event(self.logger, logging.INFO, "fallback_pending_admitted",
                              source_name=candidate.path.name, variant=version[3],
                              attempt_no=attempt,
                              message=f"Pending fallback admitted after {failures} failed "
                                      "attempt(s).")
                continue
            if version in self._pending_fallbacks or event.is_set():
                if version in self._pending_fallbacks:
                    self._pending_fallback_failures[version] = attempt
                continue
            # Admission failed transiently (locked source, staging backpressure, low disk). The
            # scanner already acknowledged this candidate, so keep the fallback pending and retry
            # next cycle -- unless the source itself no longer matches the candidate.
            try:
                info = os.stat(candidate.path)
                unchanged = (info.st_size, info.st_mtime_ns) == (candidate.size, candidate.mtime_ns)
            except OSError:
                unchanged = False
            if unchanged:
                self._pending_fallbacks[version] = (candidate, primary_job_id)
                self._pending_fallback_failures[version] = attempt
            else:
                log_event(self.logger, logging.INFO, "fallback_pending_dropped",
                          source_name=candidate.path.name, variant=version[3],
                          message="Pending fallback dropped: the source changed or was removed; "
                                  "the new version is evaluated on its own.")

    def _admit_watch_variant(
        self, candidate: StableCandidate, event: threading.Event, variant: str, *,
        retry_failed_variants: bool = False, retry_input_rejection: bool = False,
        reconsidered: set[tuple[str, int, int, str]] | None = None,
        trigger: JobRecord | None = None,
        log_failures: bool = True,
    ) -> bool:
        settled = True
        version = (_path_key(candidate.path), candidate.size, candidate.mtime_ns, variant)
        retry_options = {}
        if retry_failed_variants and (reconsidered is None or version not in reconsidered):
            retry_options["retry_failed_variants"] = True
        if retry_input_rejection:
            retry_options["retry_input_rejection"] = True
        if trigger is not None:
            self._queue_note = (
                f"primary {trigger.variant} failed ({trigger.error_code})"
            )
        try:
            result = self._admit(
                candidate.path, candidate=candidate, shutdown_event=event, variant=variant,
                **retry_options,
            )
        except AppError as exc:
            if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                raise
            if log_failures:
                log_event(self.logger, logging.ERROR, "watch_item_failed",
                          source_name=candidate.path.name, variant=variant,
                          error_code=exc.code.value, message=exc.safe_message)
            evidence_error = isinstance(exc, (_MpoRecoveryEvidenceError, _ResponseRecoveryEvidenceError))
            if exc.code not in _ACKNOWLEDGED_WATCH_ADMISSION_ERRORS and not evidence_error:
                settled = False
            elif reconsidered is not None:
                reconsidered.add(version)
        except OSError:
            settled = False
            if log_failures:
                log_event(self.logger, logging.WARNING, "watch_item_transient",
                          source_name=candidate.path.name, variant=variant,
                          error_code=ErrorCode.SOURCE_CHANGED.value,
                          message="The input file is temporarily unavailable.")
        else:
            if reconsidered is not None:
                reconsidered.add(version)
            if result.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
                self._report_terminal(result.job_id)
            elif result.status == JobStatus.SUCCEEDED:
                log_event(self.logger, logging.INFO, "watch_item_skipped",
                          job_id=result.job_id, variant=variant,
                          message="Already processed this prompt variant; skipped.")
        finally:
            self._queue_note = None
        return settled

    def _watch(
        self, *, shutdown_event: threading.Event | None, max_cycles: int | None,
        retry_failed_variants: bool = False, retry_input_rejection: bool = False,
    ) -> None:
        shutdown_event = shutdown_event or threading.Event()
        scanner = FolderScanner(
            self.config.paths.source, self.config.watch.stable_seconds,
            on_observation=lambda candidate: log_event(
                self.logger, logging.INFO, "source_detected", source_name=candidate.path.name,
                message=f"Detected {candidate.path.name}; waiting for a stable file.",
            ),
        )
        cycles = 0
        reconsidered: set[tuple[str, int, int, str]] = set()
        while not shutdown_event.is_set():
            for candidate in scanner.observe(self.clock.monotonic()):
                if shutdown_event.is_set():
                    break
                if self.config.provider.prompts is not None or retry_failed_variants or retry_input_rejection:
                    retry_options = {}
                    if retry_failed_variants:
                        retry_options["retry_failed_variants"] = True
                    if retry_input_rejection:
                        retry_options["retry_input_rejection"] = True
                    if self._admit_watch_variants(
                        candidate, shutdown_event,
                        **({"reconsidered": reconsidered} if retry_failed_variants else {}),
                        **retry_options,
                    ):
                        scanner.acknowledge(candidate)
                    continue
                try:
                    admitted = self._admit(
                        candidate.path,
                        candidate=candidate,
                        shutdown_event=shutdown_event,
                    )
                except AppError as exc:
                    if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                        raise
                    log_event(
                        self.logger,
                        logging.ERROR,
                        "watch_item_failed",
                        source_name=candidate.path.name,
                        error_code=exc.code.value,
                        message=exc.safe_message,
                    )
                    if exc.code in _ACKNOWLEDGED_WATCH_ADMISSION_ERRORS or isinstance(exc, _MpoRecoveryEvidenceError):
                        scanner.acknowledge(candidate)
                except (PermissionError, OSError):
                    log_event(
                        self.logger,
                        logging.WARNING,
                        "watch_item_transient",
                        source_name=candidate.path.name,
                        error_code=ErrorCode.SOURCE_CHANGED.value,
                        message="The input file is temporarily unavailable.",
                    )
                else:
                    scanner.acknowledge(candidate)
                    if admitted.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}:
                        log_event(
                            self.logger, logging.ERROR, "watch_item_failed",
                            job_id=admitted.job_id, source_name=candidate.path.name,
                            error_code=admitted.error_code.value if admitted.error_code else None,
                            message=f"Failed {candidate.path.name}; previously failed/input rejected, skipped: "
                                    f"{admitted.error_code.value if admitted.error_code else 'unknown failure'}.",
                        )
                    elif admitted.status == JobStatus.SUCCEEDED:
                        log_event(
                            self.logger, logging.INFO, "watch_item_skipped",
                            job_id=admitted.job_id, source_name=candidate.path.name,
                            message=f"Already processed {candidate.path.name}; skipped.",
                        )
            while not shutdown_event.is_set():
                try:
                    if self._pending_fallbacks:
                        self._admit_pending_fallbacks(shutdown_event)
                    if not self._dispatch_one(shutdown_event):
                        break
                    if self._pending_fallbacks:
                        self._admit_pending_fallbacks(shutdown_event)
                    if self.config.provider.requests_per_minute is not None:
                        break
                except AppError as exc:
                    if exc.code in {ErrorCode.AUTHENTICATION_FAILED, ErrorCode.SHUTDOWN_INTERRUPTED}:
                        raise
                    log_event(
                        self.logger,
                        logging.ERROR,
                        "watch_dispatch_failed",
                        error_code=exc.code.value,
                        message=exc.safe_message,
                    )
                    break
                except OSError:
                    raise AppError(
                        ErrorCode.STATE_FAILED,
                        "A durable item failure artifact could not be written.",
                    ) from None
            cycles += 1
            if max_cycles is not None and cycles >= max_cycles:
                return
            self._wait_with_heartbeat(shutdown_event, self.config.watch.poll_interval_seconds)
