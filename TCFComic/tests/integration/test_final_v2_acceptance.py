"""Independent additions for remaining version-history/CAS/cooldown boundaries."""
import json
import threading
from dataclasses import replace
from datetime import timedelta

import pytest

from conftest import write_image
from fresh_response import capture_response
from test_retry_cda7_pacing import (
    Clock, Scripted, admit, config_for, processor_for, rows, tmp_path,
)
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.providers.openai import recorded_retryable_response
from tcfcomic.providers.fake import FakeProvider


@pytest.mark.parametrize("mode", ["explicit", "READY", "READY_RETRY"])
@pytest.mark.parametrize("version", ["v0", "v2", "missing"])
@pytest.mark.parametrize("ordinal", [1, 2])
def test_every_older_row_requires_current_complete_evidence(
    tmp_path, quiet_logger, mode, version, ordinal,
):
    config = config_for(tmp_path, attempts=1)
    source = write_image(config.paths.source / "x.png")
    fresh = capture_response(source, 429, enabled=True)
    assert recorded_retryable_response(fresh.safe_message, fresh.code) == 429
    timer = Clock()
    provider = Scripted(timer, [fresh])
    with processor_for(config, quiet_logger, timer, provider) as p:
        failed = p.process_path(source)
        for number in (2, 3):
            p.state.connection.execute(
                """INSERT INTO attempts SELECT job_id, ?, state, started_at, finished_at,
                   duration_ms, error_code, safe_message, provider_request_id, retry_after_seconds
                   FROM attempts WHERE job_id=? AND attempt_no=1""", (number, failed.job_id))
        # Negative tampering only. No modified message is used as positive authority.
        bad = (fresh.safe_message.replace(" Classification evidence v1 complete.", "")
               if version == "missing" else fresh.safe_message.replace("v1 complete.", version + " complete."))
        assert bad != fresh.safe_message
        p.state.connection.execute(
            "UPDATE attempts SET safe_message=? WHERE job_id=? AND attempt_no=?",
            (bad, failed.job_id, ordinal))
        failure_path = config.paths.quarantine / f"{failed.job_id}.json"
        payload = json.loads(failure_path.read_text())
        payload["attempts"] = 3
        failure_path.write_text(json.dumps(payload), encoding="utf-8")
        if mode != "explicit":
            p.state.connection.execute("UPDATE jobs SET status=? WHERE job_id=?", (mode, failed.job_id))
        history, failure_bytes = rows(p), failure_path.read_bytes()
        gate = p.state.provider_not_before(2)
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    timer.advance(30)
    with processor_for(config, quiet_logger, timer, provider) as p:
        before = p.state.get_job(failed.job_id)
        for invocation in range(2):
            if mode == "explicit":
                with pytest.raises(AppError) as caught:
                    p.process_path(source, retry_failed_variants=True)
                assert caught.value.code == ErrorCode.STATE_FAILED
                assert p.state.get_job(failed.job_id) == before
            else:
                assert p._dispatch_one(threading.Event(), failed.job_id) == (invocation == 0)
                denied = p.state.get_job(failed.job_id)
                assert denied.status == JobStatus.FAILED and denied.error_code == "STATE_FAILED"
                assert denied.next_attempt_at == before.next_attempt_at
            assert rows(p) == history
            assert failure_path.read_bytes() == failure_bytes
            assert p.state.provider_not_before(2) == gate
            assert p.state.attempt_count(failed.job_id) == 3
            assert len(provider.requests) == 1
        assert not list(config.paths.quarantine.glob("*.response-*.json"))


def test_begin_attempt_cas_compares_earlier_not_only_last_row(tmp_path, quiet_logger, monkeypatch):
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "x.png")
    error = capture_response(source, 429, enabled=True)
    timer = Clock()
    provider = Scripted(timer, [error, error])
    with processor_for(config, quiet_logger, timer, provider) as p:
        job = admit(p, source).job_id
        p._dispatch_one(threading.Event(), job)
        timer.advance(30)
        p._dispatch_one(threading.Event(), job)
        timer.advance(30)
        history = rows(p)
        original = p.state.begin_attempt_checked
        committed = []

        def competing_writer(expected, snapshot):
            # Commit an earlier-row change after authorization and claim, before
            # the checked transaction. Keep the most recent eligible row intact.
            p.state.connection.execute(
                "UPDATE attempts SET safe_message='legacy unknown earlier row' WHERE job_id=? AND attempt_no=1",
                (job,))
            assert not p.state.connection.in_transaction
            committed.append(rows(p))
            return original(expected, snapshot)

        monkeypatch.setattr(p.state, "begin_attempt_checked", competing_writer)
        with pytest.raises(AppError) as caught:
            p._dispatch_one(threading.Event(), job)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert len(committed) == 1 and rows(p) == committed[0]
        assert rows(p)[1] == history[1]
        assert p.state.attempt_count(job) == len(provider.requests) == 2


def test_real_final_429_exhaustion_retains_cooldown_on_quarantine_fault(
    tmp_path, quiet_logger, monkeypatch,
):
    config = config_for(tmp_path, attempts=1)
    source = write_image(config.paths.source / "x.png")
    error = capture_response(source, 429, enabled=True, headers={"retry-after": "75"})
    assert error.code == ErrorCode.PROVIDER_RETRYABLE
    timer = Clock()
    provider = Scripted(timer, [error])
    with processor_for(config, quiet_logger, timer, provider) as p:
        job = admit(p, source).job_id
        def fail(*args):
            assert p.state.get_job(job).status == JobStatus.FAILED
            raise AppError(ErrorCode.STATE_FAILED, "Synthetic quarantine fault.")
        monkeypatch.setattr(p, "_quarantine", fail)
        with pytest.raises(AppError, match="Synthetic quarantine fault"):
            p._dispatch_one(threading.Event(), job)
        history = rows(p)
        assert p.state.completed_attempts(job)[0].retry_after_seconds == 75
        assert p.state.provider_not_before(None) == timer.wall + timedelta(seconds=75)
    with processor_for(config, quiet_logger, timer, provider) as p:
        assert rows(p) == history
        assert p.state.get_job(job).status == JobStatus.FAILED
        other = admit(p, write_image(config.paths.source / "other.png")).job_id
        timer.advance(74)
        assert not p._dispatch_one(threading.Event(), other)
        timer.advance(1)
        assert p._dispatch_one(threading.Event(), other)
        assert p.state.attempt_count(job) == 1
        assert history[0] in rows(p)
    assert provider.starts == [0, 75]


def test_exhausted_quarantine_fault_cannot_reopen_on_budget_change_without_flag(
    tmp_path, quiet_logger, monkeypatch,
):
    """R06: a budget/config change is not explicit failed-response recovery."""
    config = config_for(tmp_path, attempts=1)
    source = write_image(config.paths.source / "x.png")
    error = capture_response(source, 429, enabled=True, headers={"retry-after": "75"})
    timer = Clock()
    provider = Scripted(timer, [error])
    with processor_for(config, quiet_logger, timer, provider) as p:
        job = admit(p, source).job_id
        def fail(*args):
            raise AppError(ErrorCode.STATE_FAILED, "Synthetic quarantine fault.")
        monkeypatch.setattr(p, "_quarantine", fail)
        with pytest.raises(AppError, match="Synthetic quarantine fault"):
            p._dispatch_one(threading.Event(), job)
        history = rows(p)
        assert len(history) == len(provider.requests) == 1
        assert p.state.provider_not_before(None) == timer.wall + timedelta(seconds=75)
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as p:
        try:
            result = p.process_path(source)  # Deliberately no recovery flag.
        except AppError as caught:
            assert caught.code == ErrorCode.STATE_FAILED
        print(json.dumps({
            "case": "exhausted-quarantine-fault-budget-change-no-flag",
            "calls": len(provider.requests),
            "attempts": p.state.attempt_count(job),
            "status": p.state.get_job(job).status.value,
            "starts": provider.starts,
            "recovery_audits": len(list(config.paths.quarantine.glob("*.response-*.json"))),
        }))
        assert history[0] in rows(p)
        assert len(provider.requests) == 1, "Exhausted failed submission reopened without explicit recovery"
        assert p.state.attempt_count(job) == 1


def test_permanent_quarantine_write_fault_keeps_completed_terminal_outcome(
    tmp_path, quiet_logger, monkeypatch,
):
    """New terminal ordering complements the retained unanswered crash fixtures."""
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = FakeProvider(["permanent"])
    with processor_for(config, quiet_logger, timer, provider) as p:
        def fail(*args):
            raise OSError("synthetic permanent quarantine write fault")
        monkeypatch.setattr(p, "_quarantine", fail)
        with pytest.raises(OSError, match="permanent quarantine"):
            p.process_path(source)
        job, = p.state.list_jobs()
        assert job.status == JobStatus.FAILED
        with pytest.raises(AppError, match="no active attempt") as inactive:
            p.state.active_attempt(job.job_id)
        assert inactive.value.code == ErrorCode.STATE_FAILED
        attempt, = p.state.completed_attempts(job.job_id)
        assert attempt.state.value == "permanent"
        assert attempt.error_code == ErrorCode.PROVIDER_PERMANENT
        history = rows(p)
        assert not list(config.paths.quarantine.glob("*.json"))
    with processor_for(config, quiet_logger, timer, provider) as p:
        result = p.process_path(source)
        assert result.status == JobStatus.FAILED and result.attempts == 1
        assert rows(p) == history
    assert len(provider.requests) == 1


def test_permanent_precommit_fault_never_prepares_quarantine_before_commit(
    tmp_path, quiet_logger,
):
    config = config_for(tmp_path)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    provider = FakeProvider(["permanent"])
    def crash(point):
        if point == "mark_failed_after_attempt":
            raise RuntimeError("synthetic precommit crash")
    with processor_for(config, quiet_logger, timer, provider,
                       state_transition_hook=crash) as p:
        with pytest.raises(RuntimeError, match="precommit crash"):
            p.process_path(source)
        job, = p.state.list_jobs()
        assert job.status == JobStatus.DISPATCHING
        assert p.state.active_attempt(job.job_id).attempt_no == 1
        assert not list(config.paths.quarantine.glob("*.json"))
    with processor_for(config, quiet_logger, timer, provider) as p:
        assert p.state.get_job(job.job_id).status == JobStatus.AMBIGUOUS
        assert p.process_path(source).attempts == 1
        assert len(list(config.paths.quarantine.glob("*.json"))) == 1
    assert len(provider.requests) == 1
