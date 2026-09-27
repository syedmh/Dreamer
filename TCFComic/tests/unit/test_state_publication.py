from __future__ import annotations

import hashlib
import json
import os
from concurrent.futures import ThreadPoolExecutor
from datetime import UTC, datetime, timedelta
from pathlib import Path

import pytest
from conftest import write_image
from tcfcomic.domain import (
    AppError,
    ErrorCode,
    FailureRecord,
    JobStatus,
    RequestIdentity,
    SourceSnapshot,
)
from tcfcomic.publication import (
    initial_output_leaf,
    prepare_publication,
    promote_planned_output,
    safe_source_stem,
)
from tcfcomic.path_safety import (
    contained_leaf,
    reject_reparse_components,
)
from tcfcomic.quarantine import write_failure_record
from tcfcomic.state import StateStore


def failure_record() -> FailureRecord:
    return FailureRecord(
        job_id="a" * 32,
        source_name="photo.png",
        size=3,
        mtime_ns=4,
        sha256="a" * 64,
        stage="provider",
        error_code=ErrorCode.PROVIDER_PERMANENT,
        safe_message="The provider rejected the request.",
        attempts=1,
        first_seen_at="2026-08-30T00:00:00+00:00",
        failed_at="2026-08-30T00:00:01+00:00",
    )


def snapshot(
    tmp_path: Path,
    name: str = "photo.png",
    content: bytes = b"abc",
    *,
    production_identity: bool = True,
) -> SourceSnapshot:
    source = tmp_path / name
    source.write_bytes(content)
    staged = tmp_path / "1234567890abcdef.input"
    staged.write_bytes(content)
    info = source.stat()
    return SourceSnapshot(
        source,
        (
            os.path.normcase(str(Path(os.path.abspath(source))))
            if production_identity
            else str(source).lower()
        ),
        len(content),
        info.st_mtime_ns,
        hashlib.sha256(content).hexdigest(),
        staged,
    )


def opaque_temp_name(nonce: str = "a" * 32) -> str:
    return f".{nonce}.tmp"


def succeed_job(store: StateStore, job_id: str) -> int:
    claimed = store.claim_ready(job_id, datetime.now(UTC))
    assert claimed is not None
    attempt = store.begin_attempt(job_id)
    temp_name = opaque_temp_name()
    store.set_current_temp_name(job_id, temp_name)
    store.mark_response_staged(
        job_id,
        attempt.attempt_no,
        1,
        temp_name,
        None,
    )
    store.mark_output_verified(job_id)
    store.plan_publication(job_id, "published.png", "a" * 64)
    store.mark_published(job_id)
    store.mark_succeeded(job_id)
    return attempt.attempt_no


def test_state_pragmas_unique_admission_and_recovery(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    assert store.pragma_values() == ("wal", 1, 2)
    snap = snapshot(tmp_path)
    identity = RequestIdentity("fake", "gpt-image-2", "hash")
    first = store.admit(snap, identity)
    second = store.admit(snap, identity)
    assert first.admitted is True
    assert second.admitted is False
    claimed = store.claim_next_ready(datetime.now(UTC))
    assert claimed is not None and claimed.job_id == first.job_id
    attempt = store.begin_attempt(first.job_id)
    report = store.recover()
    assert report.ambiguous_jobs == 1
    assert report.ambiguous_job_ids == (first.job_id,)
    assert store.get_job(first.job_id).status == JobStatus.DISPATCHING
    active = store.active_attempt(first.job_id)
    assert active.attempt_no == attempt.attempt_no
    store.mark_ambiguous(first.job_id, attempt.attempt_no, 0, "reconciled")
    assert store.get_job(first.job_id).status == JobStatus.AMBIGUOUS
    assert store.attempt_count(first.job_id) == attempt.attempt_no
    store.close()


def test_modified_source_version_is_admitted(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    first = store.admit(snapshot(tmp_path, content=b"one"), identity)
    second = store.admit(snapshot(tmp_path, content=b"two"), identity)
    assert first.job_id != second.job_id
    assert len(store.list_jobs()) == 2
    store.close()


def test_find_jobs_by_source_metadata_requires_exact_path_size_and_mtime(
    tmp_path: Path,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    snap = snapshot(tmp_path, content=b"durable")
    admitted = store.admit(
        snap,
        RequestIdentity("fake", "model", "prompt"),
    )

    matches = store.find_jobs_by_source_metadata(
        snap.normalized_path,
        snap.size,
        snap.mtime_ns,
    )

    assert [job.job_id for job in matches] == [admitted.job_id]
    assert (
        store.find_jobs_by_source_metadata(
            snap.normalized_path + "-other",
            snap.size,
            snap.mtime_ns,
        )
        == []
    )
    assert (
        store.find_jobs_by_source_metadata(
            snap.normalized_path,
            snap.size + 1,
            snap.mtime_ns,
        )
        == []
    )
    assert (
        store.find_jobs_by_source_metadata(
            snap.normalized_path,
            snap.size,
            snap.mtime_ns + 1,
        )
        == []
    )
    store.close()


def test_sqlite_queue_admits_1000_unique_versions(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    admitted = []
    for index in range(1000):
        content = f"image-{index}".encode()
        snap = snapshot(tmp_path, name=f"photo-{index}.png", content=content)
        admitted.append(store.admit(snap, identity).admitted)
    assert all(admitted)
    assert len(store.list_jobs()) == 1000
    store.close()


def test_safe_names_and_collision_preserve_existing(tmp_path: Path) -> None:
    snap = snapshot(
        tmp_path,
        name="CON.png",
        production_identity=False,
    )
    assert safe_source_stem("CON.png") == "image"
    assert "/" not in safe_source_stem('bad<>:"/\\|?*.png')
    job_id = "abcdef1234567890"
    expected = tmp_path / initial_output_leaf(snap, job_id)
    expected.write_bytes(b"existing")
    temp = write_image(tmp_path / "attempt.tmp", "PNG")
    final, digest = prepare_publication(temp, tmp_path, snap, job_id)
    promote_planned_output(temp, final, digest)
    assert expected.read_bytes() == b"existing"
    assert final != expected
    assert final.suffix == ".png"
    assert hashlib.sha256(final.read_bytes()).hexdigest() == digest


@pytest.mark.parametrize(
    "output_name",
    [
        "api_key=ordinary-photo__123456789abc__abcdef12.png",
        f"{'A' * 80}__123456789abc__abcdef12.png",
    ],
)
def test_publication_plan_preserves_valid_machine_identifier_exactly(
    tmp_path: Path, output_name: str
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    temp_name = opaque_temp_name()
    store.set_current_temp_name(admitted.job_id, temp_name)
    store.mark_response_staged(
        admitted.job_id,
        attempt.attempt_no,
        1,
        temp_name,
        None,
    )
    store.mark_output_verified(admitted.job_id)

    store.plan_publication(admitted.job_id, output_name, "a" * 64)

    assert store.get_job(admitted.job_id).output_name == output_name
    store.close()


@pytest.mark.parametrize(
    "output_name",
    [
        "..\\escaped.png",
        "bad<name.png",
        "bad>name.png",
        'bad"name.png',
        "bad|name.png",
        "bad?name.png",
        "bad*name.png",
    ],
)
def test_publication_plan_rejects_invalid_machine_identifier(
    tmp_path: Path, output_name: str
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    temp_name = opaque_temp_name()
    store.set_current_temp_name(admitted.job_id, temp_name)
    store.mark_response_staged(
        admitted.job_id,
        attempt.attempt_no,
        1,
        temp_name,
        None,
    )
    store.mark_output_verified(admitted.job_id)

    with pytest.raises(AppError):
        store.plan_publication(admitted.job_id, output_name, "a" * 64)

    assert store.get_job(admitted.job_id).output_name is None
    store.close()


def test_current_temp_name_preserves_valid_opaque_leaf_exactly(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    monkeypatch.setenv("OPENAI_API_KEY", ".")
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    temp_name = opaque_temp_name("b" * 32)
    store.set_current_temp_name(admitted.job_id, temp_name)

    store.mark_response_staged(
        admitted.job_id,
        attempt.attempt_no,
        1,
        temp_name,
        None,
    )

    assert store.get_job(admitted.job_id).temp_name == temp_name
    store.close()


@pytest.mark.parametrize(
    "temp_name",
    [
        "..\\escaped.tmp",
        "bad<name.tmp",
        ".attempt.png",
    ],
)
def test_current_temp_name_rejects_unsafe_leaf_without_partial_transition(
    tmp_path: Path,
    temp_name: str,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)

    with pytest.raises(AppError) as caught:
        store.set_current_temp_name(admitted.job_id, temp_name)

    job = store.get_job(admitted.job_id)
    row = store.connection.execute(
        """
        SELECT state, finished_at
          FROM attempts
         WHERE job_id = ? AND attempt_no = ?
        """,
        (admitted.job_id, attempt.attempt_no),
    ).fetchone()
    assert caught.value.code == ErrorCode.STATE_FAILED
    assert job.status == JobStatus.DISPATCHING
    assert job.temp_name is None
    assert row["state"] == "dispatching"
    assert row["finished_at"] is None
    store.close()


def test_current_temp_name_clear_requires_expected_value_and_state(
    tmp_path: Path,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    store.begin_attempt(admitted.job_id)
    temp_name = opaque_temp_name()
    store.set_current_temp_name(admitted.job_id, temp_name)

    assert not store.clear_current_temp_name(
        admitted.job_id,
        opaque_temp_name("b" * 32),
        JobStatus.DISPATCHING,
    )
    assert not store.clear_current_temp_name(
        admitted.job_id,
        temp_name,
        JobStatus.FAILED,
    )
    assert store.get_job(admitted.job_id).temp_name == temp_name
    assert store.clear_current_temp_name(
        admitted.job_id,
        temp_name,
        JobStatus.DISPATCHING,
    )
    assert store.get_job(admitted.job_id).temp_name is None
    store.close()


@pytest.mark.parametrize("error_code", tuple(ErrorCode))
def test_mark_failed_rejects_succeeded_for_every_error_code(
    tmp_path: Path, error_code: ErrorCode
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    attempt_no = succeed_job(store, admitted.job_id)
    before = dict(
        store.connection.execute(
            "SELECT * FROM attempts WHERE job_id = ? AND attempt_no = ?",
            (admitted.job_id, attempt_no),
        ).fetchone()
    )

    with pytest.raises(ValueError, match="guarded state transition failed"):
        store.mark_failed(admitted.job_id, error_code)

    job = store.get_job(admitted.job_id)
    after = dict(
        store.connection.execute(
            "SELECT * FROM attempts WHERE job_id = ? AND attempt_no = ?",
            (admitted.job_id, attempt_no),
        ).fetchone()
    )
    assert job.status == JobStatus.SUCCEEDED
    assert job.error_code is None
    assert after == before
    store.close()


def test_claim_ready_claims_only_the_requested_ready_job(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    first = store.admit(
        snapshot(tmp_path, name="first.png", content=b"first"), identity
    )
    second = store.admit(
        snapshot(tmp_path, name="second.png", content=b"second"), identity
    )

    claimed = store.claim_ready(second.job_id, datetime.now(UTC))

    assert claimed is not None
    assert claimed.job_id == second.job_id
    assert store.get_job(first.job_id).status == JobStatus.READY
    assert store.get_job(second.job_id).status == JobStatus.DISPATCHING
    store.close()


def test_claim_ready_honors_target_retry_due_and_preserves_global_queue(
    tmp_path: Path,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    retry = store.admit(
        snapshot(tmp_path, name="retry.png", content=b"retry"), identity
    )
    unrelated = store.admit(
        snapshot(tmp_path, name="unrelated.png", content=b"unrelated"), identity
    )
    assert store.claim_ready(retry.job_id, datetime.now(UTC)) is not None
    attempt = store.begin_attempt(retry.job_id)
    due = datetime.now(UTC) + timedelta(minutes=5)
    store.mark_retryable(
        retry.job_id,
        attempt.attempt_no,
        1,
        due.isoformat(),
        ErrorCode.PROVIDER_RETRYABLE,
        "temporary",
    )

    assert store.claim_ready(retry.job_id, due - timedelta(seconds=1)) is None
    assert store.next_ready_at_for(retry.job_id) == due
    assert store.next_ready_at_for(unrelated.job_id) is None
    assert store.next_ready_at() == due
    globally_claimed = store.claim_next_ready(due - timedelta(seconds=1))
    assert globally_claimed is not None
    assert globally_claimed.job_id == unrelated.job_id

    claimed = store.claim_ready(retry.job_id, due)
    assert claimed is not None
    assert claimed.job_id == retry.job_id
    store.close()


def test_claim_ready_rejects_retry_without_due_time(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    store.connection.execute(
        "UPDATE jobs SET status = ?, next_attempt_at = NULL WHERE job_id = ?",
        (JobStatus.READY_RETRY.value, admitted.job_id),
    )

    assert store.claim_ready(admitted.job_id, datetime.now(UTC)) is None
    assert store.next_ready_at_for(admitted.job_id) is None
    store.close()


@pytest.mark.parametrize(
    "status",
    [JobStatus.DISPATCHING, JobStatus.FAILED, JobStatus.AMBIGUOUS, JobStatus.SUCCEEDED],
)
def test_claim_ready_returns_none_for_ineligible_target(
    tmp_path: Path, status: JobStatus
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    admitted = store.admit(
        snapshot(tmp_path, name="target.png", content=b"target"), identity
    )
    unrelated = store.admit(
        snapshot(tmp_path, name="unrelated.png", content=b"unrelated"), identity
    )
    store.connection.execute(
        "UPDATE jobs SET status = ? WHERE job_id = ?",
        (status.value, admitted.job_id),
    )

    assert store.claim_ready(admitted.job_id, datetime.now(UTC)) is None
    assert store.get_job(admitted.job_id).status == status
    assert store.claim_ready("f" * 32, datetime.now(UTC)) is None
    assert store.get_job(unrelated.job_id).status == JobStatus.READY
    store.close()


def test_next_ready_at_for_is_target_specific(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    identity = RequestIdentity("fake", "model", "prompt")
    retry = store.admit(
        snapshot(tmp_path, name="retry.png", content=b"retry"), identity
    )
    ready = store.admit(
        snapshot(tmp_path, name="ready.png", content=b"ready"), identity
    )
    due = datetime.now(UTC) + timedelta(minutes=2)
    store.connection.execute(
        "UPDATE jobs SET status = ?, next_attempt_at = ? WHERE job_id = ?",
        (JobStatus.READY_RETRY.value, due.isoformat(), retry.job_id),
    )

    assert store.next_ready_at_for(retry.job_id) == due
    assert store.next_ready_at_for(ready.job_id) is None
    assert store.next_ready_at_for("f" * 32) is None
    store.close()


def test_compare_and_swap_staged_path_requires_exact_ready_value(
    tmp_path: Path,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    original = Path(store.get_job(admitted.job_id).staged_path)
    replacement = tmp_path / "replacement.input"

    assert store.compare_and_swap_staged_path(
        admitted.job_id, original, replacement
    )
    assert Path(store.get_job(admitted.job_id).staged_path) == replacement
    assert not store.compare_and_swap_staged_path(
        admitted.job_id, original, tmp_path / "lost-race.input"
    )
    assert Path(store.get_job(admitted.job_id).staged_path) == replacement
    assert not store.compare_and_swap_staged_path(
        "f" * 32, original, replacement
    )
    store.close()


def test_compare_and_swap_staged_path_allows_ready_retry(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    original = Path(store.get_job(admitted.job_id).staged_path)
    store.connection.execute(
        "UPDATE jobs SET status = ?, next_attempt_at = ? WHERE job_id = ?",
        (
            JobStatus.READY_RETRY.value,
            (datetime.now(UTC) + timedelta(minutes=1)).isoformat(),
            admitted.job_id,
        ),
    )
    replacement = tmp_path / "retry-replacement.input"

    assert store.compare_and_swap_staged_path(
        admitted.job_id, original, replacement
    )
    assert Path(store.get_job(admitted.job_id).staged_path) == replacement
    store.close()


@pytest.mark.parametrize(
    "status",
    [JobStatus.DISPATCHING, JobStatus.RESPONSE_STAGED, JobStatus.SUCCEEDED, JobStatus.FAILED],
)
def test_compare_and_swap_staged_path_rejects_wrong_state(
    tmp_path: Path, status: JobStatus
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    original = Path(store.get_job(admitted.job_id).staged_path)
    store.connection.execute(
        "UPDATE jobs SET status = ? WHERE job_id = ?",
        (status.value, admitted.job_id),
    )

    assert not store.compare_and_swap_staged_path(
        admitted.job_id, original, tmp_path / "replacement.input"
    )
    assert Path(store.get_job(admitted.job_id).staged_path) == original
    store.close()


def test_staged_path_references_use_normalized_absolute_keys(
    tmp_path: Path,
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    original = Path(store.get_job(admitted.job_id).staged_path)
    normalized_spelling = str(original).upper().replace("\\", "/")
    store.connection.execute(
        "UPDATE jobs SET staged_path = ? WHERE job_id = ?",
        (normalized_spelling, admitted.job_id),
    )

    assert store.staged_path_references(original, admitted.job_id) == (
        (admitted.job_id, JobStatus.READY),
    )
    store.close()


_ALLOWED_INTEGRITY_FAILURES = (
    ErrorCode.STATE_FAILED,
    ErrorCode.OUTPUT_INVALID,
    ErrorCode.OUTPUT_LIMIT_EXCEEDED,
    ErrorCode.PUBLICATION_FAILED,
)


@pytest.mark.parametrize("error_code", _ALLOWED_INTEGRITY_FAILURES)
def test_reconcile_succeeded_integrity_failure_accepts_allowlisted_code(
    tmp_path: Path, error_code: ErrorCode
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    attempt_no = succeed_job(store, admitted.job_id)
    before = dict(
        store.connection.execute(
            "SELECT * FROM attempts WHERE job_id = ? AND attempt_no = ?",
            (admitted.job_id, attempt_no),
        ).fetchone()
    )

    store.reconcile_succeeded_integrity_failure(admitted.job_id, error_code)

    job = store.get_job(admitted.job_id)
    after = dict(
        store.connection.execute(
            "SELECT * FROM attempts WHERE job_id = ? AND attempt_no = ?",
            (admitted.job_id, attempt_no),
        ).fetchone()
    )
    assert job.status == JobStatus.FAILED
    assert job.error_code == error_code.value
    assert after == before
    store.close()


@pytest.mark.parametrize(
    "error_code",
    tuple(code for code in ErrorCode if code not in _ALLOWED_INTEGRITY_FAILURES),
)
def test_reconcile_succeeded_integrity_failure_rejects_other_codes(
    tmp_path: Path, error_code: ErrorCode
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    succeed_job(store, admitted.job_id)

    with pytest.raises(ValueError):
        store.reconcile_succeeded_integrity_failure(admitted.job_id, error_code)

    assert store.get_job(admitted.job_id).status == JobStatus.SUCCEEDED
    store.close()


@pytest.mark.parametrize(
    "status", tuple(status for status in JobStatus if status != JobStatus.SUCCEEDED)
)
def test_reconcile_succeeded_integrity_failure_rejects_wrong_source_state(
    tmp_path: Path, status: JobStatus
) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(
        snapshot(tmp_path), RequestIdentity("fake", "model", "prompt")
    )
    store.connection.execute(
        "UPDATE jobs SET status = ? WHERE job_id = ?",
        (status.value, admitted.job_id),
    )

    with pytest.raises(ValueError):
        store.reconcile_succeeded_integrity_failure(
            admitted.job_id, ErrorCode.STATE_FAILED
        )

    assert store.get_job(admitted.job_id).status == status
    with pytest.raises(ValueError):
        store.reconcile_succeeded_integrity_failure(
            "f" * 32, ErrorCode.STATE_FAILED
        )
    store.close()


@pytest.mark.parametrize(
    "operation",
    [
        lambda store: store.claim_ready("invalid", datetime.now(UTC)),
        lambda store: store.next_ready_at_for("invalid"),
        lambda store: store.compare_and_swap_staged_path(
            "invalid", Path("old"), Path("new")
        ),
        lambda store: store.reconcile_succeeded_integrity_failure(
            "invalid", ErrorCode.STATE_FAILED
        ),
        lambda store: store.reconcile_terminal_staged_integrity_failure(
            "invalid"
        ),
    ],
)
def test_targeted_state_apis_validate_job_id(tmp_path: Path, operation) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()

    with pytest.raises(AppError) as caught:
        operation(store)

    assert caught.value.code == ErrorCode.STATE_FAILED
    store.close()


def test_quarantine_schema_is_allowlisted(tmp_path: Path) -> None:
    path = write_failure_record(failure_record(), tmp_path)
    payload = json.loads(path.read_text(encoding="utf-8"))
    assert set(payload) == {
        "schema_version",
        "job_id",
        "source_name",
        "source_version",
        "stage",
        "error_code",
        "safe_message",
        "attempts",
        "first_seen_at",
        "failed_at",
    }
    assert not list(tmp_path.glob("*.tmp"))


def test_quarantine_removes_owned_temp_when_payload_write_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import tcfcomic.quarantine as quarantine_module

    def fail_after_write(payload, stream, **kwargs):
        del payload, kwargs
        stream.write("{")
        raise RuntimeError("simulated payload failure")

    monkeypatch.setattr(quarantine_module.json, "dump", fail_after_write)
    with pytest.raises(RuntimeError, match="payload failure"):
        write_failure_record(failure_record(), tmp_path)
    assert not list(tmp_path.glob("*.json.tmp"))
    assert not list(tmp_path.glob("*.json"))


def test_quarantine_removes_owned_temp_when_post_fsync_revalidation_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import tcfcomic.quarantine as quarantine_module

    real_contained_leaf = quarantine_module.contained_leaf
    final_checks = 0

    def fail_second_final_check(root, leaf, **kwargs):
        nonlocal final_checks
        if leaf == f"{'a' * 32}.json":
            final_checks += 1
            if final_checks == 2:
                raise AppError(
                    ErrorCode.STATE_FAILED,
                    "simulated post-fsync revalidation failure",
                )
        return real_contained_leaf(root, leaf, **kwargs)

    monkeypatch.setattr(
        quarantine_module, "contained_leaf", fail_second_final_check
    )
    with pytest.raises(AppError, match="post-fsync"):
        write_failure_record(failure_record(), tmp_path)
    assert not list(tmp_path.glob("*.json.tmp"))
    assert not list(tmp_path.glob("*.json"))


def test_quarantine_removes_owned_temp_when_replace_fails(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import tcfcomic.quarantine as quarantine_module

    def fail_replace(source, destination):
        del source, destination
        raise OSError("simulated replace failure")

    monkeypatch.setattr(quarantine_module.os, "replace", fail_replace)
    with pytest.raises(OSError, match="replace failure"):
        write_failure_record(failure_record(), tmp_path)
    assert not list(tmp_path.glob("*.json.tmp"))
    assert not list(tmp_path.glob("*.json"))


def test_quarantine_surfaces_safe_owned_temp_cleanup_failure(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import tcfcomic.quarantine as quarantine_module

    def fail_after_write(payload, stream, **kwargs):
        del payload, kwargs
        stream.write("{")
        raise RuntimeError("simulated payload failure")

    real_unlink = Path.unlink

    def fail_temp_unlink(path: Path, *args, **kwargs):
        if path.name.endswith(".json.tmp"):
            raise PermissionError("operator detail")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(quarantine_module.json, "dump", fail_after_write)
    monkeypatch.setattr(Path, "unlink", fail_temp_unlink)
    with pytest.raises(AppError) as caught:
        write_failure_record(failure_record(), tmp_path)
    assert caught.value.code == ErrorCode.STATE_FAILED
    assert "operator detail" not in caught.value.safe_message
    assert len(list(tmp_path.glob("*.json.tmp"))) == 1


def test_quarantine_leaves_owned_temp_for_baseexception_recovery(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    import tcfcomic.quarantine as quarantine_module

    class SimulatedCrash(BaseException):
        pass

    def crash_replace(source, destination):
        del source, destination
        raise SimulatedCrash()

    monkeypatch.setattr(quarantine_module.os, "replace", crash_replace)
    with pytest.raises(SimulatedCrash):
        write_failure_record(failure_record(), tmp_path)
    temps = list(tmp_path.glob("*.json.tmp"))
    assert len(temps) == 1
    assert temps[0].name == f"{'a' * 32}.{temps[0].name.split('.')[1]}.json.tmp"
    assert not list(tmp_path.glob(f"{'a' * 32}.json"))


@pytest.mark.parametrize(
    "leaf",
    [
        "bad:name.png",
        "bad/name.png",
        "bad\\name.png",
        "bad\x01name.png",
        "trailing.png.",
        "trailing.png ",
        "CON.png",
        "nul.txt",
        "COM1.log",
        "COM¹.png",
        "COM².png",
        "COM³.png",
        "LPT¹.txt",
        "LPT².txt",
        "LPT³.txt",
    ],
)
def test_contained_leaf_rejects_windows_unsafe_names(
    tmp_path: Path, leaf: str
) -> None:
    with pytest.raises(AppError):
        contained_leaf(tmp_path, leaf)


def _create_dangling_symlink_or_skip(
    path: Path, target: Path, *, target_is_directory: bool
) -> None:
    try:
        path.symlink_to(target, target_is_directory=target_is_directory)
    except (NotImplementedError, OSError) as exc:
        pytest.skip(f"symlink creation unavailable: {exc}")


def test_reject_reparse_components_rejects_dangling_symlink_component(
    tmp_path: Path,
) -> None:
    root = tmp_path / "root"
    root.mkdir()
    dangling = root / "dangling"
    _create_dangling_symlink_or_skip(
        dangling,
        tmp_path / "missing-directory-target",
        target_is_directory=True,
    )

    with pytest.raises(AppError) as caught:
        reject_reparse_components(dangling / "child.png")

    assert caught.value.code == ErrorCode.SOURCE_OUTSIDE_ROOT
    assert "reparse point" in caught.value.safe_message


def test_contained_leaf_rejects_dangling_symlink_leaf(
    tmp_path: Path,
) -> None:
    root = tmp_path / "root"
    root.mkdir()
    dangling = root / "dangling.png"
    _create_dangling_symlink_or_skip(
        dangling,
        tmp_path / "missing-file-target.png",
        target_is_directory=False,
    )

    with pytest.raises(AppError) as caught:
        contained_leaf(root, dangling.name, suffix=".png", must_exist=True)

    assert caught.value.code == ErrorCode.STATE_FAILED
    assert "reparse point" in caught.value.safe_message


def test_quarantine_rejects_malicious_job_id_without_escape(tmp_path: Path) -> None:
    record = FailureRecord(
        job_id="..\\escaped",
        source_name="photo.png",
        size=3,
        mtime_ns=4,
        sha256="a" * 64,
        stage="provider",
        error_code=ErrorCode.PROVIDER_PERMANENT,
        safe_message="safe",
        attempts=1,
        first_seen_at="2026-08-30T00:00:00+00:00",
        failed_at="2026-08-30T00:00:01+00:00",
    )
    with pytest.raises(AppError):
        write_failure_record(record, tmp_path / "quarantine")
    assert not (tmp_path / "escaped.json").exists()


def test_publication_digest_verification_streams_without_read_bytes(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    snap = snapshot(tmp_path)
    temp = write_image(tmp_path / "attempt.tmp", "PNG")

    def forbid_read_bytes(path: Path) -> bytes:
        raise AssertionError(f"read_bytes must not be used for digest verification: {path}")

    monkeypatch.setattr(Path, "read_bytes", forbid_read_bytes)
    final, digest = prepare_publication(
        temp, tmp_path, snap, "abcdef1234567890"
    )
    promote_planned_output(temp, final, digest)
    with final.open("rb") as stream:
        assert hashlib.file_digest(stream, "sha256").hexdigest() == digest


def test_retry_transition_rolls_back_attempt_and_job_together(tmp_path: Path) -> None:
    armed = False

    def fail_between_rows(point: str) -> None:
        if armed and point == "mark_retryable_after_attempt":
            raise RuntimeError("simulated crash")

    store = StateStore(tmp_path / "state.db", transition_hook=fail_between_rows)
    store.initialize()
    admitted = store.admit(snapshot(tmp_path), RequestIdentity("fake", "model", "hash"))
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    armed = True
    with pytest.raises(RuntimeError, match="simulated crash"):
        store.mark_retryable(
            admitted.job_id,
            attempt.attempt_no,
            10,
            (datetime.now(UTC) + timedelta(seconds=1)).isoformat(),
            ErrorCode.PROVIDER_RETRYABLE,
            "temporary",
        )
    job = store.get_job(admitted.job_id)
    row = store.connection.execute(
        "SELECT state, finished_at FROM attempts WHERE job_id = ? AND attempt_no = ?",
        (admitted.job_id, attempt.attempt_no),
    ).fetchone()
    assert job.status == JobStatus.DISPATCHING
    assert row["state"] == "dispatching"
    assert row["finished_at"] is None
    store.close()


def test_recovery_rolls_back_active_and_unsubmitted_claims_together(
    tmp_path: Path,
) -> None:
    armed = False

    def fail_during_recovery(point: str) -> None:
        if armed and point == "recover_after_requeue":
            raise RuntimeError("simulated recovery crash")

    store = StateStore(
        tmp_path / "state.db", transition_hook=fail_during_recovery
    )
    store.initialize()
    identity = RequestIdentity("fake", "model", "hash")
    active = store.admit(
        snapshot(tmp_path, name="active.png", content=b"active"), identity
    )
    unsubmitted = store.admit(
        snapshot(tmp_path, name="unsubmitted.png", content=b"unsubmitted"),
        identity,
    )
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(active.job_id)
    claimed = store.claim_next_ready(datetime.now(UTC))
    assert claimed and claimed.job_id == unsubmitted.job_id
    armed = True

    with pytest.raises(RuntimeError, match="simulated recovery crash"):
        store.recover()

    assert store.get_job(active.job_id).status == JobStatus.DISPATCHING
    assert store.get_job(unsubmitted.job_id).status == JobStatus.DISPATCHING
    row = store.connection.execute(
        """
        SELECT state, finished_at
          FROM attempts
         WHERE job_id = ? AND attempt_no = ?
        """,
        (active.job_id, attempt.attempt_no),
    ).fetchone()
    assert row["state"] == "dispatching"
    assert row["finished_at"] is None
    store.close()


@pytest.mark.parametrize(
    ("fault_point", "transition"),
    [
        (
            "mark_response_staged_after_attempt",
            lambda store, job_id, attempt_no: store.mark_response_staged(
                job_id,
                attempt_no,
                10,
                opaque_temp_name(),
                "request-id",
            ),
        ),
        (
            "mark_failed_after_attempt",
            lambda store, job_id, attempt_no: store.mark_failed(
                job_id,
                ErrorCode.PROVIDER_PERMANENT,
                attempt_no=attempt_no,
                duration_ms=10,
                safe_message="permanent",
            ),
        ),
        (
            "mark_ambiguous_after_attempt",
            lambda store, job_id, attempt_no: store.mark_ambiguous(
                job_id, attempt_no, 10, "uncertain"
            ),
        ),
    ],
)
def test_all_attempt_job_transitions_rollback_atomically(
    tmp_path: Path, fault_point: str, transition
) -> None:
    armed = False

    def fail_between_rows(point: str) -> None:
        if armed and point == fault_point:
            raise RuntimeError("simulated crash")

    store = StateStore(tmp_path / "state.db", transition_hook=fail_between_rows)
    store.initialize()
    admitted = store.admit(snapshot(tmp_path), RequestIdentity("fake", "model", "hash"))
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    if fault_point == "mark_response_staged_after_attempt":
        store.set_current_temp_name(admitted.job_id, opaque_temp_name())
    armed = True
    with pytest.raises(RuntimeError, match="simulated crash"):
        transition(store, admitted.job_id, attempt.attempt_no)
    job = store.get_job(admitted.job_id)
    row = store.connection.execute(
        "SELECT state, finished_at FROM attempts WHERE job_id = ? AND attempt_no = ?",
        (admitted.job_id, attempt.attempt_no),
    ).fetchone()
    assert job.status == JobStatus.DISPATCHING
    assert row["state"] == "dispatching"
    assert row["finished_at"] is None
    store.close()


def test_claim_honors_durable_retry_time(tmp_path: Path) -> None:
    store = StateStore(tmp_path / "state.db")
    store.initialize()
    admitted = store.admit(snapshot(tmp_path), RequestIdentity("fake", "model", "hash"))
    assert store.claim_next_ready(datetime.now(UTC)) is not None
    attempt = store.begin_attempt(admitted.job_id)
    due = datetime.now(UTC) + timedelta(minutes=5)
    store.mark_retryable(
        admitted.job_id,
        attempt.attempt_no,
        1,
        due.isoformat(),
        ErrorCode.PROVIDER_RETRYABLE,
        "temporary",
    )
    assert store.claim_next_ready(datetime.now(UTC)) is None
    claimed = store.claim_next_ready(due + timedelta(seconds=1))
    assert claimed is not None
    assert claimed.job_id == admitted.job_id
    store.close()


def test_concurrent_duplicate_admission_has_one_winner(tmp_path: Path) -> None:
    database = tmp_path / "state.db"
    setup = StateStore(database)
    setup.initialize()
    setup.close()
    original = snapshot(tmp_path)
    identity = RequestIdentity("fake", "model", "hash")

    def admit(index: int) -> bool:
        staged = tmp_path / f"{index:032x}.input"
        staged.write_bytes(original.staged_path.read_bytes())
        candidate = SourceSnapshot(
            original.path,
            original.normalized_path,
            original.size,
            original.mtime_ns,
            original.sha256,
            staged,
        )
        store = StateStore(database)
        try:
            return store.admit(candidate, identity).admitted
        finally:
            store.close()

    with ThreadPoolExecutor(max_workers=8) as pool:
        winners = list(pool.map(admit, range(16)))
    verify = StateStore(database)
    try:
        assert winners.count(True) == 1
        assert len(verify.list_jobs()) == 1
    finally:
        verify.close()
