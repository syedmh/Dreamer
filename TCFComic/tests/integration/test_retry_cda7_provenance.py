"""Independent one-field-at-a-time historical authorization negatives."""
from __future__ import annotations

import json
import os
from dataclasses import replace
from pathlib import Path

import pytest

from test_retry_cda7_pacing import tmp_path, failed, fresh_snapshot, rows, admit
from tcfcomic import quarantine
from tcfcomic.domain import AppError, ErrorCode, JobStatus


def provenance(f):
    return f.config.paths.quarantine / f"{f.job.job_id}.json"


def deny(f):
    p = f.processor
    before, history = p.state.get_job(f.job.job_id), rows(p)
    try:
        result = admit(p, f.source, retry_failed_variants=True)
        assert result.status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
    except AppError as error:
        assert error.code in {ErrorCode.STATE_FAILED, ErrorCode.SOURCE_CHANGED}
    assert p.state.get_job(f.job.job_id) == before
    assert rows(p) == history
    assert len(f.provider.requests) == 1
    assert not list(p.staging.glob("*.input"))


@pytest.mark.parametrize("field,value", [
    ("job_id", "b" * 32), ("source_name", "other.png"), ("stage", "input"),
    ("error_code", "PROVIDER_RETRYABLE"), ("attempts", 0), ("attempts", True),
    ("schema_version", True), ("safe_message", "HTTP 400."),
    ("first_seen_at", "1999-01-01T00:00:00+00:00"),
    ("failed_at", "invalid"), ("failed_at", "2030-01-01T00:00:00"),
    ("failed_at", "1900-01-01T00:00:00+00:00"), ("failed_at", "x" * 65),
    ("variant", "wrong"), ("extra", 1),
    ("source_version.size", 999), ("source_version.size", True),
    ("source_version.mtime_ns", 0), ("source_version.mtime_ns", True),
    ("source_version.sha256", "b" * 64), ("source_version.extra", "wrong"),
])
def test_each_provenance_binding_denies(failed, field, value):
    path = provenance(failed)
    data = json.loads(path.read_text())
    if "." in field:
        outer, inner = field.split(".")
        data[outer][inner] = value
    else:
        data[field] = value
    path.write_text(json.dumps(data), encoding="utf-8")
    deny(failed)


@pytest.mark.parametrize("shape", ["duplicate", "missing", "list", "null", "malformed"])
def test_noncanonical_json_denies(failed, shape):
    path = provenance(failed)
    text = path.read_text()
    if shape == "duplicate":
        text = '{"schema_version":1,' + text[1:]
    elif shape == "missing":
        data = json.loads(text)
        del data["safe_message"]
        text = json.dumps(data)
    else:
        text = {"list": "[]", "null": "null", "malformed": "{"}[shape]
    path.write_text(text, encoding="utf-8")
    deny(failed)


@pytest.mark.parametrize("field,value", [
    ("state", "dispatching"), ("finished_at", None), ("attempt_no", 2),
    ("started_at", "bad"), ("finished_at", "2030-01-01T00:00:00"),
    ("finished_at", "1900-01-01T00:00:00+00:00"),
    ("duration_ms", -1), ("duration_ms", "bad"), ("safe_message", "x" * 2049),
    ("provider_request_id", "x" * 129), ("error_code", "UNKNOWN"),
    ("retry_after_seconds", -1), ("retry_after_seconds", 86401),
])
def test_malformed_typed_history_denies_without_rewrite(failed, field, value):
    failed.processor.state.connection.execute(
        f"UPDATE attempts SET {field}=? WHERE job_id=?", (value, failed.job.job_id))
    deny(failed)


@pytest.mark.parametrize("kind", ["unanswered", "count-mismatch", "moderation", "unknown-diagnostic"])
def test_completed_history_requires_exact_definitive_evidence(failed, kind):
    p = failed.processor
    if kind == "count-mismatch":
        p.state.connection.execute(
            """INSERT INTO attempts SELECT job_id, 2, state, started_at, finished_at,
               duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
               FROM attempts WHERE job_id=?""", (failed.job.job_id,))
    else:
        message = {
            "unanswered": "The provider request ended with an uncertain dispatch result.",
            "moderation": "The provider rejected the request. HTTP 400. Provider code moderation_blocked. The provider blocked the input or generated output under its content policy.",
            "unknown-diagnostic": "The provider rejected the request. HTTP 400. private provider prose",
        }[kind]
        code = "PROVIDER_AMBIGUOUS" if kind == "unanswered" else "PROVIDER_PERMANENT"
        state = "ambiguous" if kind == "unanswered" else "permanent"
        p.state.connection.execute(
            "UPDATE attempts SET safe_message=?,error_code=?,state=? WHERE job_id=?",
            (message, code, state, failed.job.job_id))
        p.state.connection.execute(
            "UPDATE jobs SET error_code=?, status=? WHERE job_id=?",
            (code, "AMBIGUOUS" if kind == "unanswered" else "FAILED", failed.job.job_id))
        path = provenance(failed)
        data = json.loads(path.read_text())
        data.update(error_code=code, safe_message=message)
        path.write_text(json.dumps(data), encoding="utf-8")
    deny(failed)


@pytest.mark.parametrize("kind", ["missing", "oversize", "hardlink", "reparse"])
def test_unsafe_provenance_files_denied(failed, monkeypatch, kind):
    path = provenance(failed)
    hits = []
    if kind == "missing":
        path.unlink()
    elif kind == "oversize":
        path.write_bytes(b"x" * 65537)
    elif kind == "hardlink":
        os.link(path, path.with_suffix(".linked"))
    else:
        original = quarantine.contained_leaf
        def reject(root, leaf, **kwargs):
            if leaf == path.name:
                hits.append(True)
                raise AppError(ErrorCode.STATE_FAILED, "Synthetic reparse boundary refusal.")
            return original(root, leaf, **kwargs)
        monkeypatch.setattr(quarantine, "contained_leaf", reject)
    deny(failed)
    if kind == "reparse":
        assert hits  # Explicit substitute; not a privileged native symlink claim.


def test_immutable_audit_exact_reuse_and_collision(failed):
    p = failed.processor
    payload = provenance(failed).read_bytes()
    args = (failed.job, failed.config.paths.quarantine, p._request_identity(), payload,
            p.state.completed_attempts(failed.job.job_id), 4)
    original, transition = quarantine.preserve_response_retry_audits(*args)
    before = original.read_bytes(), transition.read_bytes()
    assert quarantine.preserve_response_retry_audits(*args) == (original, transition)
    assert (original.read_bytes(), transition.read_bytes()) == before
    data = json.loads(before[1])
    assert data["previous_attempt_count"] == 1 and data["next_attempt"] == 2
    assert data["max_lifetime_attempts"] == 4
    for secret in [str(failed.source), p.config.provider.prompt, p.config.provider.endpoint]:
        assert secret.encode() not in before[1]
    original.write_bytes(b"conflicting immutable bytes")
    with pytest.raises(AppError, match="different evidence"):
        quarantine.preserve_response_retry_audits(*args)
    assert original.read_bytes() == b"conflicting immutable bytes"
    assert rows(p) == failed.attempts
    deny(failed)


@pytest.mark.parametrize("field,value", [
    ("provider", "openai"), ("source_name", "wrong.png"), ("size", 0),
    ("mtime_ns", 0), ("sha256", "b" * 64), ("source_path", "C:\\not-the-source.png"),
    ("source_path_key", "b" * 64), ("error_code", "INVALID_IMAGE"),
])
def test_mismatched_job_source_binding_cannot_requeue(failed, field, value):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    expected = replace(failed.job, **{field: value})
    history = p.state.completed_attempts(failed.job.job_id)
    assert not p.state.requeue_failed_response(
        expected, snapshot, p._request_identity(), history, lambda: True,
        max_attempts=4, next_attempt_at=failed.timer.wall.isoformat(), retry_failed_variants=True)
    assert p.state.get_job(failed.job.job_id) == failed.job
    assert rows(p) == failed.attempts
    snapshot.staged_path.unlink()


def test_earlier_unanswered_attempt_denies_even_when_last_is_eligible(failed):
    p = failed.processor
    p.state.connection.execute(
        """INSERT INTO attempts SELECT job_id, 2, state, started_at, finished_at,
           duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
           FROM attempts WHERE job_id=?""", (failed.job.job_id,))
    p.state.connection.execute(
        """UPDATE attempts SET state='ambiguous', error_code='PROVIDER_AMBIGUOUS',
           safe_message='The provider request ended with an uncertain dispatch result.'
           WHERE job_id=? AND attempt_no=1""", (failed.job.job_id,))
    path = provenance(failed)
    data = json.loads(path.read_text())
    data["attempts"] = 2
    path.write_text(json.dumps(data), encoding="utf-8")
    assert len(p.state.completed_attempts(failed.job.job_id)) == 2
    deny(failed)


def test_history_upper_bound_denies_without_dispatch(failed):
    p = failed.processor
    for number in range(2, 102):
        p.state.connection.execute(
            """INSERT INTO attempts SELECT job_id, ?, state, started_at, finished_at,
               duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
               FROM attempts WHERE job_id=? AND attempt_no=1""", (number, failed.job.job_id))
    before = rows(p)
    with pytest.raises(AppError, match="contiguous, completed, typed"):
        p.state.completed_attempts(failed.job.job_id)
    assert rows(p) == before
    assert len(failed.provider.requests) == 1


def test_current_variant_cannot_be_rebound_by_explicit_recovery(failed):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    assert not p.state.requeue_failed_response(
        failed.job, snapshot, replace(p._request_identity(), variant="another-slot"),
        p.state.completed_attempts(failed.job.job_id), lambda: True, max_attempts=4,
        next_attempt_at=failed.timer.wall.isoformat(), retry_failed_variants=True)
    assert p.state.get_job(failed.job.job_id) == failed.job
    assert rows(p) == failed.attempts
    snapshot.staged_path.unlink()
