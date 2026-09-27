"""Queued budget exhaustion must commit before quarantine, without rewriting history."""
from __future__ import annotations

import base64
import json
import threading
from dataclasses import replace
from pathlib import Path

import httpx
import pytest

from conftest import write_image
from test_retry_cda7_pacing import Clock, admit, config_for, processor_for, rows, sdk, tmp_path
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.quarantine import read_response_failure_provenance


@pytest.mark.parametrize("quarantine_fault", [False, True])
@pytest.mark.parametrize("completed_count", [1, 3])
@pytest.mark.parametrize("named", [False, True])
def test_queued_exhaustion_preserves_evidence_and_requires_explicit_recovery(
    sdk, tmp_path, quiet_logger, monkeypatch, quarantine_fault, completed_count, named,
):
    config = config_for(tmp_path / "run", attempts=4, named=named)
    source = write_image(config.paths.source / "x.png")
    source_bytes, source_stat = source.read_bytes(), source.stat()
    variant = "one" if named else ""
    calls = []

    def http(request):
        calls.append(request.method)
        if len(calls) <= completed_count:
            return httpx.Response(429, json={}, headers={"retry-after": "75"})
        return httpx.Response(200, json={
            "data": [{"b64_json": base64.b64encode(source_bytes).decode("ascii")}],
        })

    provider, _ = sdk.install(enabled=True, handler=http)
    timer = Clock()
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = admit(p, source, variant=variant).job_id
        for _ in range(completed_count):
            assert p._dispatch_one(threading.Event(), job_id)
            assert p.state.get_job(job_id).status == JobStatus.READY_RETRY
            timer.advance(75)
        queued = p.state.get_job(job_id)
        history = rows(p)
        last = p.state.completed_attempts(job_id)[-1]
        gate = p.state.provider_not_before(None)
        stage_bytes = Path(queued.staged_path).read_bytes()
        assert calls == ["POST"] * completed_count

    def preserved(p):
        current = p.state.get_job(job_id)
        assert current.status == JobStatus.FAILED
        assert replace(current, status=queued.status, updated_at=queued.updated_at) == queued
        assert rows(p) == history
        assert p.state.attempt_count(job_id) == completed_count
        assert p.state.provider_not_before(None) == gate
        assert calls == ["POST"] * completed_count
        assert source.read_bytes() == source_bytes
        assert source.stat().st_mtime_ns == source_stat.st_mtime_ns
        assert not list(config.paths.quarantine.glob("*.response-*.json"))

    reduced = replace(config, retry=replace(config.retry, max_attempts=completed_count))
    with processor_for(reduced, quiet_logger, timer, provider) as p:
        if quarantine_fault:
            def fail(job, stage, error, count):
                preserved(p)
                assert stage == "provider" and count == completed_count
                assert error.code == last.error_code and error.safe_message == last.safe_message
                assert Path(job.staged_path).read_bytes() == stage_bytes
                raise OSError("synthetic queued exhaustion quarantine fault")
            monkeypatch.setattr(p, "_quarantine", fail)
            with pytest.raises(OSError, match="synthetic queued exhaustion quarantine fault"):
                p._dispatch_one(threading.Event(), job_id)
        else:
            assert p._dispatch_one(threading.Event(), job_id)
        preserved(p)
        if quarantine_fault:
            assert not list(config.paths.quarantine.glob("*.json"))
        else:
            provenance = read_response_failure_provenance(
                p.state.get_job(job_id), config.paths.quarantine,
                p.state.completed_attempts(job_id),
            )
            assert json.loads(provenance)["safe_message"] == last.safe_message
            assert not Path(queued.staged_path).exists()

    for _ in range(2):
        with processor_for(config, quiet_logger, timer, provider) as p:
            preserved(p)
            assert p.process_path(source, variant=variant).status == JobStatus.FAILED
            assert not p._dispatch_one(threading.Event(), job_id)
            preserved(p)
            if not quarantine_fault:
                assert (config.paths.quarantine / f"{job_id}.json").read_bytes() == provenance

    with processor_for(config, quiet_logger, timer, provider) as p:
        if quarantine_fault:
            with pytest.raises(AppError) as caught:
                p.process_path(source, variant=variant, retry_failed_variants=True)
            assert caught.value.code == ErrorCode.STATE_FAILED
            preserved(p)
        else:
            result = p.process_path(source, variant=variant, retry_failed_variants=True)
            assert result.status == JobStatus.SUCCEEDED
            assert result.attempts == completed_count + 1
            assert rows(p)[:completed_count] == history
            assert calls == ["POST"] * (completed_count + 1)
            assert len(list(config.paths.quarantine.glob("*.response-*.json"))) == 2
            assert (config.paths.quarantine / f"{job_id}.json").read_bytes() == provenance
            assert p.process_path(source, variant=variant, retry_failed_variants=True) == result
            assert len(calls) == completed_count + 1
    print(json.dumps({
        "quarantine_fault": quarantine_fault, "named": named,
        "completed_history": completed_count, "unflagged_additional_http": 0,
        "actual_sdk_post_count": len(calls), "history_preserved": True,
    }, sort_keys=True))


@pytest.mark.parametrize("mutation", ["job", "history"])
def test_queued_exhaustion_checks_final_snapshot_before_quarantine(
    sdk, tmp_path, quiet_logger, monkeypatch, mutation,
):
    config = config_for(tmp_path / "run", attempts=4)
    source = write_image(config.paths.source / "x.png")
    provider, _ = sdk.install(429, {}, enabled=True)
    timer = Clock()
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = admit(p, source).job_id
        assert p._dispatch_one(threading.Event(), job_id)
        timer.advance(30)
        p.config = replace(config, retry=replace(config.retry, max_attempts=1))
        original = p._validated_staged_cleanup_path
        observed = []

        def mutate(*args):
            path = original(*args)
            if mutation == "job":
                p.state.connection.execute(
                    "UPDATE jobs SET next_attempt_at=? WHERE job_id=?",
                    ("2030-01-02T00:00:00+00:00", job_id),
                )
            else:
                p.state.connection.execute(
                    "UPDATE attempts SET safe_message='changed evidence' WHERE job_id=?", (job_id,),
                )
            observed.append((p.state.get_job(job_id), rows(p)))
            return path

        monkeypatch.setattr(p, "_validated_staged_cleanup_path", mutate)
        with pytest.raises(AppError) as caught:
            p._dispatch_one(threading.Event(), job_id)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert (p.state.get_job(job_id), rows(p)) == observed[0]
        assert p.state.get_job(job_id).status == JobStatus.DISPATCHING
        assert len(sdk.calls) == 1
        assert not list(config.paths.quarantine.glob("*.json"))


@pytest.mark.parametrize("boundary", ["rollback", "postcommit"])
def test_queued_exhaustion_transaction_preserves_completed_history(
    sdk, tmp_path, quiet_logger, monkeypatch, boundary,
):
    config = config_for(tmp_path / "run", attempts=4)
    source = write_image(config.paths.source / "x.png")
    provider, _ = sdk.install(429, {}, enabled=True)
    timer = Clock()
    with processor_for(config, quiet_logger, timer, provider) as p:
        job_id = admit(p, source).job_id
        assert p._dispatch_one(threading.Event(), job_id)
        queued, history = p.state.get_job(job_id), rows(p)
        gate = p.state.provider_not_before(2)
        timer.advance(30)
        reduced = replace(config, retry=replace(config.retry, max_attempts=1))
        p.config = reduced
        if boundary == "rollback":
            original = p.state._fault

            def fault(point):
                if point == "mark_queued_retry_exhausted_after_update":
                    assert p.state.get_job(job_id).status == JobStatus.FAILED
                    raise RuntimeError("synthetic queued terminal transaction crash")
                original(point)

            monkeypatch.setattr(p.state, "_fault", fault)
        else:
            original = p.state.mark_queued_retry_exhausted

            def fault(*args):
                original(*args)
                raise RuntimeError("synthetic queued terminal transaction crash")

            monkeypatch.setattr(p.state, "mark_queued_retry_exhausted", fault)
        with pytest.raises(RuntimeError, match="synthetic queued terminal transaction crash"):
            p._dispatch_one(threading.Event(), job_id)
        current = p.state.get_job(job_id)
        assert current.status == (
            JobStatus.DISPATCHING if boundary == "rollback" else JobStatus.FAILED
        )
        assert replace(current, status=queued.status, updated_at=queued.updated_at) == queued
        assert rows(p) == history and p.state.provider_not_before(2) == gate
        assert not list(config.paths.quarantine.glob("*.json"))
        assert Path(queued.staged_path).exists()
    # A rolled-back transition can be retried under the same exhausted budget.
    with processor_for(reduced, quiet_logger, timer, provider) as p:
        assert p.process_path(source).status == JobStatus.FAILED
        assert rows(p) == history and p.state.provider_not_before(2) == gate
    with processor_for(config, quiet_logger, timer, provider) as p:
        assert p.process_path(source).status == JobStatus.FAILED
        assert rows(p) == history and p.state.provider_not_before(2) == gate
        assert len(sdk.calls) == 1
        assert not list(config.paths.quarantine.glob("*.response-*.json"))
