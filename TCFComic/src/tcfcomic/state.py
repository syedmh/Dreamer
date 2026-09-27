from __future__ import annotations

import hashlib
import math
import os
import re
import sqlite3
import uuid
from contextlib import contextmanager
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from pathlib import Path
from typing import Callable, Iterator

from .domain import (
    AdmissionResult,
    AppError,
    AttemptState,
    ErrorCode,
    JobStatus,
    RequestIdentity,
    SourceSnapshot,
    validate_retry_after,
    valid_variant,
)
from .path_safety import validated_leaf_name
from .redaction import sanitize_text, validated_request_id
from .state_inspection import preflight_state

_JOB_ID = re.compile(r"^[0-9a-f]{32}$")
_STAGED_REFERENCE_STATUSES = (
    JobStatus.READY,
    JobStatus.READY_RETRY,
    JobStatus.DISPATCHING,
    JobStatus.RESPONSE_STAGED,
    JobStatus.OUTPUT_VERIFIED,
    JobStatus.PUBLISHED,
)


def utc_now() -> str:
    return datetime.now(UTC).isoformat()


def require_valid_job_id(job_id: str) -> str:
    if type(job_id) is not str or _JOB_ID.fullmatch(job_id) is None:
        raise AppError(
            ErrorCode.STATE_FAILED,
            "Recovered state contains an invalid job identifier.",
        )
    return job_id


@dataclass(frozen=True)
class JobRecord:
    job_id: str
    source_path: str
    source_path_key: str
    source_name: str
    size: int
    mtime_ns: int
    sha256: str
    status: JobStatus
    provider: str
    model: str
    prompt_hash: str
    staged_path: str
    temp_name: str | None
    output_name: str | None
    output_sha256: str | None
    next_attempt_at: str | None
    created_at: str
    updated_at: str
    error_code: str | None
    variant: str = ""


@dataclass(frozen=True)
class AttemptRecord:
    job_id: str
    attempt_no: int
    state: AttemptState
    started_at: str


@dataclass(frozen=True)
class CompletedAttempt:
    job_id: str
    attempt_no: int
    state: AttemptState
    started_at: str
    finished_at: str
    duration_ms: int
    error_code: ErrorCode | None
    safe_message: str | None
    provider_request_id: str | None
    retry_after_seconds: float | None


@dataclass(frozen=True)
class RecoveryReport:
    ambiguous_jobs: int
    ambiguous_job_ids: tuple[str, ...] = ()


class StateStore:
    def __init__(
        self,
        path: Path,
        *,
        transition_hook: Callable[[str], None] | None = None,
        now: Callable[[], str] = utc_now,
        named: bool = False,
    ) -> None:
        self.path = path
        self.named = named
        preflight_state(path, named=named, allow_legacy_partial=True)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.connection = sqlite3.connect(path, timeout=30, isolation_level=None)
        self.connection.row_factory = sqlite3.Row
        self._transition_hook = transition_hook
        self._now = now
        self._opened_at = now()

    def close(self) -> None:
        self.connection.close()

    def initialize(self) -> None:
        schema = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            PRAGMA synchronous=FULL;
            CREATE TABLE IF NOT EXISTS jobs (
                job_id TEXT PRIMARY KEY,
                source_path TEXT NOT NULL,
                source_path_key TEXT NOT NULL,
                source_name TEXT NOT NULL,
                size INTEGER NOT NULL,
                mtime_ns INTEGER NOT NULL,
                sha256 TEXT NOT NULL,
                status TEXT NOT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_hash TEXT NOT NULL,
                staged_path TEXT NOT NULL,
                temp_name TEXT,
                output_name TEXT,
                output_sha256 TEXT,
                next_attempt_at TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                error_code TEXT,
                UNIQUE(source_path_key, size, mtime_ns, sha256)
            );
            CREATE TABLE IF NOT EXISTS attempts (
                job_id TEXT NOT NULL REFERENCES jobs(job_id),
                attempt_no INTEGER NOT NULL,
                state TEXT NOT NULL,
                started_at TEXT NOT NULL,
                finished_at TEXT,
                duration_ms INTEGER,
                error_code TEXT,
                safe_message TEXT,
                provider_request_id TEXT,
                PRIMARY KEY(job_id, attempt_no)
            );
            """
        if self.named:
            schema = schema.replace(
                "UNIQUE(source_path_key, size, mtime_ns, sha256)",
                "variant TEXT NOT NULL, UNIQUE(source_path_key, size, mtime_ns, sha256, variant)",
            )
        self.connection.executescript(schema)
        with self._transaction():
            columns = {
                row["name"] for row in self.connection.execute("PRAGMA table_info(attempts)")
            }
            if "retry_after_seconds" not in columns:
                self.connection.execute(
                    "ALTER TABLE attempts ADD COLUMN retry_after_seconds REAL"
                )
                self._fault("initialize_after_rate_migration")

    def provider_not_before(self, requests_per_minute: int | None) -> datetime | None:
        # Every recorded attempt counts, including ambiguous and legacy attempts.
        row = self.connection.execute(
            """
            SELECT MAX(COALESCE(finished_at, MAX(started_at, ?))) AS last_finish
              FROM attempts
            """,
            (self._opened_at,),
        ).fetchone()
        deadlines: list[datetime] = []
        if row["last_finish"] is not None and requests_per_minute is not None:
            deadlines.append(
                datetime.fromisoformat(row["last_finish"])
                + timedelta(microseconds=math.ceil(60_000_000 / requests_per_minute))
            )
        for cooldown in self.connection.execute(
            """
            SELECT COALESCE(finished_at, MAX(started_at, ?)) AS finish,
                   retry_after_seconds
              FROM attempts WHERE retry_after_seconds IS NOT NULL
            """,
            (self._opened_at,),
        ):
            delay = cooldown["retry_after_seconds"]
            validate_retry_after(delay)
            deadlines.append(
                datetime.fromisoformat(cooldown["finish"]) + timedelta(seconds=delay)
            )
        return max(deadlines) if deadlines else None

    def pragma_values(self) -> tuple[str, int, int]:
        journal = self.connection.execute("PRAGMA journal_mode").fetchone()[0]
        foreign_keys = self.connection.execute("PRAGMA foreign_keys").fetchone()[0]
        synchronous = self.connection.execute("PRAGMA synchronous").fetchone()[0]
        return journal, foreign_keys, synchronous

    @contextmanager
    def _transaction(self) -> Iterator[None]:
        self.connection.execute("BEGIN IMMEDIATE")
        try:
            yield
        except BaseException:
            self.connection.execute("ROLLBACK")
            raise
        else:
            self.connection.execute("COMMIT")

    def _fault(self, point: str) -> None:
        if self._transition_hook is not None:
            self._transition_hook(point)

    @staticmethod
    def _require_updated(cursor: sqlite3.Cursor, operation: str) -> None:
        if cursor.rowcount != 1:
            raise ValueError(f"guarded state transition failed: {operation}")

    def recover(self) -> RecoveryReport:
        now = self._now()
        with self._transaction():
            recovered_rows = self.connection.execute(
                """
                SELECT
                    jobs.job_id,
                    jobs.next_attempt_at,
                    EXISTS (
                        SELECT 1
                          FROM attempts
                         WHERE attempts.job_id = jobs.job_id
                           AND attempts.state = ?
                           AND attempts.finished_at IS NULL
                    ) AS has_active_attempt
                  FROM jobs
                 WHERE jobs.status = ?
                 ORDER BY jobs.created_at
                """,
                (
                    AttemptState.DISPATCHING.value,
                    JobStatus.DISPATCHING.value,
                ),
            ).fetchall()
            recovered = tuple(
                (
                    require_valid_job_id(row["job_id"]),
                    bool(row["has_active_attempt"]),
                )
                for row in recovered_rows
            )
            ambiguous_ids = tuple(
                job_id for job_id, has_active in recovered if has_active
            )
            unsubmitted_ids = tuple(
                job_id for job_id, has_active in recovered if not has_active
            )
            requeued = self.connection.execute(
                """
                UPDATE jobs
                   SET status = CASE
                           WHEN next_attempt_at IS NULL THEN ?
                           ELSE ?
                       END,
                       updated_at = ?
                 WHERE status = ?
                   AND NOT EXISTS (
                       SELECT 1
                         FROM attempts
                        WHERE attempts.job_id = jobs.job_id
                          AND attempts.state = ?
                          AND attempts.finished_at IS NULL
                   )
                """,
                (
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                    now,
                    JobStatus.DISPATCHING.value,
                    AttemptState.DISPATCHING.value,
                ),
            )
            self._fault("recover_after_requeue")
            if requeued.rowcount != len(unsubmitted_ids):
                raise ValueError("dispatch recovery found contradictory attempt state")
        return RecoveryReport(
            ambiguous_jobs=len(ambiguous_ids),
            ambiguous_job_ids=ambiguous_ids,
        )

    def admit(
        self,
        snapshot: SourceSnapshot,
        request: RequestIdentity,
    ) -> AdmissionResult:
        self._check_variant(request.variant)
        now = self._now()
        name = snapshot.staged_path.name
        job_id = name.split(".", 1)[0] if "." in name else uuid.uuid4().hex
        if _JOB_ID.fullmatch(job_id) is None:
            job_id = uuid.uuid4().hex
        source_path_key = hashlib.sha256(
            snapshot.normalized_path.encode("utf-8", errors="surrogatepass")
        ).hexdigest()
        source_name = snapshot.path.name
        source_path = str(snapshot.path)
        with self._transaction():
            existing = self.connection.execute(
                """
                SELECT job_id, status FROM jobs
                 WHERE source_path_key = ? AND size = ? AND mtime_ns = ? AND sha256 = ?
                """ + (" AND variant = ?" if self.named else ""),
                (source_path_key, snapshot.size, snapshot.mtime_ns, snapshot.sha256)
                + ((request.variant,) if self.named else ()),
            ).fetchone()
            if existing:
                return AdmissionResult(
                    job_id=existing["job_id"],
                    admitted=False,
                    status=JobStatus(existing["status"]),
                )
            if self.connection.execute(
                "SELECT 1 FROM jobs WHERE job_id = ?", (job_id,)
            ).fetchone():
                job_id = uuid.uuid4().hex
            insert = """
                INSERT INTO jobs (
                    job_id, source_path, source_path_key, source_name, size, mtime_ns,
                    sha256, status, provider, model, prompt_hash, staged_path,
                    created_at, updated_at
                ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                """
            if self.named:
                insert = insert.replace("created_at, updated_at", "created_at, updated_at, variant")
                insert = insert.replace(
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)",
                )
            self.connection.execute(
                insert,
                (
                    job_id,
                    source_path,
                    source_path_key,
                    source_name,
                    snapshot.size,
                    snapshot.mtime_ns,
                    snapshot.sha256,
                    JobStatus.READY.value,
                    request.provider,
                    request.model,
                    request.prompt_hash,
                    str(snapshot.staged_path),
                    now,
                    now,
                ) + ((request.variant,) if self.named else ()),
            )
        return AdmissionResult(job_id, True, JobStatus.READY)

    def active_attempt(self, job_id: str) -> AttemptRecord:
        require_valid_job_id(job_id)
        row = self.connection.execute(
            """
            SELECT job_id, attempt_no, state, started_at
              FROM attempts
             WHERE job_id = ? AND state = ? AND finished_at IS NULL
             ORDER BY attempt_no DESC
             LIMIT 1
            """,
            (job_id, AttemptState.DISPATCHING.value),
        ).fetchone()
        if row is None:
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Recovered dispatch state has no active attempt.",
            )
        return AttemptRecord(
            row["job_id"],
            row["attempt_no"],
            AttemptState(row["state"]),
            row["started_at"],
        )

    def requeue_never_sent_mpo(
        self, expected: JobRecord, snapshot: SourceSnapshot, identity: RequestIdentity,
        check_evidence: Callable[[], bool], *, retry_input_rejection: bool = False,
    ) -> bool:
        """CAS only the exact failed, never-attempted version after external proof."""
        require_valid_job_id(expected.job_id)
        key = hashlib.sha256(
            snapshot.normalized_path.encode("utf-8", errors="surrogatepass")
        ).hexdigest()
        with self._transaction():
            current = self.get_job(expected.job_id)
            if (
                current != expected
                or current.variant != identity.variant
                or current.status != JobStatus.FAILED
                or current.error_code != ErrorCode.INVALID_IMAGE.value
                or any(value is not None for value in (
                    current.temp_name, current.output_name, current.output_sha256,
                ))
                or self.attempt_count(current.job_id) != 0
                or (not retry_input_rejection
                    and (current.provider, current.model, current.prompt_hash)
                    != (identity.provider, identity.model, identity.prompt_hash))
                or (current.source_path_key, current.source_path, current.source_name,
                    current.size, current.mtime_ns, current.sha256)
                != (key, str(snapshot.path), snapshot.path.name,
                    snapshot.size, snapshot.mtime_ns, snapshot.sha256)
                or self.staged_path_references(snapshot.staged_path, current.job_id)
                or not check_evidence()
            ):
                return False
            self._fault("mpo_requeue_before_update")
            cursor = self.connection.execute(
                """UPDATE jobs SET staged_path = ?, status = ?, error_code = NULL,
                          next_attempt_at = NULL, updated_at = ?,
                          provider = ?, model = ?, prompt_hash = ?
                     WHERE job_id = ? AND status = ? AND error_code = ?
                       AND temp_name IS NULL AND output_name IS NULL AND output_sha256 IS NULL
                       AND NOT EXISTS (SELECT 1 FROM attempts WHERE attempts.job_id = jobs.job_id)""",
                (str(snapshot.staged_path), JobStatus.READY.value, self._now(),
                 identity.provider, identity.model, identity.prompt_hash,
                 current.job_id, JobStatus.FAILED.value, ErrorCode.INVALID_IMAGE.value),
            )
            self._require_updated(cursor, "mpo_requeue")
            self._fault("mpo_requeue_after_update")
        self._fault("mpo_requeue_after_commit")
        return True

    def get_job(self, job_id: str) -> JobRecord:
        require_valid_job_id(job_id)
        row = self.connection.execute(
            "SELECT * FROM jobs WHERE job_id = ?", (job_id,)
        ).fetchone()
        if row is None:
            raise KeyError(job_id)
        return self._job(row)

    def completed_attempts(self, job_id: str) -> tuple[CompletedAttempt, ...]:
        """Bounded, typed snapshot of every row; unfinished or malformed rows fail closed."""
        return self._completed_attempts(job_id)

    def completed_publication_attempts(self, job_id: str) -> tuple[CompletedAttempt, ...]:
        attempts = self._completed_attempts(job_id, allow_succeeded=True)
        if (
            not attempts or attempts[-1].state != AttemptState.SUCCEEDED
            or any(item.state == AttemptState.SUCCEEDED for item in attempts[:-1])
        ):
            raise AppError(ErrorCode.STATE_FAILED, "Restoration requires a completed successful final attempt.")
        return attempts

    def _completed_attempts(
        self, job_id: str, *, allow_succeeded: bool = False,
    ) -> tuple[CompletedAttempt, ...]:
        require_valid_job_id(job_id)
        rows = self.connection.execute(
            """SELECT job_id, attempt_no, state, started_at, finished_at, duration_ms,
                      error_code, safe_message, provider_request_id, retry_after_seconds
                 FROM attempts WHERE job_id = ? ORDER BY attempt_no LIMIT 101""",
            (job_id,),
        ).fetchall()
        result = []
        try:
            if len(rows) > 100:
                raise ValueError("unbounded history")
            for number, row in enumerate(rows, 1):
                succeeded = allow_succeeded and row["state"] == AttemptState.SUCCEEDED.value
                if (
                    row["job_id"] != job_id
                    or type(row["attempt_no"]) is not int or row["attempt_no"] != number
                    or (not succeeded and row["state"] not in {
                        AttemptState.RETRYABLE.value, AttemptState.PERMANENT.value,
                        AttemptState.AMBIGUOUS.value,
                    })
                    or type(row["duration_ms"]) is not int or row["duration_ms"] < 0
                    or (not succeeded and (
                        type(row["safe_message"]) is not str or len(row["safe_message"]) > 2048
                    ))
                    or (succeeded and any(row[field] is not None for field in (
                        "error_code", "safe_message", "retry_after_seconds",
                    )))
                    or (row["provider_request_id"] is not None and (
                        type(row["provider_request_id"]) is not str or len(row["provider_request_id"]) > 128
                    ))
                ):
                    raise ValueError("invalid attempt")
                dates = []
                for field in ("started_at", "finished_at"):
                    value = row[field]
                    if type(value) is not str or len(value) > 64:
                        raise ValueError("invalid timestamp")
                    date = datetime.fromisoformat(value)
                    if date.tzinfo is None:
                        raise ValueError("naive timestamp")
                    dates.append(date)
                if dates[1] < dates[0]:
                    raise ValueError("invalid completion")
                validate_retry_after(row["retry_after_seconds"])
                result.append(CompletedAttempt(
                    job_id, number, AttemptState(row["state"]), row["started_at"],
                    row["finished_at"], row["duration_ms"],
                    None if succeeded else ErrorCode(row["error_code"]),
                    row["safe_message"], row["provider_request_id"], row["retry_after_seconds"],
                ))
        except (ValueError, TypeError, OverflowError):
            raise AppError(
                ErrorCode.STATE_FAILED,
                "Failed-response recovery requires contiguous, completed, typed attempt history.",
            ) from None
        return tuple(result)

    def reconcile_restored_publication(
        self, expected: JobRecord, attempts: tuple[CompletedAttempt, ...],
        check_evidence: Callable[[], bool],
    ) -> bool:
        """Local integrity repair only; never change request identity or attempt rows."""
        require_valid_job_id(expected.job_id)
        if (
            expected.status != JobStatus.FAILED or expected.temp_name is not None
            or expected.error_code not in {
                ErrorCode.STATE_FAILED.value, ErrorCode.OUTPUT_INVALID.value,
                ErrorCode.PUBLICATION_FAILED.value, ErrorCode.OUTPUT_LIMIT_EXCEEDED.value,
            }
            or type(expected.output_name) is not str or not expected.output_name
            or type(expected.output_sha256) is not str
            or re.fullmatch(r"[0-9a-f]{64}", expected.output_sha256) is None
        ):
            return False
        validated_leaf_name(expected.output_name, suffix=".png")

        def unchanged() -> bool:
            return (
                self.get_job(expected.job_id) == expected
                and self.completed_publication_attempts(expected.job_id) == attempts
                and self.attempt_count(expected.job_id) == len(attempts)
                and self.staged_path_references(
                    Path(expected.staged_path), expected.job_id, include_terminal=True,
                ) == ((expected.job_id, JobStatus.FAILED),)
                and all(
                    row["job_id"] == expected.job_id or (
                        type(row["output_name"]) is str
                        and row["output_name"].casefold() != expected.output_name.casefold()
                    )
                    for row in self.connection.execute(
                        "SELECT job_id, output_name FROM jobs WHERE output_name IS NOT NULL"
                    )
                )
            )

        with self._transaction():
            if not unchanged():
                return False
            self._fault("restored_publication_before_update")
            if not check_evidence() or not unchanged():
                return False
            cursor = self.connection.execute(
                """UPDATE jobs SET status = ?, error_code = NULL, next_attempt_at = NULL,
                          updated_at = ? WHERE job_id = ? AND status = ?""",
                (JobStatus.SUCCEEDED.value, self._now(), expected.job_id, JobStatus.FAILED.value),
            )
            self._require_updated(cursor, "restored_publication")
            self._fault("restored_publication_after_update")
        self._fault("restored_publication_after_commit")
        return True

    def requeue_failed_response(
        self, expected: JobRecord, snapshot: SourceSnapshot, identity: RequestIdentity,
        attempts: tuple[CompletedAttempt, ...], check_evidence: Callable[[], bool], *,
        max_attempts: int, next_attempt_at: str, retry_failed_variants: bool = False,
    ) -> bool:
        require_valid_job_id(expected.job_id)
        if not retry_failed_variants or not 1 <= len(attempts) < max_attempts <= 4:
            return False
        due = datetime.fromisoformat(next_attempt_at)
        if due.tzinfo is None:
            raise ValueError("recovery retry deadline must have a timezone")
        key = hashlib.sha256(
            snapshot.normalized_path.encode("utf-8", errors="surrogatepass")
        ).hexdigest()
        with self._transaction():
            current = self.get_job(expected.job_id)
            if (
                current != expected
                or current.status not in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
                or current.provider != "azure_openai" or identity.provider != "azure_openai"
                or current.variant != identity.variant
                or current.error_code != attempts[-1].error_code.value
                or any(value is not None for value in (
                    current.temp_name, current.output_name, current.output_sha256,
                ))
                or (current.source_path_key, current.source_path, current.source_name,
                    current.size, current.mtime_ns, current.sha256)
                != (key, str(snapshot.path), snapshot.path.name,
                    snapshot.size, snapshot.mtime_ns, snapshot.sha256)
                or self.completed_attempts(current.job_id) != attempts
                or self.attempt_count(current.job_id) != len(attempts)
                or self.staged_path_references(
                    snapshot.staged_path, current.job_id, include_terminal=True,
                )
            ):
                return False
            self._fault("response_requeue_before_update")
            if (
                not check_evidence()
                or self.get_job(current.job_id) != expected
                or self.completed_attempts(current.job_id) != attempts
                or self.attempt_count(current.job_id) != len(attempts)
                or self.staged_path_references(
                    snapshot.staged_path, current.job_id, include_terminal=True,
                )
            ):
                return False
            cursor = self.connection.execute(
                """UPDATE jobs SET staged_path = ?, status = ?, error_code = NULL,
                          next_attempt_at = ?, updated_at = ?,
                          provider = ?, model = ?, prompt_hash = ?
                     WHERE job_id = ? AND status = ?
                       AND temp_name IS NULL AND output_name IS NULL AND output_sha256 IS NULL""",
                (str(snapshot.staged_path),
                 (JobStatus.READY if due <= datetime.fromisoformat(self._now()) else JobStatus.READY_RETRY).value,
                 next_attempt_at, self._now(),
                 identity.provider, identity.model, identity.prompt_hash,
                 current.job_id, current.status.value),
            )
            self._require_updated(cursor, "response_requeue")
            self._fault("response_requeue_after_update")
        self._fault("response_requeue_after_commit")
        return True

    def list_jobs(self) -> list[JobRecord]:
        return [
            self._job(row)
            for row in self.connection.execute("SELECT * FROM jobs ORDER BY created_at")
        ]

    def pending_count(self) -> int:
        return self.connection.execute(
            "SELECT COUNT(*) FROM jobs WHERE status IN (?, ?)",
            (JobStatus.READY.value, JobStatus.READY_RETRY.value),
        ).fetchone()[0]

    def find_jobs_by_source_metadata(
        self,
        normalized_path: str,
        size: int,
        mtime_ns: int,
        variant: str = "",
    ) -> list[JobRecord]:
        self._check_variant(variant)
        source_path_key = hashlib.sha256(
            normalized_path.encode("utf-8", errors="surrogatepass")
        ).hexdigest()
        return [
            self._job(row)
            for row in self.connection.execute(
                """
                SELECT * FROM jobs
                 WHERE source_path_key = ? AND size = ? AND mtime_ns = ?
                """ + (" AND variant = ?" if self.named else "") + " ORDER BY created_at, job_id",
                (source_path_key, size, mtime_ns) + ((variant,) if self.named else ()),
            )
        ]

    def staged_path_references(
        self,
        staged_path: Path,
        expected_job_id: str,
        *,
        include_terminal: bool = False,
    ) -> tuple[tuple[str, JobStatus], ...]:
        require_valid_job_id(expected_job_id)
        target_key = os.path.normcase(
            os.path.normpath(os.path.abspath(staged_path))
        )
        references: list[tuple[str, JobStatus]] = []
        rows = self.connection.execute(
            """
            SELECT job_id, status, staged_path
              FROM jobs
             WHERE ? OR job_id = ?
                OR status IN (?, ?, ?, ?, ?, ?)
             ORDER BY created_at, job_id
            """,
            (
                include_terminal,
                expected_job_id,
                *(
                    status.value
                    for status in _STAGED_REFERENCE_STATUSES
                ),
            ),
        ).fetchall()
        for row in rows:
            job_id = require_valid_job_id(row["job_id"])
            try:
                status = JobStatus(row["status"])
                reference_key = os.path.normcase(
                    os.path.normpath(os.path.abspath(row["staged_path"]))
                )
            except (TypeError, ValueError):
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "Recovered state contains an invalid staged input reference.",
                ) from None
            if reference_key == target_key:
                references.append((job_id, status))
        return tuple(references)

    def peek_ready(self, now_utc: datetime, job_id: str | None = None) -> JobRecord | None:
        """Read the next eligible job without changing queue or retry state."""
        if job_id is not None:
            require_valid_job_id(job_id)
        row = self.connection.execute(
            """
            SELECT * FROM jobs
             WHERE (? IS NULL OR job_id = ?)
               AND (status = ?
                    OR (status = ? AND next_attempt_at IS NOT NULL AND next_attempt_at <= ?))
             ORDER BY
                CASE WHEN next_attempt_at IS NULL THEN created_at ELSE next_attempt_at END,
                created_at
             LIMIT 1
            """,
            (job_id, job_id, JobStatus.READY.value, JobStatus.READY_RETRY.value, now_utc.isoformat()),
        ).fetchone()
        return self.get_job(row["job_id"]) if row is not None else None

    def claim_next_ready(self, now_utc: datetime) -> JobRecord | None:
        now = now_utc.isoformat()
        with self._transaction():
            row = self.connection.execute(
                """
                SELECT * FROM jobs
                 WHERE status = ?
                    OR (status = ? AND next_attempt_at IS NOT NULL AND next_attempt_at <= ?)
                 ORDER BY
                    CASE WHEN next_attempt_at IS NULL THEN created_at ELSE next_attempt_at END,
                    created_at
                 LIMIT 1
                """,
                (JobStatus.READY.value, JobStatus.READY_RETRY.value, now),
            ).fetchone()
            if row is None:
                return None
            require_valid_job_id(row["job_id"])
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, updated_at = ?
                 WHERE job_id = ? AND status IN (?, ?)
                """,
                (
                    JobStatus.DISPATCHING.value,
                    self._now(),
                    row["job_id"],
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                ),
            )
            self._require_updated(cursor, "claim_next_ready")
        return self.get_job(row["job_id"])

    def claim_ready(self, job_id: str, now_utc: datetime) -> JobRecord | None:
        require_valid_job_id(job_id)
        now = now_utc.isoformat()
        with self._transaction():
            row = self.connection.execute(
                """
                SELECT * FROM jobs
                 WHERE job_id = ?
                   AND (
                       status = ?
                       OR (
                           status = ?
                           AND next_attempt_at IS NOT NULL
                           AND next_attempt_at <= ?
                       )
                   )
                """,
                (
                    job_id,
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                    now,
                ),
            ).fetchone()
            if row is None:
                return None
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, updated_at = ?
                 WHERE job_id = ?
                   AND (
                       status = ?
                       OR (
                           status = ?
                           AND next_attempt_at IS NOT NULL
                           AND next_attempt_at <= ?
                       )
                   )
                """,
                (
                    JobStatus.DISPATCHING.value,
                    self._now(),
                    job_id,
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                    now,
                ),
            )
            self._require_updated(cursor, "claim_ready")
        return self.get_job(job_id)

    def _raw_dispatch_history(self, job_id: str) -> tuple[tuple, ...]:
        rows = self.connection.execute(
            "SELECT * FROM attempts WHERE job_id = ? ORDER BY attempt_no LIMIT 101",
            (job_id,),
        ).fetchall()
        if len(rows) > 100 or len(rows) != self.attempt_count(job_id):
            raise AppError(ErrorCode.STATE_FAILED, "Dispatch history is not bounded and complete.")
        return tuple(tuple(row) for row in rows)

    def dispatch_history(self, expected: JobRecord):
        """Read one consistent history, retaining invalid bytes for denial CAS."""
        with self._transaction():
            if self.get_job(expected.job_id) != expected:
                raise AppError(ErrorCode.STATE_FAILED, "The selected dispatch job changed.")
            raw = self._raw_dispatch_history(expected.job_id)
            try:
                completed = self.completed_attempts(expected.job_id)
            except AppError:
                completed = None
            return raw, completed

    def deny_queued_replay(self, expected: JobRecord, history: tuple[tuple, ...]) -> bool:
        """Only terminalize the mutable queue row; preserve all evidence/artifacts."""
        with self._transaction():
            if (
                expected.status not in {JobStatus.READY, JobStatus.READY_RETRY}
                or self.get_job(expected.job_id) != expected
                or self._raw_dispatch_history(expected.job_id) != history
            ):
                return False
            cursor = self.connection.execute(
                "UPDATE jobs SET status = ?, error_code = ?, updated_at = ? WHERE job_id = ?",
                (JobStatus.FAILED.value, ErrorCode.STATE_FAILED.value, self._now(), expected.job_id),
            )
            self._require_updated(cursor, "deny_queued_replay")
        return True

    def begin_attempt(self, job_id: str) -> AttemptRecord:
        return self._begin_attempt(job_id)

    def begin_attempt_checked(
        self, expected: JobRecord, history: tuple[tuple, ...],
    ) -> AttemptRecord:
        return self._begin_attempt(expected.job_id, expected=expected, history=history)

    def _begin_attempt(
        self, job_id: str, *, expected: JobRecord | None = None,
        history: tuple[tuple, ...] | None = None,
    ) -> AttemptRecord:
        require_valid_job_id(job_id)
        now = self._now()
        with self._transaction():
            self._fault("begin_attempt_before_check")
            if expected is not None and (
                self.get_job(job_id) != expected
                or self._raw_dispatch_history(job_id) != history
            ):
                raise AppError(
                    ErrorCode.STATE_FAILED, "The authorized dispatch job or history changed.",
                )
            row = self.connection.execute(
                "SELECT status, temp_name FROM jobs WHERE job_id = ?", (job_id,)
            ).fetchone()
            if (
                row is None
                or row["status"] != JobStatus.DISPATCHING.value
                or row["temp_name"] is not None
            ):
                raise ValueError("job is not claimed for dispatch")
            attempt_no = self.connection.execute(
                "SELECT COALESCE(MAX(attempt_no), 0) + 1 FROM attempts WHERE job_id = ?",
                (job_id,),
            ).fetchone()[0]
            self.connection.execute(
                """
                INSERT INTO attempts(job_id, attempt_no, state, started_at)
                VALUES (?, ?, ?, ?)
                """,
                (job_id, attempt_no, AttemptState.DISPATCHING.value, now),
            )
            cursor = self.connection.execute(
                """
                UPDATE jobs SET next_attempt_at = NULL, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (now, job_id, JobStatus.DISPATCHING.value),
            )
            self._require_updated(cursor, "begin_attempt")
        return AttemptRecord(job_id, attempt_no, AttemptState.DISPATCHING, now)

    def set_current_temp_name(self, job_id: str, temp_name: str) -> None:
        require_valid_job_id(job_id)
        validated_temp_name = validated_leaf_name(temp_name, suffix=".tmp")
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET temp_name = ?, updated_at = ?
                 WHERE job_id = ? AND status = ? AND temp_name IS NULL
                   AND EXISTS (
                       SELECT 1
                         FROM attempts
                        WHERE attempts.job_id = jobs.job_id
                          AND attempts.state = ?
                          AND attempts.finished_at IS NULL
                   )
                """,
                (
                    validated_temp_name,
                    self._now(),
                    job_id,
                    JobStatus.DISPATCHING.value,
                    AttemptState.DISPATCHING.value,
                ),
            )
            self._require_updated(cursor, "set_current_temp_name")

    def clear_current_temp_name(
        self,
        job_id: str,
        expected_temp_name: str,
        expected_status: JobStatus,
    ) -> bool:
        require_valid_job_id(job_id)
        validated_temp_name = validated_leaf_name(
            expected_temp_name, suffix=".tmp"
        )
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET temp_name = NULL, updated_at = ?
                 WHERE job_id = ? AND status = ? AND temp_name = ?
                """,
                (
                    self._now(),
                    job_id,
                    expected_status.value,
                    validated_temp_name,
                ),
            )
            return cursor.rowcount == 1

    def _finish_attempt(
        self,
        job_id: str,
        attempt_no: int,
        state: AttemptState,
        duration_ms: int,
        error_code: ErrorCode | None,
        safe_message: str | None,
        provider_request_id: str | None = None,
        retry_after_seconds: float | None = None,
    ) -> None:
        validate_retry_after(retry_after_seconds)
        cursor = self.connection.execute(
            """
            UPDATE attempts
               SET state = ?, finished_at = ?, duration_ms = ?, error_code = ?,
                   safe_message = ?, provider_request_id = ?, retry_after_seconds = ?
             WHERE job_id = ? AND attempt_no = ? AND state = ? AND finished_at IS NULL
            """,
            (
                state.value,
                self._now(),
                duration_ms,
                error_code.value if error_code else None,
                sanitize_text(safe_message) if safe_message is not None else None,
                validated_request_id(provider_request_id),
                retry_after_seconds,
                job_id,
                attempt_no,
                AttemptState.DISPATCHING.value,
            ),
        )
        self._require_updated(cursor, "finish_attempt")

    def mark_retryable(
        self,
        job_id: str,
        attempt_no: int,
        duration_ms: int,
        next_attempt_at: str,
        error_code: ErrorCode,
        safe_message: str,
        retry_after_seconds: float | None = None,
    ) -> None:
        with self._transaction():
            self._finish_attempt(
                job_id,
                attempt_no,
                AttemptState.RETRYABLE,
                duration_ms,
                error_code,
                safe_message,
                retry_after_seconds=retry_after_seconds,
            )
            self._fault("mark_retryable_after_attempt")
            cursor = self.connection.execute(
                """
                UPDATE jobs
                   SET status = ?, next_attempt_at = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.READY_RETRY.value,
                    next_attempt_at,
                    error_code.value,
                    self._now(),
                    job_id,
                    JobStatus.DISPATCHING.value,
                ),
            )
            self._require_updated(cursor, "mark_retryable")

    def mark_queued_retry_exhausted(
        self, expected: JobRecord, history: tuple[tuple, ...],
    ) -> CompletedAttempt:
        """Terminalize an exhausted claim without changing completed evidence."""
        with self._transaction():
            if (
                expected.status != JobStatus.DISPATCHING
                or self.get_job(expected.job_id) != expected
                or self._raw_dispatch_history(expected.job_id) != history
                or not history
                or any(value is not None for value in (
                    expected.temp_name, expected.output_name, expected.output_sha256,
                ))
            ):
                raise AppError(
                    ErrorCode.STATE_FAILED, "The authorized dispatch job or history changed.",
                )
            last = self.completed_attempts(expected.job_id)[-1]
            cursor = self.connection.execute(
                "UPDATE jobs SET status = ?, error_code = ?, updated_at = ? WHERE job_id = ?",
                (JobStatus.FAILED.value, last.error_code.value, self._now(), expected.job_id),
            )
            self._require_updated(cursor, "mark_queued_retry_exhausted")
            self._fault("mark_queued_retry_exhausted_after_update")
        return last

    def mark_retry_exhausted(
        self,
        job_id: str,
        attempt_no: int,
        duration_ms: int,
        next_attempt_at: str,
        error_code: ErrorCode,
        safe_message: str,
        retry_after_seconds: float | None = None,
    ) -> None:
        """Keep the retryable HTTP evidence without granting a queued transition."""
        with self._transaction():
            self._finish_attempt(
                job_id, attempt_no, AttemptState.RETRYABLE, duration_ms,
                error_code, safe_message, retry_after_seconds=retry_after_seconds,
            )
            self._fault("mark_retry_exhausted_after_attempt")
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, next_attempt_at = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.FAILED.value, next_attempt_at, error_code.value, self._now(),
                    job_id, JobStatus.DISPATCHING.value,
                ),
            )
            self._require_updated(cursor, "mark_retry_exhausted")

    def mark_response_staged(
        self,
        job_id: str,
        attempt_no: int,
        duration_ms: int,
        expected_temp_name: str,
        provider_request_id: str | None,
    ) -> None:
        require_valid_job_id(job_id)
        validated_temp_name = validated_leaf_name(
            expected_temp_name, suffix=".tmp"
        )
        with self._transaction():
            self._finish_attempt(
                job_id,
                attempt_no,
                AttemptState.SUCCEEDED,
                duration_ms,
                None,
                None,
                provider_request_id,
            )
            self._fault("mark_response_staged_after_attempt")
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, updated_at = ?
                 WHERE job_id = ? AND status = ? AND temp_name = ?
                """,
                (
                    JobStatus.RESPONSE_STAGED.value,
                    self._now(),
                    job_id,
                    JobStatus.DISPATCHING.value,
                    validated_temp_name,
                ),
            )
            self._require_updated(cursor, "mark_response_staged")

    def mark_output_verified(self, job_id: str) -> None:
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.OUTPUT_VERIFIED.value,
                    self._now(),
                    job_id,
                    JobStatus.RESPONSE_STAGED.value,
                ),
            )
            self._require_updated(cursor, "mark_output_verified")

    def plan_publication(
        self, job_id: str, output_name: str, output_sha256: str
    ) -> None:
        require_valid_job_id(job_id)
        validated_output_name = validated_leaf_name(output_name, suffix=".png")
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET output_name = ?, output_sha256 = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    validated_output_name,
                    output_sha256,
                    self._now(),
                    job_id,
                    JobStatus.OUTPUT_VERIFIED.value,
                ),
            )
            self._require_updated(cursor, "plan_publication")
            self._fault("plan_publication_after_update")

    def mark_published(self, job_id: str) -> None:
        require_valid_job_id(job_id)
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                   AND output_name IS NOT NULL AND output_sha256 IS NOT NULL
                """,
                (
                    JobStatus.PUBLISHED.value,
                    self._now(),
                    job_id,
                    JobStatus.OUTPUT_VERIFIED.value,
                ),
            )
            self._require_updated(cursor, "mark_published")
            self._fault("mark_published_after_update")

    def mark_succeeded(self, job_id: str) -> None:
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, error_code = NULL, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.SUCCEEDED.value,
                    self._now(),
                    job_id,
                    JobStatus.PUBLISHED.value,
                ),
            )
            self._require_updated(cursor, "mark_succeeded")

    def mark_failed(
        self,
        job_id: str,
        error_code: ErrorCode,
        *,
        attempt_no: int | None = None,
        duration_ms: int = 0,
        safe_message: str | None = None,
        retry_after_seconds: float | None = None,
    ) -> None:
        with self._transaction():
            if attempt_no is not None:
                self._finish_attempt(
                    job_id,
                    attempt_no,
                    AttemptState.PERMANENT,
                    duration_ms,
                    error_code,
                    safe_message,
                    retry_after_seconds=retry_after_seconds,
                )
                self._fault("mark_failed_after_attempt")
                expected = (JobStatus.DISPATCHING.value,)
            else:
                expected = (
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                    JobStatus.DISPATCHING.value,
                    JobStatus.RESPONSE_STAGED.value,
                    JobStatus.OUTPUT_VERIFIED.value,
                    JobStatus.PUBLISHED.value,
                )
            placeholders = ",".join("?" for _ in expected)
            cursor = self.connection.execute(
                f"""
                UPDATE jobs SET status = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status IN ({placeholders})
                """,
                (
                    JobStatus.FAILED.value,
                    error_code.value,
                    self._now(),
                    job_id,
                    *expected,
                ),
            )
            self._require_updated(cursor, "mark_failed")

    def reconcile_succeeded_integrity_failure(
        self, job_id: str, error_code: ErrorCode
    ) -> None:
        require_valid_job_id(job_id)
        allowed_error_codes = {
            ErrorCode.STATE_FAILED,
            ErrorCode.OUTPUT_INVALID,
            ErrorCode.OUTPUT_LIMIT_EXCEEDED,
            ErrorCode.PUBLICATION_FAILED,
        }
        if error_code not in allowed_error_codes:
            raise ValueError("error code is not an allowed succeeded-integrity failure")
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.FAILED.value,
                    error_code.value,
                    self._now(),
                    job_id,
                    JobStatus.SUCCEEDED.value,
                ),
            )
            self._require_updated(cursor, "reconcile_succeeded_integrity_failure")

    def reconcile_terminal_staged_integrity_failure(self, job_id: str) -> None:
        require_valid_job_id(job_id)
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status IN (?, ?)
                """,
                (
                    JobStatus.FAILED.value,
                    ErrorCode.STATE_FAILED.value,
                    self._now(),
                    job_id,
                    JobStatus.FAILED.value,
                    JobStatus.AMBIGUOUS.value,
                ),
            )
            self._require_updated(
                cursor, "reconcile_terminal_staged_integrity_failure"
            )

    def mark_ambiguous(
        self,
        job_id: str,
        attempt_no: int,
        duration_ms: int,
        safe_message: str,
    ) -> None:
        with self._transaction():
            self._finish_attempt(
                job_id,
                attempt_no,
                AttemptState.AMBIGUOUS,
                duration_ms,
                ErrorCode.PROVIDER_AMBIGUOUS,
                safe_message,
            )
            self._fault("mark_ambiguous_after_attempt")
            cursor = self.connection.execute(
                """
                UPDATE jobs SET status = ?, error_code = ?, updated_at = ?
                 WHERE job_id = ? AND status = ?
                """,
                (
                    JobStatus.AMBIGUOUS.value,
                    ErrorCode.PROVIDER_AMBIGUOUS.value,
                    self._now(),
                    job_id,
                    JobStatus.DISPATCHING.value,
                ),
            )
            self._require_updated(cursor, "mark_ambiguous")

    def attempt_count(self, job_id: str) -> int:
        require_valid_job_id(job_id)
        return self.connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?", (job_id,)
        ).fetchone()[0]

    def next_ready_at(self) -> datetime | None:
        value = self.connection.execute(
            """
            SELECT MIN(next_attempt_at) FROM jobs
             WHERE status = ? AND next_attempt_at IS NOT NULL
            """,
            (JobStatus.READY_RETRY.value,),
        ).fetchone()[0]
        return datetime.fromisoformat(value) if value else None

    def next_ready_at_for(self, job_id: str) -> datetime | None:
        require_valid_job_id(job_id)
        row = self.connection.execute(
            """
            SELECT next_attempt_at FROM jobs
             WHERE job_id = ? AND status = ? AND next_attempt_at IS NOT NULL
            """,
            (job_id, JobStatus.READY_RETRY.value),
        ).fetchone()
        return datetime.fromisoformat(row["next_attempt_at"]) if row else None

    def compare_and_swap_staged_path(
        self,
        job_id: str,
        expected_staged_path: Path,
        replacement_staged_path: Path,
    ) -> bool:
        require_valid_job_id(job_id)
        with self._transaction():
            cursor = self.connection.execute(
                """
                UPDATE jobs SET staged_path = ?, updated_at = ?
                 WHERE job_id = ?
                   AND status IN (?, ?)
                   AND staged_path = ?
                """,
                (
                    str(replacement_staged_path),
                    self._now(),
                    job_id,
                    JobStatus.READY.value,
                    JobStatus.READY_RETRY.value,
                    str(expected_staged_path),
                ),
            )
            return cursor.rowcount == 1

    def _check_variant(self, variant: str) -> None:
        if (self.named and not valid_variant(variant)) or (not self.named and variant != ""):
            raise AppError(ErrorCode.STATE_FAILED, "Queue contains an invalid prompt variant.")

    def _job(self, row: sqlite3.Row) -> JobRecord:
        require_valid_job_id(row["job_id"])
        variant = row["variant"] if self.named else ""
        self._check_variant(variant)
        return JobRecord(
            job_id=row["job_id"],
            source_path=row["source_path"],
            source_path_key=row["source_path_key"],
            source_name=row["source_name"],
            size=row["size"],
            mtime_ns=row["mtime_ns"],
            sha256=row["sha256"],
            status=JobStatus(row["status"]),
            provider=row["provider"],
            model=row["model"],
            prompt_hash=row["prompt_hash"],
            staged_path=row["staged_path"],
            temp_name=row["temp_name"],
            output_name=row["output_name"],
            output_sha256=row["output_sha256"],
            next_attempt_at=row["next_attempt_at"],
            created_at=row["created_at"],
            updated_at=row["updated_at"],
            error_code=row["error_code"],
            variant=variant,
        )
