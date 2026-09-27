"""Bounded operator journeys, independent slots and explicitly guarded recovery."""
from __future__ import annotations

import hashlib
import json
import logging
import os
import signal
import subprocess
import sys
import threading
import time
from dataclasses import replace
from pathlib import Path

import pytest
import yaml

from conftest import write_config, write_image, write_mpo
from fresh_response import capture_response
from test_retry_cda7_pacing import (
    tmp_path, Clock, Scripted, config_for, processor_for, rows, failed, admit,
)
from tcfcomic import cli
from tcfcomic.domain import AppError, ErrorCode, JobStatus, PermanentProviderError, AmbiguousProviderError
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner


@pytest.mark.parametrize("mode", ["process", "watch"])
@pytest.mark.parametrize("reason", ["eligible", "moderation", "unanswered"])
def test_named_recovery_never_replays_success_and_uses_original(tmp_path, quiet_logger, mode, reason):
    config = config_for(tmp_path, named=True, attempts=1)
    source = write_image(config.paths.source / "x.png")
    source_bytes = source.read_bytes()
    timer = Clock()
    failure = {
        "eligible": capture_response(source),
        "moderation": PermanentProviderError(ErrorCode.PROVIDER_PERMANENT,
            "The provider rejected the request. HTTP 400. Provider code moderation_blocked. The provider blocked the input or generated output under its content policy."),
        "unanswered": AmbiguousProviderError(ErrorCode.PROVIDER_AMBIGUOUS,
            "The provider request ended with an uncertain dispatch result."),
    }[reason]
    provider = Scripted(timer, [None, failure])
    with processor_for(config, quiet_logger, timer, provider) as p:
        first = p.process_variants(source)
        success = p.state.get_job(first[0].job_id)
        old_history = rows(p)
        output = first[0].output_path
        output_bytes = output.read_bytes()
    config = replace(config, retry=replace(config.retry, max_attempts=4),
                     provider=replace(config.provider, prompts={"one": "new one", "two": "new two"}))
    with processor_for(config, quiet_logger, timer, provider) as p:
        # Identity change without explicit authorization cannot reopen failure.
        assert [r.attempts for r in p.process_variants(source)] == [1, 1]
        hits = []
        original = p._reconsider_failed_response
        def observe(job, snapshot):
            hits.append(job.job_id)
            return original(job, snapshot)
        p._reconsider_failed_response = observe
        if mode == "watch":
            event = threading.Event()
            stop_at = timer.elapsed + 65
            timer.on_wait = lambda event, seconds: event.set() if timer.elapsed >= stop_at else None
            p.watch(shutdown_event=event, max_cycles=100, retry_failed_variants=True)
            assert event.is_set()
        else:
            p.process_variants(source, retry_failed_variants=True)
        assert len(hits) == 1  # Watch reconsideration is once per observed version/slot.
        assert p.state.get_job(success.job_id) == success
        current = p.state.get_job(first[1].job_id)
        assert current.status == (JobStatus.SUCCEEDED if reason == "eligible" else (
            JobStatus.AMBIGUOUS if reason == "unanswered" else JobStatus.FAILED))
        assert p.state.attempt_count(current.job_id) == (2 if reason == "eligible" else 1)
        for old in old_history:
            assert old in rows(p)
        p.process_variants(source, retry_failed_variants=True)
        assert len(provider.requests) == (3 if reason == "eligible" else 2)
    assert output.read_bytes() == output_bytes
    assert source.read_bytes() == source_bytes
    assert provider.inputs == [hashlib.sha256(source_bytes).hexdigest()] * len(provider.requests)
    if reason == "eligible":
        assert provider.requests[-1].variant == "two" and provider.requests[-1].prompt == "new two"


@pytest.mark.parametrize("enabled", [False, True])
def test_watch_input_mpo_guard_and_coexisting_flags(tmp_path, quiet_logger, monkeypatch, enabled):
    from test_mpo_recovery import historical_failure
    config = config_for(tmp_path)
    source = write_mpo(config.paths.source / "x.JPG")
    timer, provider = Clock(), FakeProvider()
    with processor_for(config, quiet_logger, timer, provider) as p:
        original = historical_failure(p, source, monkeypatch)
        p.config = replace(config, provider=replace(config.provider, prompt="explicit changed input prompt"))
        before = source.read_bytes()
        event = threading.Event()
        timer.on_wait = lambda event, seconds: event.set() if timer.elapsed >= 3 else None
        p.watch(shutdown_event=event, max_cycles=10, retry_input_rejection=enabled, retry_failed_variants=True)
        assert event.is_set()
        job = p.state.get_job(original.job_id)
        assert job.status == (JobStatus.SUCCEEDED if enabled else JobStatus.FAILED)
        assert p.state.attempt_count(job.job_id) == (1 if enabled else 0)
        assert len(provider.requests) == (1 if enabled else 0)
        assert source.read_bytes() == before


def test_input_flag_alone_never_authorizes_provider_failure(failed):
    result = failed.processor.process_path(failed.source, retry_input_rejection=True)
    assert result.status == JobStatus.FAILED and result.attempts == 1
    assert failed.processor.state.get_job(result.job_id) == failed.job
    assert rows(failed.processor) == failed.attempts


@pytest.mark.parametrize("command", ["process", "watch"])
@pytest.mark.parametrize("invalid", ["disabled", "nonazure", "reset"])
def test_invalid_recovery_flags_fail_before_auth_or_runtime(tmp_path, monkeypatch, command, invalid):
    if invalid == "reset" and command == "process":
        # Parser has no process reset option; actual parser refusal is evidence.
        with pytest.raises(SystemExit) as error:
            cli.build_parser().parse_args(["process", "--config", "unused", "--reset-state", "unused.png"])
        assert error.value.code == 2
        return
    provider = "fake" if invalid == "nonazure" else "azure_openai"
    path, config = write_config(tmp_path, provider=provider,
                               endpoint="https://offline.openai.azure.com" if provider == "azure_openai" else None)
    data = yaml.safe_load(path.read_text())
    data["retry"]["azure_response_retries"] = invalid == "reset"
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    def forbidden(*args, **kwargs):
        pytest.fail("invalid flags reached auth/runtime")
    monkeypatch.setattr(cli, "require_provider_credentials", forbidden)
    monkeypatch.setattr(cli, "DestinationSession", forbidden)
    args = [command, "--config", str(path), "--retry-failed-variants"]
    if invalid == "reset":
        args += ["--reset-state"]
    if command == "process":
        args += [str(config.paths.source / "unused.png")]
    assert cli.main(args) == 2


def test_validate_is_readonly_and_never_probes_or_authenticates(tmp_path, monkeypatch):
    path, _ = write_config(tmp_path)
    before = {str(p.relative_to(tmp_path)): p.read_bytes() for p in tmp_path.rglob("*") if p.is_file()}
    def forbidden(*args, **kwargs):
        pytest.fail("read-only validate reached a writable or authentication phase")
    monkeypatch.setattr(cli, "DestinationSession", forbidden)
    monkeypatch.setattr(cli, "Processor", forbidden)
    monkeypatch.setattr(cli, "require_provider_credentials", forbidden)
    monkeypatch.setattr(Path, "mkdir", forbidden)
    monkeypatch.setattr(Path, "unlink", forbidden)
    original_open = Path.open
    def readonly(path, mode="r", *args, **kwargs):
        assert not any(c in mode for c in "wax+"), "validate opened a writable probe"
        return original_open(path, mode, *args, **kwargs)
    monkeypatch.setattr(Path, "open", readonly)
    assert cli.main(["validate", "--config", str(path)]) == 0
    assert {str(p.relative_to(tmp_path)): p.read_bytes() for p in tmp_path.rglob("*") if p.is_file()} == before


def offline_cli():
    """Real CLI subprocess; only provider execution is replaced, never live credentials."""
    for key in tuple(os.environ):
        if key.upper().startswith(("OPENAI_", "AZURE_OPENAI_")):
            del os.environ[key]
    os.environ["AZURE_OPENAI_API_KEY"] = "dummy-azure"
    import socket
    def deny(*args, **kwargs):
        raise AssertionError("network forbidden in CLI fixture")
    socket.socket.connect = deny
    socket.getaddrinfo = deny
    trace = Path(os.environ["CDA7_TRACE"])
    mode = os.environ["CDA7_OUTCOME"]
    class Provider:
        def transform(self, request, output):
            with trace.open("a", encoding="utf-8") as stream:
                stream.write(json.dumps({"variant": request.variant, "job_id": request.job_id,
                    "sha256": hashlib.sha256(request.source.staged_path.read_bytes()).hexdigest()}) + "\n")
            if request.variant == "two" and mode == "partial":
                raise capture_response(request.source.staged_path)
            if request.variant == "two" and mode == "auth":
                raise PermanentProviderError(ErrorCode.AUTHENTICATION_FAILED, "Synthetic authentication failure.")
            if request.variant == "two" and mode == "interrupt":
                raise KeyboardInterrupt()
            return FakeProvider().transform(request, output)
    class Heartbeat(logging.Handler):
        def emit(self, record):
            if getattr(record, "event", None) == "watch_heartbeat":
                trace.with_suffix(".ready").write_text("heartbeat observed", encoding="utf-8")
    real = cli.Processor
    def factory(config, logger, **kwargs):
        logger.addHandler(Heartbeat())
        return real(config, logger, runner=InProcessAttemptRunner(Provider()), **kwargs)
    cli.Processor = factory
    raise SystemExit(cli.main(sys.argv[1:]))


def test_real_cli_partial_recovery_then_bounded_watch_no_success_replay(tmp_path):
    path, config = write_config(tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
                               max_attempts=1)
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = {"one": "first original", "two": "second original"}
    data["retry"]["azure_response_retries"] = True
    data["watch"]["heartbeat_seconds"] = 1
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    source = write_image(config.paths.source / "x.png")
    original = source.read_bytes()
    trace = tmp_path / "calls.trace"
    workspace = Path(__file__).resolve().parents[2]
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("OPENAI_", "AZURE_OPENAI_"))}
    env.update(CDA7_TRACE=str(trace), CDA7_OUTCOME="partial", PYTHONDONTWRITEBYTECODE="1",
               PYTHONPATH=os.pathsep.join(str(workspace / p) for p in ["src", "tests", "tests/unit", "tests/integration"]))
    base = [sys.executable, "-B", "-c", "from test_retry_cda7_operator import offline_cli; offline_cli()"]
    def process():
        return subprocess.run(base + ["process", "--config", str(path), "--retry-failed-variants", str(source)],
                              env=env, cwd=tmp_path, capture_output=True, text=True, timeout=20)
    first = process()
    assert first.returncode == 5, (first.stdout, first.stderr)
    outputs = [Path(line) for line in first.stdout.splitlines() if line.strip()]
    assert len(outputs) == 1 and outputs[0].is_file()
    success_bytes = outputs[0].read_bytes()
    assert len(trace.read_text().splitlines()) == 2
    data["retry"]["max_attempts"] = 4
    data["provider"]["prompts"] = {"one": "changed not replayed", "two": "changed explicitly"}
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    env["CDA7_OUTCOME"] = "success"
    second = process()
    assert second.returncode == 0, (second.stdout, second.stderr)
    assert len(trace.read_text().splitlines()) == 3
    assert outputs[0].read_bytes() == success_bytes
    artifacts = {str(p): p.read_bytes() for p in config.paths.destination.rglob("*.json")}
    command = base + ["watch", "--config", str(path), "--retry-input-rejection", "--retry-failed-variants"]
    child = subprocess.Popen(command, env=env, cwd=tmp_path, text=True,
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                             creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0)
    try:
        deadline = time.monotonic() + 12
        while not trace.with_suffix(".ready").exists():
            assert child.poll() is None, child.communicate(timeout=2)
            assert time.monotonic() < deadline, "watch heartbeat not observed before deadline"
            time.sleep(.02)
        child.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
        stdout, stderr = child.communicate(timeout=10)
        assert child.returncode == 0, (stdout, stderr)
    finally:
        if child.poll() is None:
            child.kill()
            child.communicate(timeout=5)
    assert len(trace.read_text().splitlines()) == 3
    assert outputs[0].read_bytes() == success_bytes
    assert source.read_bytes() == original
    assert {str(p): p.read_bytes() for p in config.paths.destination.rglob("*.json")} == artifacts


@pytest.mark.parametrize("mode,expected", [("auth", 3), ("interrupt", 130)])
def test_real_cli_preserves_early_success_and_stops_after_auth_or_interrupt(tmp_path, mode, expected):
    path, config = write_config(tmp_path, provider="azure_openai",
                               endpoint="https://offline.openai.azure.com", max_attempts=1)
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = {"one": "first", "two": "second", "three": "must not dispatch"}
    data["retry"]["azure_response_retries"] = True
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    source = write_image(config.paths.source / "x.png")
    trace = tmp_path / "calls.trace"
    workspace = Path(__file__).resolve().parents[2]
    env = {k: v for k, v in os.environ.items() if not k.upper().startswith(("OPENAI_", "AZURE_OPENAI_"))}
    env.update(CDA7_TRACE=str(trace), CDA7_OUTCOME=mode, PYTHONDONTWRITEBYTECODE="1",
               PYTHONPATH=os.pathsep.join(str(workspace / p) for p in
                                         ["src", "tests", "tests/unit", "tests/integration"]))
    result = subprocess.run([
        sys.executable, "-B", "-c", "from test_retry_cda7_operator import offline_cli; offline_cli()",
        "process", "--config", str(path), str(source),
    ], env=env, cwd=tmp_path, text=True, capture_output=True, timeout=15)
    assert result.returncode == expected, (result.stdout, result.stderr)
    outputs = result.stdout.splitlines()
    assert len(outputs) == 1 and Path(outputs[0]).is_file()
    assert [json.loads(line)["variant"] for line in trace.read_text().splitlines()] == ["one", "two"]
    if mode == "auth":
        assert "AUTHENTICATION_FAILED" in result.stderr
