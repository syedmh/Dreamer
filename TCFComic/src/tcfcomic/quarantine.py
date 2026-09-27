from __future__ import annotations

import hashlib
import json
import os
import re
import stat
import uuid
from dataclasses import asdict
from datetime import datetime
from pathlib import Path

from .domain import AppError, AttemptState, ErrorCode, FailureRecord, JobStatus, RequestIdentity
from .path_safety import contained_leaf, reject_reparse_components
from .redaction import sanitize_text
from .state import CompletedAttempt, JobRecord, require_valid_job_id
from .providers.openai import _NOT_SENT, recorded_retryable_response

_OWNED_TEMP = re.compile(r"^[0-9a-f]{32}\.[0-9a-f]{32}\.json\.tmp$")
_MAX_FAILURE_BYTES = 65_536
PUBLISHED_OUTPUT_MISSING = (
    "Previously generated output is missing; restore original PNG at expected output name. "
    "No Azure retry performed."
)
_PUBLISHED_INTEGRITY_MESSAGES = {
    ErrorCode.STATE_FAILED.value: {
        "A recovered internal file is missing.", PUBLISHED_OUTPUT_MISSING,
        "A recovered internal file is a reparse point.",
        "The published output is not a private regular file.",
    },
    ErrorCode.OUTPUT_INVALID.value: {
        "The published output does not match durable state.",
        "The output is not a valid PNG image.",
        "The output is not a single-frame PNG image.",
        "The image file cannot be read.",
    },
    ErrorCode.OUTPUT_LIMIT_EXCEEDED.value: {
        "The image exceeds the configured byte limit.",
        "The image dimensions exceed the configured limits.",
    },
    ErrorCode.PUBLICATION_FAILED.value: {"The published output cannot be verified."},
}


def _authorize_replay_history(
    attempts: tuple[CompletedAttempt, ...], *, mode: str, provider: str,
    azure_response_retries: bool,
) -> str | None:
    """Return a static denial, never reconstruct or attest historical evidence."""
    if mode not in {"queued", "explicit"}:
        raise ValueError("invalid replay authorization mode")
    if mode == "queued" and provider == "fake":
        return None
    if provider not in {"azure_openai", "openai"}:
        return "unsupported_provider"
    if mode == "explicit" and (provider != "azure_openai" or not azure_response_retries):
        return "explicit_policy_required"
    if not attempts:
        return None if mode == "queued" else "missing_response"
    states = {
        ErrorCode.PROVIDER_PERMANENT: {AttemptState.PERMANENT},
        ErrorCode.PROVIDER_RETRYABLE: {AttemptState.RETRYABLE, AttemptState.PERMANENT},
        ErrorCode.PROVIDER_AMBIGUOUS: {AttemptState.AMBIGUOUS},
    }
    last_status = None
    for number, item in enumerate(attempts, 1):
        if (
            item.job_id != attempts[0].job_id or item.attempt_no != number
            or item.state not in states.get(item.error_code, set())
        ):
            return "invalid_history"
        last_status = recorded_retryable_response(item.safe_message, item.error_code)
        if last_status is None and not (
            item.safe_message == _NOT_SENT
            and item.error_code == ErrorCode.PROVIDER_RETRYABLE
            and item.state in {AttemptState.RETRYABLE, AttemptState.PERMANENT}
        ):
            return "unverified_or_terminal_history"
    if mode == "explicit" and last_status is None:
        return "last_response_required"
    if mode == "queued" and last_status is not None and not (
        last_status in {409, 429}
        or (provider == "azure_openai" and azure_response_retries)
    ):
        return "response_policy_required"
    return None


def _read_failure_bytes(root: Path, leaf: str) -> bytes:
    path = contained_leaf(root, leaf, suffix=".json", must_exist=True)
    try:
        before = path.stat(follow_symlinks=False)
        if not stat.S_ISREG(before.st_mode) or before.st_nlink != 1:
            raise ValueError("not a private regular file")
        with path.open("rb") as stream:
            opened = os.fstat(stream.fileno())
            payload = stream.read(_MAX_FAILURE_BYTES + 1)
            after = os.fstat(stream.fileno())
        contained_leaf(root, leaf, must_exist=True)
        current = path.stat(follow_symlinks=False)
        signature = lambda s: (
            s.st_dev, s.st_ino, s.st_mode, s.st_nlink, s.st_size, s.st_mtime_ns,
        )
        if (
            len(payload) > _MAX_FAILURE_BYTES
            or not signature(before) == signature(opened) == signature(after) == signature(current)
            # Windows path stat and handle stat expose different ctime semantics.
            or before.st_ctime_ns != current.st_ctime_ns
            or opened.st_ctime_ns != after.st_ctime_ns
        ):
            raise ValueError("failure artifact changed")
        return payload
    except (OSError, ValueError):
        raise AppError(
            ErrorCode.STATE_FAILED, "The input failure provenance cannot be read safely.",
        ) from None


def read_mpo_failure_provenance(job: JobRecord, root: Path) -> bytes:
    require_valid_job_id(job.job_id)
    payload = _read_failure_bytes(root, f"{job.job_id}.json")

    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate provenance field")
            result[key] = value
        return result

    try:
        data = json.loads(payload, object_pairs_hook=unique_keys)
        expected = {
            "schema_version": 1, "job_id": job.job_id, "source_name": job.source_name,
            "source_version": {"size": job.size, "mtime_ns": job.mtime_ns, "sha256": job.sha256},
            "stage": "input", "error_code": ErrorCode.INVALID_IMAGE.value, "attempts": 0,
            "first_seen_at": job.created_at,
            **({"variant": job.variant} if job.variant else {}),
        }
        if (
            type(data) is not dict
            or any(data.get(key) != value for key, value in expected.items())
            or type(data["attempts"]) is not int
            or type(data["schema_version"]) is not int
            or type(data["source_version"]["size"]) is not int
            or type(data["source_version"]["mtime_ns"]) is not int
            or type(data.get("safe_message")) is not str
            or type(data.get("failed_at")) is not str
        ):
            raise ValueError("mismatched provenance")
    except (ValueError, TypeError, KeyError, UnicodeError, RecursionError):
        raise AppError(
            ErrorCode.STATE_FAILED, "The input failure provenance does not match the never-sent job.",
        ) from None
    return payload


def preserve_mpo_failure_audit(job: JobRecord, root: Path, payload: bytes) -> Path:
    """Publish an immutable byte-for-byte copy; a crash may leave an identical copy."""
    return _preserve_immutable_audit(job, root, f"{job.job_id}.mpo-input-failure.json", payload)


def read_response_failure_provenance(
    job: JobRecord, root: Path, attempts: tuple[CompletedAttempt, ...],
) -> bytes:
    require_valid_job_id(job.job_id)
    payload = _read_failure_bytes(root, f"{job.job_id}.json")

    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate provenance field")
            result[key] = value
        return result

    try:
        if not attempts:
            raise ValueError("missing completed attempt")
        last = attempts[-1]
        states = {
            ErrorCode.PROVIDER_PERMANENT: {AttemptState.PERMANENT},
            ErrorCode.PROVIDER_RETRYABLE: {AttemptState.RETRYABLE, AttemptState.PERMANENT},
            ErrorCode.PROVIDER_AMBIGUOUS: {AttemptState.AMBIGUOUS},
        }
        status = recorded_retryable_response(last.safe_message, last.error_code)
        if (
            job.provider != "azure_openai"
            or job.status not in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
            or last.job_id != job.job_id or last.attempt_no != len(attempts)
            or last.error_code.value != job.error_code
            or last.state not in states.get(last.error_code, set())
            or status is None
            or (job.status == JobStatus.AMBIGUOUS and (
                last.error_code != ErrorCode.PROVIDER_AMBIGUOUS
                or not (status == 408 or 500 <= status <= 599)
            ))
            or _authorize_replay_history(
                attempts, mode="explicit", provider=job.provider, azure_response_retries=True,
            ) is not None
        ):
            raise ValueError("not a qualifying response")
        data = json.loads(payload, object_pairs_hook=unique_keys)
        expected = {
            "schema_version": 1, "job_id": job.job_id, "source_name": job.source_name,
            "source_version": {"size": job.size, "mtime_ns": job.mtime_ns, "sha256": job.sha256},
            "stage": "provider", "error_code": job.error_code,
            "attempts": len(attempts), "safe_message": last.safe_message,
            "first_seen_at": job.created_at,
            **({"variant": job.variant} if job.variant else {}),
        }
        if (
            type(data) is not dict or set(data) != set(expected) | {"failed_at"}
            or any(data.get(key) != value for key, value in expected.items())
            or type(data["attempts"]) is not int or type(data["schema_version"]) is not int
            or type(data["source_version"]) is not dict
            or type(data["source_version"]["size"]) is not int
            or type(data["source_version"]["mtime_ns"]) is not int
            or type(data["failed_at"]) is not str or len(data["failed_at"]) > 64
        ):
            raise ValueError("mismatched failure")
        failed_at = datetime.fromisoformat(data["failed_at"])
        created_at = datetime.fromisoformat(job.created_at)
        if (
            failed_at.tzinfo is None or created_at.tzinfo is None
            or failed_at < created_at or failed_at < datetime.fromisoformat(last.started_at)
        ):
            raise ValueError("invalid failure time")
    except (ValueError, TypeError, KeyError, UnicodeError, RecursionError, OverflowError):
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Recovery skipped: permanent, unclassified historical evidence, or no matching definitive HTTP response.",
        ) from None
    return payload


def preserve_response_retry_audits(
    job: JobRecord, root: Path, identity: RequestIdentity, provenance: bytes,
    attempts: tuple[CompletedAttempt, ...], max_attempts: int,
) -> tuple[Path, Path]:
    def canonical(value: object) -> bytes:
        return (json.dumps(
            value, ensure_ascii=True, sort_keys=True, separators=(",", ":"),
        ) + "\n").encode("utf-8")

    def fingerprint(provider: str, model: str, prompt_hash: str, variant: str) -> str:
        return hashlib.sha256(canonical([provider, model, prompt_hash, variant])).hexdigest()

    provenance_sha = hashlib.sha256(provenance).hexdigest()
    original = _preserve_immutable_audit(
        job, root, f"{job.job_id}.response-failure.{provenance_sha}.json", provenance,
    )
    payload = canonical({
        "schema_version": 1, "kind": "prepared-failed-response-retry",
        "authorization": "explicit-flag-required-until-state-commit",
        "job_id": job.job_id, "variant": job.variant,
        "source_binding": {
            "path_key": job.source_path_key, "source_name": job.source_name,
            "size": job.size, "mtime_ns": job.mtime_ns, "sha256": job.sha256,
        },
        "original_provenance_sha256": provenance_sha,
        "attempt_history_sha256": hashlib.sha256(canonical([asdict(item) for item in attempts])).hexdigest(),
        "previous_attempt_count": len(attempts), "max_lifetime_attempts": max_attempts,
        "next_attempt": len(attempts) + 1, "created_at": job.created_at,
        "old_request_sha256": fingerprint(job.provider, job.model, job.prompt_hash, job.variant),
        "new_request_sha256": fingerprint(identity.provider, identity.model, identity.prompt_hash, identity.variant),
    })
    digest = hashlib.sha256(payload).hexdigest()
    transition = _preserve_immutable_audit(
        job, root, f"{job.job_id}.response-retry.{digest}.json", payload,
    )
    return original, transition


def read_published_failure_provenance(
    job: JobRecord, root: Path, attempts: tuple[CompletedAttempt, ...],
) -> bytes:
    """Authenticate a local published-integrity failure, not a provider rejection."""
    require_valid_job_id(job.job_id)
    payload = _read_failure_bytes(root, f"{job.job_id}.json")

    def unique_keys(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("duplicate provenance field")
            result[key] = value
        return result

    try:
        if (
            job.status != JobStatus.FAILED or job.temp_name is not None
            or not job.output_name or not job.output_sha256
            or re.fullmatch(r"[0-9a-f]{64}", job.output_sha256) is None
            or not attempts or attempts[-1].state != AttemptState.SUCCEEDED
            or any(item.job_id != job.job_id or item.attempt_no != number
                   for number, item in enumerate(attempts, 1))
        ):
            raise ValueError("not a published failure")
        data = json.loads(payload, object_pairs_hook=unique_keys)
        expected = {
            "schema_version": 1, "job_id": job.job_id, "source_name": job.source_name,
            "source_version": {"size": job.size, "mtime_ns": job.mtime_ns, "sha256": job.sha256},
            "stage": "recovery", "error_code": job.error_code, "attempts": len(attempts),
            "first_seen_at": job.created_at,
            **({"variant": job.variant} if job.variant else {}),
        }
        if (
            type(data) is not dict or set(data) != set(expected) | {"safe_message", "failed_at"}
            or any(data.get(key) != value for key, value in expected.items())
            or type(data["schema_version"]) is not int or type(data["attempts"]) is not int
            or type(data["source_version"]) is not dict
            or type(data["source_version"]["size"]) is not int
            or type(data["source_version"]["mtime_ns"]) is not int
            or type(data["safe_message"]) is not str
            or data["safe_message"] not in _PUBLISHED_INTEGRITY_MESSAGES.get(job.error_code, set())
            or type(data["failed_at"]) is not str or len(data["failed_at"]) > 64
        ):
            raise ValueError("mismatched published failure")
        failed_at = datetime.fromisoformat(data["failed_at"])
        created_at = datetime.fromisoformat(job.created_at)
        if (
            failed_at.tzinfo is None or created_at.tzinfo is None
            or failed_at < created_at
            or failed_at < datetime.fromisoformat(attempts[-1].finished_at)
        ):
            raise ValueError("invalid failure time")
    except (ValueError, TypeError, KeyError, UnicodeError, RecursionError, OverflowError):
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Restoration skipped: no matching previous-success integrity failure evidence. No Azure retry performed.",
        ) from None
    return payload


def preserve_published_restoration_audits(
    job: JobRecord, root: Path, provenance: bytes, attempts: tuple[CompletedAttempt, ...],
) -> tuple[Path, Path]:
    def canonical(value: object) -> bytes:
        return (json.dumps(value, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")

    provenance_sha = hashlib.sha256(provenance).hexdigest()
    original = _preserve_immutable_audit(
        job, root, f"{job.job_id}.published-failure.{provenance_sha}.json", provenance,
    )
    payload = canonical({
        "schema_version": 1, "kind": "prepared-local-output-restoration",
        "authorization": "verified-evidence-required-until-state-commit",
        "job_id": job.job_id, "output_name": job.output_name, "output_sha256": job.output_sha256,
        "original_provenance_sha256": provenance_sha,
        "attempt_history_sha256": hashlib.sha256(canonical([asdict(item) for item in attempts])).hexdigest(),
        "job_snapshot_sha256": hashlib.sha256(canonical(asdict(job))).hexdigest(),
        "attempt_count": len(attempts),
    })
    digest = hashlib.sha256(payload).hexdigest()
    marker = _preserve_immutable_audit(
        job, root, f"{job.job_id}.output-restoration.{digest}.json", payload,
    )
    return original, marker


def preserve_mpo_request_transition_audit(
    job: JobRecord, root: Path, identity: RequestIdentity, provenance: bytes,
) -> Path:
    def fingerprint(provider: str, model: str, prompt_hash: str, variant: str) -> str:
        encoded = json.dumps(
            [provider, model, prompt_hash] + ([variant] if variant else []),
            ensure_ascii=True, separators=(",", ":"),
        ).encode("utf-8")
        return hashlib.sha256(encoded).hexdigest()

    payload = (json.dumps({
        "schema_version": 1,
        "kind": "mpo-input-request-transition",
        "job_id": job.job_id,
        "source_binding": {
            "path_key": job.source_path_key, "size": job.size,
            "mtime_ns": job.mtime_ns, "sha256": job.sha256,
        },
        "original_provenance_sha256": hashlib.sha256(provenance).hexdigest(),
        "old_request_sha256": fingerprint(job.provider, job.model, job.prompt_hash, job.variant),
        "new_request_sha256": fingerprint(identity.provider, identity.model, identity.prompt_hash, identity.variant),
        **({"variant": job.variant} if job.variant else {}),
    }, ensure_ascii=True, sort_keys=True, separators=(",", ":")) + "\n").encode("utf-8")
    digest = hashlib.sha256(payload).hexdigest()
    return _preserve_immutable_audit(
        job, root, f"{job.job_id}.mpo-request-transition.{digest}.json", payload,
    )


def _preserve_immutable_audit(job: JobRecord, root: Path, leaf: str, payload: bytes) -> Path:
    require_valid_job_id(job.job_id)
    if len(payload) > _MAX_FAILURE_BYTES or not leaf.startswith(f"{job.job_id}."):
        raise AppError(ErrorCode.STATE_FAILED, "The input failure audit is not bounded or job-owned.")
    audit = contained_leaf(root, leaf, suffix=".json")
    if os.path.lexists(audit):
        if _read_failure_bytes(root, audit.name) != payload:
            raise AppError(ErrorCode.STATE_FAILED, "The input failure audit already contains different evidence.")
        try:
            # Windows FlushFileBuffers requires a writable handle; no bytes are changed.
            with audit.open("r+b") as stream:
                if stream.read(_MAX_FAILURE_BYTES + 1) != payload:
                    raise AppError(ErrorCode.STATE_FAILED, "The input failure audit changed before sync.")
                os.fsync(stream.fileno())
        except OSError:
            raise AppError(ErrorCode.STATE_FAILED, "The input failure audit could not be synchronized.") from None
        return audit
    temp = contained_leaf(root, f"{job.job_id}.{uuid.uuid4().hex}.json.tmp")
    created = False
    try:
        with temp.open("xb") as stream:
            created = True
            stream.write(payload)
            stream.flush()
            os.fsync(stream.fileno())
        contained_leaf(root, audit.name)
        try:
            os.link(temp, audit)
        except FileExistsError:
            if _read_failure_bytes(root, audit.name) != payload:
                raise AppError(ErrorCode.STATE_FAILED, "The input failure audit collision is not identical.") from None
    except OSError:
        raise AppError(ErrorCode.STATE_FAILED, "The input failure audit could not be preserved.") from None
    finally:
        if created:
            _remove_owned_temp(root, temp)
    return audit


def _remove_owned_temp(quarantine_root: Path, temp_path: Path) -> None:
    root = Path(os.path.abspath(quarantine_root))
    candidate = Path(os.path.abspath(temp_path))
    if candidate.parent != root or _OWNED_TEMP.fullmatch(candidate.name) is None:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "A quarantine temporary file is unsafe for cleanup.",
        )
    reject_reparse_components(root)
    try:
        safe = contained_leaf(root, candidate.name, suffix=".json.tmp")
        info = os.stat(safe, follow_symlinks=False)
    except FileNotFoundError:
        return
    except AppError:
        raise
    except OSError:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "A quarantine temporary file could not be inspected for cleanup.",
        ) from None
    is_reparse = bool(
        getattr(info, "st_file_attributes", 0)
        & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
    )
    if not stat.S_ISREG(info.st_mode) or is_reparse:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "A quarantine temporary file is not safe to remove.",
        )
    try:
        safe.unlink()
    except FileNotFoundError:
        return
    except OSError:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "A quarantine temporary file could not be removed.",
        ) from None


def reconcile_quarantine_temps(quarantine_root: Path) -> None:
    root = Path(os.path.abspath(quarantine_root))
    reject_reparse_components(root)
    try:
        entries = list(os.scandir(root))
    except OSError:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Quarantine temporary files could not be enumerated safely.",
        ) from None
    for entry in entries:
        if _OWNED_TEMP.fullmatch(entry.name) is None:
            continue
        candidate = Path(entry.path)
        if Path(os.path.abspath(candidate.parent)) != root:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A quarantine temporary file escaped its configured directory.",
            )
        try:
            info = entry.stat(follow_symlinks=False)
        except OSError:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A quarantine temporary file could not be inspected safely.",
            ) from None
        is_reparse = bool(
            getattr(info, "st_file_attributes", 0)
            & getattr(stat, "FILE_ATTRIBUTE_REPARSE_POINT", 0)
        )
        if not stat.S_ISREG(info.st_mode) or is_reparse or entry.is_symlink():
            raise AppError(
                ErrorCode.STATE_FAILED,
                "A quarantine temporary file is not safe to remove.",
            )
        _remove_owned_temp(root, candidate)


def write_failure_record(record: FailureRecord, quarantine_root: Path) -> Path:
    require_valid_job_id(record.job_id)
    quarantine_root.mkdir(parents=True, exist_ok=True)
    reject_reparse_components(quarantine_root)
    final_path = contained_leaf(
        quarantine_root, f"{record.job_id}.json", suffix=".json"
    )
    temp_path = contained_leaf(
        quarantine_root,
        f"{record.job_id}.{uuid.uuid4().hex}.json.tmp",
        suffix=".json.tmp",
    )
    payload = {
        "schema_version": 1,
        "job_id": sanitize_text(record.job_id, limit=128),
        "source_name": sanitize_text(record.source_name, limit=255),
        "source_version": {
            "size": record.size,
            "mtime_ns": record.mtime_ns,
            "sha256": record.sha256,
        },
        "stage": sanitize_text(record.stage, limit=64),
        "error_code": record.error_code.value,
        "safe_message": sanitize_text(record.safe_message),
        "attempts": record.attempts,
        "first_seen_at": sanitize_text(record.first_seen_at, limit=64),
        "failed_at": sanitize_text(record.failed_at, limit=64),
        **({"variant": record.variant} if record.variant else {}),
    }
    temp_created = False
    promoted = False
    caught_exception = False
    try:
        reject_reparse_components(quarantine_root)
        with temp_path.open("x", encoding="utf-8", newline="\n") as stream:
            temp_created = True
            json.dump(payload, stream, sort_keys=True, separators=(",", ":"))
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        reject_reparse_components(quarantine_root)
        contained_leaf(quarantine_root, final_path.name, suffix=".json")
        os.replace(temp_path, final_path)
        promoted = True
        return final_path
    except Exception:
        caught_exception = True
        raise
    finally:
        if caught_exception and temp_created and not promoted:
            _remove_owned_temp(quarantine_root, temp_path)
