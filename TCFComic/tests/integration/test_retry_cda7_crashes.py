"""Reach-asserted transaction, immutable audit and filesystem ownership faults."""
from __future__ import annotations

import json
import os
import sqlite3
import threading
from pathlib import Path

import pytest

from conftest import write_image
from test_retry_cda7_pacing import tmp_path, failed, fresh_snapshot, rows, admit, processor_for
from test_retry_cda7_provenance import provenance
from tcfcomic import quarantine
from tcfcomic.domain import AppError, ErrorCode, JobStatus


class Crash(BaseException):
    pass


def external_job(p, job_id):
    with sqlite3.connect(p.state.path) as reader:
        reader.row_factory = sqlite3.Row
        return dict(reader.execute("SELECT * FROM jobs WHERE job_id=?", (job_id,)).fetchone())


@pytest.mark.parametrize("point", [
    "response_requeue_before_update", "response_requeue_after_update", "response_requeue_after_commit",
])
def test_crash_atomicity_and_restart_authority(failed, point):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    original = external_job(p, failed.job.job_id)
    hits, observed = [], []
    audit_bytes = provenance(failed).read_bytes()
    def hook(actual):
        if actual == point:
            hits.append(actual)
            observed.append(external_job(p, failed.job.job_id))
            assert len(list(failed.config.paths.quarantine.glob("*.response-retry.*.json"))) == 1
            assert rows(p) == failed.attempts
            raise Crash()
    p.state._transition_hook = hook
    with pytest.raises(Crash):
        p._reconsider_failed_response(failed.job, snapshot)
    p.state._transition_hook = None
    assert hits == [point]
    assert provenance(failed).read_bytes() == audit_bytes
    assert rows(p) == failed.attempts
    committed = point.endswith("after_commit")
    current = p.state.get_job(failed.job.job_id)
    if committed:
        assert current.status == JobStatus.READY_RETRY
        assert current.prompt_hash == p._request_identity().prompt_hash
        assert current.staged_path == str(snapshot.staged_path)
        assert snapshot.staged_path.exists()
        assert observed[0] == external_job(p, failed.job.job_id)
    else:
        assert current == failed.job
        assert observed == [original]
        assert not snapshot.staged_path.exists()
    p.close()
    # Prepared audit alone is not authority; committed current identity IS resumable.
    with processor_for(failed.config, failed.logger, failed.timer, failed.provider) as restarted:
        result = restarted.process_path(failed.source)
        assert result.attempts == (2 if committed else 1)
        assert result.status == (JobStatus.SUCCEEDED if committed else JobStatus.FAILED)
        assert rows(restarted)[:1] == failed.attempts
        assert len(failed.provider.requests) == (2 if committed else 1)
    # Fixture context manager's close is idempotent at Processor level.


@pytest.mark.parametrize("kind", [
    "job-status", "job-model", "job-output", "job-prompt", "job-source",
    "job-count-binding", "attempt-message", "attempt-count", "stage-owner",
])
def test_before_update_cas_rejects_competing_mutation(failed, kind):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    other = None
    if kind == "stage-owner":
        other = admit(p, write_image(failed.config.paths.source / "other.png")).job_id
    hits = []
    mutated = []
    def hook(point):
        if point != "response_requeue_before_update":
            return
        hits.append(point)
        if kind.startswith("job-"):
            field, value = {
                "job-status": ("status", "AMBIGUOUS"), "job-model": ("model", "competitor"),
                "job-output": ("output_name", "owned.png"), "job-prompt": ("prompt_hash", "b" * 64),
                "job-source": ("source_name", "changed.png"), "job-count-binding": ("size", 0),
            }[kind]
            p.state.connection.execute(f"UPDATE jobs SET {field}=? WHERE job_id=?", (value, failed.job.job_id))
        elif kind == "attempt-message":
            p.state.connection.execute("UPDATE attempts SET safe_message=? WHERE job_id=?",
                                       ("changed evidence", failed.job.job_id))
        elif kind == "attempt-count":
            p.state.connection.execute(
                """INSERT INTO attempts SELECT job_id, 2, state, started_at, finished_at,
                   duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
                   FROM attempts WHERE job_id=?""", (failed.job.job_id,))
        else:
            p.state.connection.execute("UPDATE jobs SET staged_path=? WHERE job_id=?",
                                       (str(snapshot.staged_path), other))
        mutated.append((p.state.get_job(failed.job.job_id), rows(p)))
    p.state._transition_hook = hook
    try:
        assert not p._reconsider_failed_response(failed.job, snapshot)
    except AppError as error:
        assert error.code == ErrorCode.STATE_FAILED
    finally:
        p.state._transition_hook = None
    assert hits == ["response_requeue_before_update"]
    # Guard refusal commits concurrent in-transaction changes only when it returns;
    # an evidence exception rolls the transaction back. Neither may adopt identity.
    assert p.state.get_job(failed.job.job_id).prompt_hash != p._request_identity().prompt_hash
    assert (p.state.get_job(failed.job.job_id), rows(p)) == mutated[0]
    assert p.state.attempt_count(failed.job.job_id) <= 2
    assert len(failed.provider.requests) == 1
    if kind == "stage-owner":
        assert p.state.get_job(other).staged_path == str(snapshot.staged_path)
        assert snapshot.staged_path.exists()
    else:
        assert not snapshot.staged_path.exists()


@pytest.mark.parametrize("mutation", ["source-hash", "source-missing", "quarantine", "fresh-stage", "audit"])
def test_commit_callback_revalidates_fresh_source_and_evidence(failed, mutation):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    hits = []
    def hook(point):
        if point != "response_requeue_before_update":
            return
        hits.append(point)
        if mutation == "source-hash":
            before = failed.source.stat()
            data = bytearray(failed.source.read_bytes())
            data[-1] ^= 1
            failed.source.write_bytes(data)
            os.utime(failed.source, ns=(before.st_atime_ns, before.st_mtime_ns))
            assert failed.source.stat().st_size == before.st_size
            assert failed.source.stat().st_mtime_ns == before.st_mtime_ns
        elif mutation == "source-missing":
            failed.source.rename(failed.source.with_suffix(".moved"))
        elif mutation == "quarantine":
            path = provenance(failed)
            data = json.loads(path.read_text())
            data["safe_message"] = "changed provenance"
            path.write_text(json.dumps(data), encoding="utf-8")
        elif mutation == "fresh-stage":
            snapshot.staged_path.write_bytes(b"changed staged bytes")
        else:
            path, = failed.config.paths.quarantine.glob("*.response-retry.*.json")
            path.write_bytes(b"changed prepared audit")
    p.state._transition_hook = hook
    unexpected = None
    try:
        assert not p._reconsider_failed_response(failed.job, snapshot)
    except AppError as error:
        assert error.code in {ErrorCode.STATE_FAILED, ErrorCode.SOURCE_CHANGED}
    except Exception as error:
        unexpected = error
    finally:
        p.state._transition_hook = None
    assert hits == ["response_requeue_before_update"]
    assert p.state.get_job(failed.job.job_id) == failed.job
    assert rows(p) == failed.attempts
    assert len(failed.provider.requests) == 1
    assert not snapshot.staged_path.exists()
    assert unexpected is None, f"Recovery must fail safely, not leak {type(unexpected).__name__}: {unexpected}"


@pytest.mark.parametrize("operation", ["open", "link", "fsync", "unlink"])
def test_audit_prepare_fault_cannot_dispatch_or_adopt(failed, monkeypatch, operation):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    hits = []
    original_open, original_link, original_fsync, original_unlink = Path.open, os.link, os.fsync, Path.unlink
    root = failed.config.paths.quarantine
    def bad_open(path, *args, **kwargs):
        if path.parent == root and args and args[0] == "xb":
            hits.append("open")
            raise OSError("synthetic audit write refusal")
        return original_open(path, *args, **kwargs)
    def bad_link(src, dst, *args, **kwargs):
        if Path(dst).parent == root:
            hits.append("link")
            raise OSError("synthetic audit link refusal")
        return original_link(src, dst, *args, **kwargs)
    def bad_fsync(fd):
        # Patch only during audit preservation, after staging was durably created.
        hits.append("fsync")
        raise OSError("synthetic audit fsync refusal")
    def bad_unlink(path, *args, **kwargs):
        if path.parent == root and path.name.endswith(".json.tmp"):
            hits.append("unlink")
            raise OSError("synthetic audit cleanup refusal")
        return original_unlink(path, *args, **kwargs)
    target, name, function = {
        "open": (Path, "open", bad_open), "link": (os, "link", bad_link),
        "fsync": (os, "fsync", bad_fsync), "unlink": (Path, "unlink", bad_unlink),
    }[operation]
    with monkeypatch.context() as patcher:
        patcher.setattr(target, name, function)
        with pytest.raises(AppError):
            p._reconsider_failed_response(failed.job, snapshot)
    assert hits and set(hits) == {operation}
    assert p.state.get_job(failed.job.job_id) == failed.job
    assert rows(p) == failed.attempts
    assert not snapshot.staged_path.exists()
    assert len(failed.provider.requests) == 1


def test_stage_unlink_failure_retains_unreferenced_stage_without_authorization(failed, monkeypatch):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    provenance(failed).unlink()
    original = Path.unlink
    hits = []
    def fail(path, *args, **kwargs):
        if path == snapshot.staged_path:
            hits.append(True)
            raise OSError("synthetic stage unlink refusal")
        return original(path, *args, **kwargs)
    with monkeypatch.context() as patcher:
        patcher.setattr(Path, "unlink", fail)
        with pytest.raises(AppError):
            p._reconsider_failed_response(failed.job, snapshot)
    assert hits and snapshot.staged_path.exists()
    assert p.state.get_job(failed.job.job_id) == failed.job
    assert rows(p) == failed.attempts
    snapshot.staged_path.unlink()


def test_postcommit_restart_rejects_old_request_identity(failed):
    from dataclasses import replace
    p = failed.processor
    old_config = replace(failed.config, provider=replace(
        failed.config.provider, prompt="wrong identity after committed recovery"))
    snapshot = fresh_snapshot(failed)
    hits = []
    def hook(point):
        if point == "response_requeue_after_commit":
            hits.append(point)
            raise Crash()
    p.state._transition_hook = hook
    with pytest.raises(Crash):
        p._reconsider_failed_response(failed.job, snapshot)
    assert hits == ["response_requeue_after_commit"]
    assert snapshot.staged_path.exists()
    p.close()
    with processor_for(old_config, failed.logger, failed.timer, failed.provider) as restarted:
        result = restarted.process_path(failed.source)
        assert result.status == JobStatus.FAILED
        assert result.error_code == ErrorCode.STATE_FAILED
        assert rows(restarted) == failed.attempts
        assert len(failed.provider.requests) == 1
