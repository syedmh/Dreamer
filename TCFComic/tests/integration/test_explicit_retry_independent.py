from __future__ import annotations

import hashlib
import json
import os
import signal
import sqlite3
import subprocess
import sys
import time
from contextlib import closing
from dataclasses import replace
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_mpo
from test_explicit_input_retry import changed_prompt, retry_admit, transitions
from test_mpo_recovery import historical_failure, make_processor
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.providers.fake import FakeProvider


def test_real_cli_changed_prompt_retry_offline_operator_journey(
    tmp_path, quiet_logger, monkeypatch,
):
    config_path, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    original_source = source.read_bytes()
    original_stat = source.stat()
    with make_processor(config, quiet_logger) as processor:
        original = historical_failure(processor, source, monkeypatch)
        state_path = processor.state.path
    provenance_path = config.paths.quarantine / f"{original.job_id}.json"
    provenance = provenance_path.read_bytes()
    prompt = "INDEPENDENT synthetic current prompt"
    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "provider:\n", f"provider:\n  prompt: {json.dumps(prompt)}\n",
        ),
        encoding="utf-8",
    )
    shim = tmp_path / "offline-shim"
    shim.mkdir()
    trace = tmp_path / "fake-calls.jsonl"
    (shim / "sitecustomize.py").write_text(
        """import json
import os
import socket
import webbrowser
from pathlib import Path
from tcfcomic.providers.fake import FakeProvider

from tcfcomic.processor import Processor
_admit_watch_variants = Processor._admit_watch_variants

def record_watch_admission(self, candidate, event, **kwargs):
    settled = _admit_watch_variants(self, candidate, event, **kwargs)
    if settled:
        Path(os.environ["TCF_TEST_TRACE"]).with_suffix(".ready").write_text(
            "stable source reconsidered", encoding="utf-8")
    return settled

Processor._admit_watch_variants = record_watch_admission

_connect = socket.socket.connect
_connect_ex = socket.socket.connect_ex
_getaddrinfo = socket.getaddrinfo

def local(address):
    return isinstance(address, tuple) and str(address[0]).lower() in {
        "127.0.0.1", "::1", "localhost"
    }

def connect(sock, address):
    if not local(address):
        raise AssertionError("External network forbidden in offline CLI journey")
    return _connect(sock, address)

def connect_ex(sock, address):
    if not local(address):
        raise AssertionError("External network forbidden in offline CLI journey")
    return _connect_ex(sock, address)

def getaddrinfo(host, port, *args, **kwargs):
    if not local((host, port)):
        raise AssertionError("External DNS forbidden in offline CLI journey")
    return _getaddrinfo(host, port, *args, **kwargs)

def no_browser(*args, **kwargs):
    raise AssertionError("Browser forbidden in offline CLI journey")

socket.socket.connect = connect
socket.socket.connect_ex = connect_ex
socket.getaddrinfo = getaddrinfo
webbrowser.open = webbrowser.open_new = webbrowser.open_new_tab = no_browser
_transform = FakeProvider.transform

def record_transform(self, request, output):
    with Path(os.environ["TCF_TEST_TRACE"]).open("a", encoding="utf-8") as stream:
        stream.write(json.dumps({
            "job_id": request.job_id, "prompt": request.prompt,
            "model": request.model, "pid": os.getpid(),
        }) + "\\n")
    return _transform(self, request, output)

FakeProvider.transform = record_transform
""",
        encoding="utf-8",
    )
    project = Path(__file__).parents[2]
    env = {
        key: value for key, value in os.environ.items()
        if key.upper() in {"SYSTEMROOT", "WINDIR", "SYSTEMDRIVE", "PATH", "PATHEXT", "COMSPEC"}
    }
    for name in ("HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP"):
        directory = tmp_path / "isolated-home" / name
        directory.mkdir(parents=True)
        env[name] = str(directory)
    env.update({
        "PYTHONPATH": os.pathsep.join((str(shim), str(project / "src"))),
        "PYTHONDONTWRITEBYTECODE": "1",
        "TCF_TEST_TRACE": str(trace),
    })

    def run(command, *extra):
        argv = [
            sys.executable, "-m", "tcfcomic", command,
            "--config", str(config_path), *extra,
        ]
        result = subprocess.run(
            argv, cwd=tmp_path, env=env, capture_output=True,
            text=True, encoding="utf-8", timeout=30, check=False,
        )
        print(f"CLI {command} {' '.join(extra)} -> exit {result.returncode}")
        assert "Error in sitecustomize" not in result.stderr
        return result

    def rows():
        with closing(sqlite3.connect(state_path)) as connection:
            connection.row_factory = sqlite3.Row
            jobs = [dict(row) for row in connection.execute("SELECT * FROM jobs")]
            attempts = [dict(row) for row in connection.execute("SELECT * FROM attempts")]
            return jobs, attempts

    before = rows()
    denied = run("process", str(source))
    assert denied.returncode == 4, denied.stderr
    assert denied.stdout == ""
    assert "Current request settings differ" in denied.stderr
    assert "--retry-input-rejection" in denied.stderr
    assert rows() == before
    assert not trace.exists() and transitions(config) == []

    succeeded = run("process", "--retry-input-rejection", str(source))
    assert succeeded.returncode == 0, succeeded.stderr
    assert len(succeeded.stdout.splitlines()) == 1
    output = Path(succeeded.stdout.strip())
    assert output.parent == config.paths.destination
    with Image.open(output) as image:
        assert image.format == "PNG"
        image.verify()
    calls = [json.loads(line) for line in trace.read_text().splitlines()]
    assert len(calls) == 1
    assert calls[0] == {
        "job_id": original.job_id, "prompt": prompt,
        "model": config.provider.model, "pid": calls[0]["pid"],
    }
    assert calls[0]["pid"] != os.getpid()
    jobs, attempts = rows()
    job, = jobs
    attempt, = attempts
    assert job["job_id"] == attempt["job_id"] == original.job_id
    assert job["status"] == "SUCCEEDED" and attempt["attempt_no"] == 1
    assert job["prompt_hash"] == hashlib.sha256(prompt.encode()).hexdigest()
    for field in ("created_at", "source_path", "source_path_key", "source_name", "size", "mtime_ns", "sha256"):
        assert job[field] == getattr(original, field)
    audit, = transitions(config)
    payload = audit.read_bytes()
    data = json.loads(payload)
    fingerprint = lambda model, digest: hashlib.sha256(json.dumps(
        ["fake", model, digest], separators=(",", ":"),
    ).encode()).hexdigest()
    assert data == {
        "schema_version": 1,
        "kind": "mpo-input-request-transition",
        "job_id": original.job_id,
        "source_binding": {
            "path_key": original.source_path_key, "size": original.size,
            "mtime_ns": original.mtime_ns, "sha256": original.sha256,
        },
        "original_provenance_sha256": hashlib.sha256(provenance).hexdigest(),
        "old_request_sha256": fingerprint(original.model, original.prompt_hash),
        "new_request_sha256": fingerprint(job["model"], job["prompt_hash"]),
    }
    assert payload == (json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n").encode()
    assert audit.name == f"{original.job_id}.mpo-request-transition.{hashlib.sha256(payload).hexdigest()}.json"
    assert prompt.encode() not in payload and config.provider.prompt.encode() not in payload
    assert provenance_path.read_bytes() == provenance
    assert (config.paths.quarantine / f"{original.job_id}.mpo-input-failure.json").read_bytes() == provenance
    output_bytes = output.read_bytes()
    output_mtime = output.stat().st_mtime_ns
    repeat = run("process", "--retry-input-rejection", str(source))
    assert repeat.returncode == 0 and repeat.stdout == succeeded.stdout, repeat.stderr
    assert rows() == (jobs, attempts)
    assert trace.read_text().splitlines() == [json.dumps(calls[0])]
    child = subprocess.Popen(
        [sys.executable, "-m", "tcfcomic", "watch", "--config", str(config_path),
         "--retry-input-rejection"],
        cwd=tmp_path, env=env, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        text=True, encoding="utf-8",
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
    )
    try:
        deadline = time.monotonic() + 15
        while not trace.with_suffix(".ready").exists():
            assert child.poll() is None, child.communicate(timeout=2)
            assert time.monotonic() < deadline, "watch did not reconsider stable source"
            time.sleep(0.02)
        child.send_signal(signal.CTRL_BREAK_EVENT if os.name == "nt" else signal.SIGTERM)
        stdout, stderr = child.communicate(timeout=10)
        assert child.returncode == 0 and stdout == "", (stdout, stderr)
        assert "Error in sitecustomize" not in stderr
        assert "Already processed this prompt variant; skipped." in stderr
    finally:
        if child.poll() is None:
            child.kill()
            child.communicate(timeout=5)
    assert rows() == (jobs, attempts)
    assert len(trace.read_text().splitlines()) == 1
    assert output.read_bytes() == output_bytes and output.stat().st_mtime_ns == output_mtime
    assert source.read_bytes() == original_source
    assert source.stat().st_mtime_ns == original_stat.st_mtime_ns
    assert transitions(config) == [audit] and audit.read_bytes() == payload


@pytest.mark.parametrize("crash_point", [
    "mpo_requeue_before_update", "mpo_requeue_after_update", "mpo_requeue_after_commit",
])
def test_adoption_is_atomic_to_independent_sqlite_reader(
    tmp_path, quiet_logger, monkeypatch, crash_point,
):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        identity = processor._request_identity()
        with closing(sqlite3.connect(processor.state.path, isolation_level=None)) as observer:
            def row():
                return observer.execute(
                    "SELECT status,provider,model,prompt_hash,staged_path,error_code FROM jobs WHERE job_id=?",
                    (original.job_id,),
                ).fetchone()

            old = row()
            seen = []

            def fault(point):
                if not point.startswith("mpo_requeue_"):
                    return
                external = row()
                seen.append(point)
                if point == "mpo_requeue_after_commit":
                    assert external[:4] == ("READY", identity.provider, identity.model, identity.prompt_hash)
                    assert external[4] != original.staged_path and external[5] is None
                    assert Path(external[4]).read_bytes() == source.read_bytes()
                else:
                    assert external == old
                if point == crash_point:
                    raise RuntimeError("independent crash boundary")

            processor.state._transition_hook = fault
            with pytest.raises(RuntimeError, match="independent crash boundary"):
                retry_admit(processor, source)
            assert seen[-1] == crash_point
            if crash_point != "mpo_requeue_after_commit":
                assert row() == old
                assert not list(processor.staging.glob("*.input"))
            else:
                assert row()[:4] == ("READY", identity.provider, identity.model, identity.prompt_hash)
                assert Path(row()[4]).is_file()
        assert processor.state.attempt_count(original.job_id) == 0 and provider.requests == []


@pytest.mark.parametrize("resume_prompt", ["old", "adopted", "different"])
@pytest.mark.parametrize("explicit", [False, True])
def test_restart_after_commit_requires_adopted_settings(
    tmp_path, quiet_logger, monkeypatch, resume_prompt, explicit,
):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    adopted = changed_prompt(config, "synthetic adopted identity")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = historical_failure(processor, source, monkeypatch)
        processor.config = adopted

        def fault(point):
            if point == "mpo_requeue_after_commit":
                raise RuntimeError("committed interruption")

        processor.state._transition_hook = fault
        with pytest.raises(RuntimeError, match="committed interruption"):
            retry_admit(processor, source)
        stage = Path(processor.state.get_job(original.job_id).staged_path)
        assert stage.is_file()
        audit, = transitions(config)
        evidence = audit.read_bytes()
    resumed = {
        "old": config, "adopted": adopted,
        "different": changed_prompt(config, "synthetic unrelated identity"),
    }[resume_prompt]
    with make_processor(resumed, quiet_logger, provider) as processor:
        before = processor.state.get_job(original.job_id)
        assert before.status == JobStatus.READY and stage.is_file()
        result = processor.process_path(source, retry_input_rejection=explicit)
        assert result.job_id == original.job_id
        if resume_prompt == "adopted":
            assert result.status == JobStatus.SUCCEEDED
            assert len(provider.requests) == result.attempts == 1
            assert provider.requests[0].prompt == adopted.provider.prompt
        else:
            assert result.status == JobStatus.FAILED and result.error_code == ErrorCode.STATE_FAILED
            assert provider.requests == [] and result.attempts == 0
            assert processor.state.get_job(original.job_id).prompt_hash == before.prompt_hash
        assert transitions(config) == [audit] and audit.read_bytes() == evidence


@pytest.mark.parametrize("mode", ["watch", "process"])
def test_precommit_transition_audit_cannot_authorize_restart(
    tmp_path, quiet_logger, monkeypatch, mode,
):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    current = changed_prompt(config)
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = historical_failure(processor, source, monkeypatch)
        processor.config = current

        def fault(point):
            if point == "mpo_requeue_after_update":
                raise RuntimeError("uncommitted interruption")

        processor.state._transition_hook = fault
        with pytest.raises(RuntimeError, match="uncommitted interruption"):
            retry_admit(processor, source)
        audit, = transitions(config)
        evidence = audit.read_bytes()
    with make_processor(current, quiet_logger, provider) as processor:
        if mode == "watch":
            processor.watch(max_cycles=5)
        else:
            assert processor.process_path(source).status == JobStatus.FAILED
        assert processor.state.get_job(original.job_id) == original
        assert processor.state.attempt_count(original.job_id) == 0 and provider.requests == []
        assert not list(processor.staging.glob("*.input"))
        assert transitions(config) == [audit] and audit.read_bytes() == evidence
        assert processor.process_path(source, retry_input_rejection=True).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1


def test_cleanup_keeps_fresh_stage_acquired_by_newly_admitted_job(
    tmp_path, quiet_logger, monkeypatch,
):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    other_source = write_mpo(config.paths.source / "new-owner.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        real_cas = processor.state.requeue_never_sent_mpo
        owners = []

        def race(expected, snapshot, identity, check, **kwargs):
            info = other_source.stat()
            other_snapshot = replace(
                snapshot, path=other_source, normalized_path=os.path.normcase(str(other_source)),
                size=info.st_size, mtime_ns=info.st_mtime_ns,
            )
            owner = processor.state.admit(other_snapshot, identity)
            assert owner.admitted and owner.job_id != original.job_id
            owners.append((owner.job_id, snapshot.staged_path))
            return real_cas(expected, snapshot, identity, check, **kwargs)

        monkeypatch.setattr(processor.state, "requeue_never_sent_mpo", race)
        with pytest.raises(AppError, match="ownership") as caught:
            retry_admit(processor, source)
        assert caught.value.code == ErrorCode.STATE_FAILED
        assert processor.state.get_job(original.job_id) == original
        owner_id, stage = owners[0]
        owner = processor.state.get_job(owner_id)
        assert owner.status == JobStatus.READY and owner.staged_path == str(stage)
        assert stage.read_bytes() == other_source.read_bytes()
        assert provider.requests == [] and processor.state.attempt_count(original.job_id) == 0
    with make_processor(changed_prompt(config), quiet_logger, provider) as processor:
        assert stage.is_file()
        result = processor.process_path(other_source)
        assert result.status == JobStatus.SUCCEEDED and result.job_id == owner_id
        assert len(provider.requests) == 1
        assert processor.state.get_job(original.job_id) == original
