"""Partial-write and artifact denial contracts: never turn decode failures into HTTP retry."""
from __future__ import annotations

import base64
import io
from dataclasses import replace
from pathlib import Path

import httpx
import pytest

from conftest import write_image
from test_retry_cda7_pacing import sdk, tmp_path, failed, fresh_snapshot, rows, config_for, Clock, Scripted, processor_for
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.providers import worker
from tcfcomic.providers.openai import recorded_retryable_response
from tcfcomic.publication import initial_output_leaf


@pytest.mark.parametrize("kind", ["malformed", "overflow"])
@pytest.mark.parametrize("unlink_failure", [False, True])
def test_partial_chunk_cleanup_and_restart_no_retry(sdk, tmp_path, quiet_logger, monkeypatch, kind, unlink_failure):
    if kind == "malformed":
        encoded, limit = "AAAA" * 8192 + "!!!!", 30000
    else:
        # Encoded upper bound rounds up; final decoded bytes exceed limit only
        # after the first 32768-character chunk has actually reached the output.
        encoded, limit = base64.b64encode(b"x" * 24579).decode(), 24577
    provider, request = sdk.install(handler=lambda request: httpx.Response(
        200, json={"data": [{"b64_json": encoded}]}))
    request = replace(request, max_output_bytes=limit)
    root = tmp_path / "case"
    root.mkdir()
    config = config_for(root)
    temp = config.paths.destination / ("." + "a" * 32 + ".tmp")
    written, unlinks = [], []
    original_write = worker._BoundedOutput.write
    original_unlink = Path.unlink
    def observe(output, payload):
        count = original_write(output, payload)
        output._output.flush()
        written.append(temp.stat().st_size)
        return count
    def deny_unlink(path, *args, **kwargs):
        if path == temp:
            unlinks.append(True)
            raise OSError("synthetic partial cleanup refusal")
        return original_unlink(path, *args, **kwargs)
    with monkeypatch.context() as patcher:
        patcher.setattr(worker._BoundedOutput, "write", observe)
        if unlink_failure:
            patcher.setattr(Path, "unlink", deny_unlink)
        result = worker._execute(provider, request, temp)
    assert written == [24576]
    assert result.outcome == "permanent"
    assert result.error_code == (ErrorCode.OUTPUT_LIMIT_EXCEEDED if kind == "overflow" else ErrorCode.PROVIDER_PERMANENT)
    assert result.safe_message != "The provider worker failed safely."
    assert recorded_retryable_response(result.safe_message, result.error_code) is None
    assert len(sdk.calls) == 1
    assert temp.exists() is unlink_failure
    if unlink_failure:
        assert unlinks and temp.stat().st_size == 24576
    timer = Clock()
    never_called = Scripted(timer)
    with processor_for(config, quiet_logger, timer, never_called) as restarted:
        assert not restarted.state.list_jobs()
    assert never_called.requests == []
    # A standalone worker has no ledger ownership. Restart must NOT delete an
    # unknown artifact merely because its name looks like provider scratch.
    assert temp.exists() is unlink_failure
    assert not list(config.paths.destination.glob("*.png"))


@pytest.mark.parametrize("kind", ["malformed", "overflow"])
def test_durable_partial_output_unlink_failure_restart_cleans_without_dispatch(
    sdk, tmp_path, quiet_logger, monkeypatch, kind,
):
    encoded = "AAAA" * 8192 + "!!!!" if kind == "malformed" else base64.b64encode(b"x" * 24579).decode()
    limit = 30000 if kind == "malformed" else 24577
    provider, _ = sdk.install(handler=lambda request: httpx.Response(
        200, json={"data": [{"b64_json": encoded}]}))
    root = tmp_path / "durable"
    root.mkdir()
    config = config_for(root)
    config = replace(config, limits=replace(config.limits, max_output_bytes=limit))
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    original = Path.unlink
    hits = []
    def fail(path, *args, **kwargs):
        if path.parent == config.paths.destination and path.suffix == ".tmp":
            hits.append(path)
            raise OSError("synthetic durable scratch unlink refusal")
        return original(path, *args, **kwargs)
    with processor_for(config, quiet_logger, timer, provider) as p:
        with monkeypatch.context() as patcher:
            patcher.setattr(Path, "unlink", fail)
            with pytest.raises(AppError) as error:
                p.process_path(source)
        assert error.value.code == ErrorCode.STATE_FAILED
        assert hits and hits[-1].stat().st_size == 24576
        job, = p.state.list_jobs()
        assert job.status == JobStatus.FAILED
        assert job.temp_name == hits[-1].name
        history = rows(p)
    never_called = Scripted(timer)
    with processor_for(config, quiet_logger, timer, never_called) as restarted:
        result = restarted.process_path(source)
        assert result.status == JobStatus.FAILED and result.attempts == 1
        # Explicit recovery rejects non-HTTP diagnostics through its documented
        # AppError contract, not a returned ProcessResult for malformed evidence.
        with pytest.raises(AppError) as refusal:
            restarted.process_path(source, retry_failed_variants=True)
        assert refusal.value.code == ErrorCode.STATE_FAILED
        assert refusal.value.safe_message == (
            "Recovery skipped: permanent, unclassified historical evidence, or no matching definitive HTTP response."
        )
        assert rows(restarted) == history
        assert restarted.state.get_job(job.job_id).temp_name is None
    assert not hits[-1].exists()
    assert never_called.requests == []
    assert len(sdk.calls) == 1
    assert not list(config.paths.destination.glob("*.png"))


@pytest.mark.parametrize("artifact", ["old-stage", "published", "scratch", "temp-ref", "output-ref", "digest-ref"])
def test_recovery_denies_existing_artifacts_without_deleting_them(failed, artifact):
    p = failed.processor
    snapshot = fresh_snapshot(failed)
    path = None
    if artifact in {"old-stage", "published", "scratch"}:
        path = {
            "old-stage": Path(failed.job.staged_path),
            "published": failed.config.paths.destination / initial_output_leaf(snapshot, failed.job.job_id),
            "scratch": failed.config.paths.destination / "unknown.tmp",
        }[artifact]
        path.write_bytes(b"unknown artifact must be preserved")
    else:
        column, value = {
            "temp-ref": ("temp_name", ".unknown.tmp"),
            "output-ref": ("output_name", "unknown.png"),
            "digest-ref": ("output_sha256", "b" * 64),
        }[artifact]
        p.state.connection.execute(f"UPDATE jobs SET {column}=? WHERE job_id=?", (value, failed.job.job_id))
    before = p.state.get_job(failed.job.job_id)
    assert not p._reconsider_failed_response(before, snapshot)
    assert p.state.get_job(failed.job.job_id) == before
    assert rows(p) == failed.attempts
    assert not snapshot.staged_path.exists()
    if path:
        assert path.read_bytes() == b"unknown artifact must be preserved"
    assert len(failed.provider.requests) == 1


def test_url_only_response_cannot_trigger_download(sdk):
    provider, request = sdk.install(handler=lambda request: httpx.Response(
        200, json={"data": [{"url": "https://never-download.invalid/private.png"}]}))
    with pytest.raises(AppError) as error:
        provider.transform(request, io.BytesIO())
    assert error.value.code == ErrorCode.PROVIDER_PERMANENT
    assert len(sdk.calls) == 1
    assert "never-download" not in error.value.safe_message
