from __future__ import annotations

import hashlib
import io
import json
import logging
import os
import threading
from contextlib import ExitStack
from dataclasses import replace
from pathlib import Path
from types import SimpleNamespace

import pytest
from PIL import Image

from conftest import write_config, write_image, write_mpo
from test_mpo_recovery import Crash, admit, historical_failure, make_processor
from test_rate_pacing import ManualTime, TimedProvider
from tcfcomic import cli, quarantine
from tcfcomic.domain import AppError, AttemptState, ErrorCode, JobStatus, ShutdownToken, StableCandidate
from tcfcomic.logging_setup import configure_logging
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.openai import _prepare_upload
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.publication import initial_output_leaf
from tcfcomic.redaction import redact_secret
from tcfcomic.scanner import stage_candidate


def retry_admit(processor, source):
    info = source.stat()
    return processor._admit(
        source, candidate=StableCandidate(source, info.st_size, info.st_mtime_ns),
        shutdown_event=threading.Event(), retry_input_rejection=True,
    )


def changed_prompt(config, prompt="current synthetic prompt"):
    return replace(config, provider=replace(config.provider, prompt=prompt))


def transitions(config):
    return sorted(config.paths.quarantine.glob("*.mpo-request-transition.*.json"))


def identity_fingerprint(identity):
    return hashlib.sha256(json.dumps(
        [identity.provider, identity.model, identity.prompt_hash],
        ensure_ascii=True, separators=(",", ":"),
    ).encode()).hexdigest()


class OfflineSession:
    started = True

    def acquire(self, *args):
        return self

    def require_valid(self, *args):
        pass


class OfflineAuthenticatedRunner(InProcessAttemptRunner):
    def run_authenticated(self, provider, request, temp_path, deadline, token):
        return self.run(provider, request, temp_path, deadline)


def test_explicit_process_retries_changed_prompt_same_job_once(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = historical_failure(processor, source, monkeypatch)
        processor.config = replace(config, provider=replace(config.provider, prompt="current synthetic prompt"))
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.SUCCEEDED
        assert result.job_id == original.job_id
        assert result.attempts == len(provider.requests) == 1
        assert provider.requests[0].prompt == "current synthetic prompt"
        current = processor.state.get_job(original.job_id)
        assert current.prompt_hash == processor._request_identity().prompt_hash
        assert current.created_at == original.created_at
        assert (current.source_path_key, current.size, current.mtime_ns, current.sha256) == (
            original.source_path_key, original.size, original.mtime_ns, original.sha256,
        )
        assert processor.process_path(source, retry_input_rejection=True).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1


@pytest.mark.parametrize("field", [
    "name", "model", "prompt", "endpoint", "authentication", "tenant_id", "client_id", "redirect_uri",
])
@pytest.mark.parametrize("mode", ["process", "watch", "explicit"])
def test_settings_changes_require_explicit_permission(tmp_path, quiet_logger, monkeypatch, field, mode):
    _, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    if field in {"tenant_id", "client_id", "redirect_uri"}:
        config = replace(config, provider=replace(config.provider, authentication="interactive"))
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    records = []

    class Capture(logging.Handler):
        def emit(self, record):
            records.append(record)

    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(Capture())
    with Processor(
        config, quiet_logger, runner=OfflineAuthenticatedRunner(provider), auth_session=OfflineSession(),
    ) as processor:
        job = historical_failure(processor, source, monkeypatch)
        provenance = (config.paths.quarantine / f"{job.job_id}.json").read_bytes()
        values = {
            "name": "openai", "model": "different-model", "prompt": "private changed prompt",
            "endpoint": "https://other-offline.openai.azure.com", "authentication": "interactive",
            "tenant_id": "11111111-1111-1111-1111-111111111111",
            "client_id": "22222222-2222-2222-2222-222222222222",
            "redirect_uri": "http://localhost:8888",
        }
        processor.config = replace(config, provider=replace(config.provider, **{field: values[field]}))
        identity = processor._request_identity()
        if mode == "watch":
            processor.watch(max_cycles=5)
        else:
            result = processor.process_path(source, retry_input_rejection=mode == "explicit")
            assert result.job_id == job.job_id
        current = processor.state.get_job(job.job_id)
        if mode != "explicit":
            assert current == job
            assert provider.requests == []
            assert transitions(config) == []
            # A second observation must neither authorize recovery nor spam the diagnostic.
            assert processor.process_path(source).error_code == ErrorCode.INVALID_IMAGE
            messages = [r.safe_message for r in records if r.event == "input_rejection_settings_mismatch"]
            assert len(messages) == 1
            assert "Current request settings differ from this never-sent input rejection" in messages[0]
            assert '--config "<config-file>" --retry-input-rejection "<image-path>"' in messages[0]
            return
        assert current.status == JobStatus.SUCCEEDED
        assert (current.provider, current.model, current.prompt_hash) == (
            identity.provider, identity.model, identity.prompt_hash,
        )
        assert current.created_at == job.created_at
        assert len(provider.requests) == 1
        assert provider.requests[0].prompt == processor.config.provider.prompt
        assert provider.requests[0].model == processor.config.provider.model
        audit, = transitions(config)
        payload = audit.read_bytes()
        data = json.loads(payload)
        assert data == {
            "schema_version": 1, "kind": "mpo-input-request-transition", "job_id": job.job_id,
            "source_binding": {
                "path_key": job.source_path_key, "size": job.size,
                "mtime_ns": job.mtime_ns, "sha256": job.sha256,
            },
            "original_provenance_sha256": hashlib.sha256(provenance).hexdigest(),
            "old_request_sha256": identity_fingerprint(job),
            "new_request_sha256": identity_fingerprint(identity),
        }
        assert payload == (json.dumps(data, sort_keys=True, separators=(",", ":")) + "\n").encode()
        assert audit.name == f"{job.job_id}.mpo-request-transition.{hashlib.sha256(payload).hexdigest()}.json"
        assert (config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json").read_bytes() == provenance
        assert (config.paths.quarantine / f"{job.job_id}.json").read_bytes() == provenance
        for private in (str(source), config.provider.prompt, processor.config.provider.prompt,
                        processor.config.provider.endpoint):
            if private:
                assert private.encode() not in payload


def test_same_identity_uses_only_original_audit(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        assert processor.process_path(source, retry_input_rejection=True).status == JobStatus.SUCCEEDED
        assert transitions(config) == []
        assert (config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json").is_file()


@pytest.mark.parametrize("attempt_state", list(AttemptState))
def test_explicit_cannot_reopen_any_historical_attempt(tmp_path, quiet_logger, monkeypatch, attempt_state):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.state.connection.execute(
            "INSERT INTO attempts (job_id,attempt_no,state,started_at) VALUES (?,1,?,?)",
            (job.job_id, attempt_state.value, job.created_at),
        )
        before = tuple(processor.state.connection.execute("SELECT * FROM attempts").fetchone())
        processor.config = changed_prompt(config)
        assert retry_admit(processor, source).status == JobStatus.FAILED
        assert processor.state.get_job(job.job_id) == job
        assert tuple(processor.state.connection.execute("SELECT * FROM attempts").fetchone()) == before
        assert provider.requests == [] and transitions(config) == []
        assert not list(processor.staging.glob("*.input"))


@pytest.mark.parametrize("condition", [
    "invalid", "non_mpo", "non_jpeg_name", "byte_limit", "pixel_limit", "ambiguous",
    "provider_failure", "temp_reference", "output_reference", "output_hash",
    "temp_artifact", "output_artifact", "old_stage",
])
def test_explicit_does_not_bypass_eligibility(tmp_path, quiet_logger, monkeypatch, condition):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / ("synthetic.png" if condition == "non_jpeg_name" else "synthetic.JPG"))
    if condition == "invalid":
        source.write_bytes(b"synthetic invalid")
    elif condition == "non_mpo":
        write_image(source, "JPEG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        if condition == "byte_limit":
            processor.config = replace(processor.config, limits=replace(config.limits, max_input_bytes=1))
        elif condition == "pixel_limit":
            processor.config = replace(processor.config, limits=replace(config.limits, max_pixels=1))
        elif condition in {"ambiguous", "provider_failure", "temp_reference", "output_reference", "output_hash"}:
            field, value = {
                "ambiguous": ("status", "AMBIGUOUS"),
                "provider_failure": ("error_code", "PROVIDER_PERMANENT"),
                "temp_reference": ("temp_name", ".owned.tmp"),
                "output_reference": ("output_name", "owned.png"),
                "output_hash": ("output_sha256", "a" * 64),
            }[condition]
            processor.state.connection.execute(f"UPDATE jobs SET {field}=? WHERE job_id=?", (value, job.job_id))
        artifact = None
        if condition == "old_stage":
            artifact = Path(job.staged_path)
        elif condition == "temp_artifact":
            artifact = config.paths.destination / ".unowned.tmp"
        elif condition == "output_artifact":
            artifact = config.paths.destination / initial_output_leaf(processor._snapshot_from_job(job), job.job_id)
        if artifact:
            artifact.write_bytes(b"unowned evidence")
        before = processor.state.get_job(job.job_id)
        assert retry_admit(processor, source).status in {JobStatus.FAILED, JobStatus.AMBIGUOUS}
        assert processor.state.get_job(job.job_id) == before
        assert provider.requests == [] and transitions(config) == []
        assert not list(config.paths.quarantine.glob("*.mpo-input-failure.json"))
        if artifact:
            assert artifact.read_bytes() == b"unowned evidence"
        if condition != "old_stage":
            assert not list(processor.staging.glob("*.input"))


@pytest.mark.parametrize("condition", ["malformed", "source_hash", "attempts", "stage", "hardlink", "original_collision"])
def test_explicit_bad_provenance_preserves_failure(tmp_path, quiet_logger, monkeypatch, condition):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        failure = config.paths.quarantine / f"{job.job_id}.json"
        audit = config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json"
        if condition == "malformed":
            failure.write_bytes(b"not json")
        elif condition == "hardlink":
            os.link(failure, tmp_path / "unowned.json")
        elif condition == "original_collision":
            audit.write_bytes(b"unowned audit")
        else:
            data = json.loads(failure.read_bytes())
            if condition == "source_hash":
                data["source_version"]["sha256"] = "0" * 64
            else:
                data[condition] = {"attempts": 1, "stage": "provider"}[condition]
            failure.write_text(json.dumps(data))
        evidence = failure.read_bytes()
        with pytest.raises(AppError):
            retry_admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert failure.read_bytes() == evidence
        assert not list(processor.staging.glob("*.input"))
        assert transitions(config) == []
        if condition == "original_collision":
            assert audit.read_bytes() == b"unowned audit"


@pytest.mark.parametrize("point", ["mpo_requeue_before_update", "mpo_requeue_after_update", "mpo_requeue_after_commit"])
@pytest.mark.parametrize("exception_type", [Crash, RuntimeError])
def test_explicit_crash_boundaries_require_flag_until_commit(
    tmp_path, quiet_logger, monkeypatch, point, exception_type,
):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    current_config = changed_prompt(config)
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = current_config
        identity = processor._request_identity()

        def crash(where):
            if where == point:
                raise exception_type()

        processor.state._transition_hook = crash
        with pytest.raises(exception_type):
            retry_admit(processor, source)
        committed = point.endswith("after_commit")
        current = processor.state.get_job(job.job_id)
        assert current.status == (JobStatus.READY if committed else JobStatus.FAILED)
        assert current.prompt_hash == (identity.prompt_hash if committed else job.prompt_hash)
        assert Path(current.staged_path).exists() == committed
        assert len(list(processor.staging.glob("*.input"))) == int(committed)
        audit, = transitions(config)
        evidence = audit.read_bytes()
        assert provider.requests == []
    with make_processor(current_config, quiet_logger, provider) as processor:
        result = processor.process_path(source)
        if not committed:
            assert result.status == JobStatus.FAILED
            assert processor.state.get_job(job.job_id) == job
            assert provider.requests == []
            result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.SUCCEEDED and result.job_id == job.job_id
        assert result.attempts == len(provider.requests) == 1
        assert transitions(config) == [audit] and audit.read_bytes() == evidence


def test_rollback_then_different_settings_keeps_distinct_transition_evidence(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config, "synthetic B")

        def crash(point):
            if point == "mpo_requeue_after_update":
                raise Crash()

        processor.state._transition_hook = crash
        with pytest.raises(Crash):
            retry_admit(processor, source)
        first, = transitions(config)
        evidence = first.read_bytes()
        processor.state._transition_hook = None
        processor.config = changed_prompt(config, "synthetic C")
        assert processor.process_path(source).status == JobStatus.FAILED
        assert processor.process_path(source, retry_input_rejection=True).status == JobStatus.SUCCEEDED
        assert len(transitions(config)) == 2
        assert first.read_bytes() == evidence
        data = [json.loads(path.read_bytes()) for path in transitions(config)]
        assert {record["old_request_sha256"] for record in data} == {identity_fingerprint(job)}
        assert len({record["new_request_sha256"] for record in data}) == 2


@pytest.mark.parametrize("kind", ["different", "directory", "hardlink", "reparse"])
def test_transition_collision_fails_closed_without_replace(tmp_path, quiet_logger, monkeypatch, kind):
    _, config = write_config(tmp_path)
    # Keep the synthetic directory collision below Windows' legacy mkdir path limit.
    config = replace(config, paths=replace(config.paths, quarantine=tmp_path / "q"))
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        evidence = (config.paths.quarantine / f"{job.job_id}.json").read_bytes()
        audit = quarantine.preserve_mpo_request_transition_audit(
            job, config.paths.quarantine, processor._request_identity(), evidence,
        )
        original = audit.read_bytes()
        if kind == "different":
            audit.write_bytes(b"unowned transition")
        elif kind == "directory":
            audit.unlink()
            audit.mkdir()
        elif kind == "hardlink":
            os.link(audit, tmp_path / "unowned-transition.json")
        else:
            from tcfcomic import path_safety
            real = path_safety.is_reparse_or_link
            monkeypatch.setattr(path_safety, "is_reparse_or_link", lambda p: p == audit or real(p))
        with pytest.raises(AppError):
            retry_admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert not list(processor.staging.glob("*.input"))
        if kind == "directory":
            assert audit.is_dir()
        else:
            assert audit.read_bytes() == (b"unowned transition" if kind == "different" else original)


@pytest.mark.parametrize("mutation", [
    "identity", "version", "attempt", "claim", "artifact", "source", "provenance", "original_audit", "transition_audit",
])
def test_explicit_cas_rechecks_all_evidence(tmp_path, quiet_logger, monkeypatch, mutation):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        real_cas = processor.state.requeue_never_sent_mpo

        def race(expected, snapshot, identity, check_evidence, **kwargs):
            assert kwargs == {"retry_input_rejection": True}
            if mutation == "attempt":
                processor.state.connection.execute(
                    "INSERT INTO attempts (job_id,attempt_no,state,started_at) VALUES (?,1,'dispatching',?)",
                    (job.job_id, job.created_at),
                )
            elif mutation in {"identity", "version", "claim"}:
                field, value = {
                    "identity": ("model", "raced-model"), "version": ("sha256", "0" * 64),
                    "claim": ("status", "DISPATCHING"),
                }[mutation]
                processor.state.connection.execute(f"UPDATE jobs SET {field}=? WHERE job_id=?", (value, job.job_id))
            elif mutation == "artifact":
                (config.paths.destination / ".unowned.tmp").write_bytes(b"unowned")
            elif mutation == "source":
                before = source.stat()
                payload = bytearray(source.read_bytes())
                payload[-1] ^= 1
                source.write_bytes(payload)
                os.utime(source, ns=(before.st_atime_ns, before.st_mtime_ns))
            else:
                path = {
                    "provenance": config.paths.quarantine / f"{job.job_id}.json",
                    "original_audit": config.paths.quarantine / f"{job.job_id}.mpo-input-failure.json",
                    "transition_audit": transitions(config)[0],
                }[mutation]
                path.write_bytes(b"unowned raced evidence")

            def transaction_check():
                assert processor.state.connection.in_transaction
                return check_evidence()

            return real_cas(expected, snapshot, identity, transaction_check, **kwargs)

        monkeypatch.setattr(processor.state, "requeue_never_sent_mpo", race)
        if mutation in {"source", "provenance", "original_audit", "transition_audit"}:
            with pytest.raises(AppError):
                retry_admit(processor, source)
        else:
            assert retry_admit(processor, source).status != JobStatus.READY
        current = processor.state.get_job(job.job_id)
        assert current.prompt_hash == job.prompt_hash
        assert current.staged_path == job.staged_path
        assert not list(processor.staging.glob("*.input"))
        assert processor.state.attempt_count(job.job_id) == int(mutation == "attempt")
        if mutation == "artifact":
            assert (config.paths.destination / ".unowned.tmp").read_bytes() == b"unowned"


def test_pending_work_keeps_dispatch_identity_guard_even_with_flag(tmp_path, quiet_logger):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        queued = admit(processor, source)
        original = processor.state.get_job(queued.job_id)
        processor.config = changed_prompt(config)
        assert retry_admit(processor, source).status == JobStatus.READY
        assert processor.state.get_job(queued.job_id) == original
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.FAILED and result.error_code == ErrorCode.STATE_FAILED
        assert provider.requests == [] and transitions(config) == []


def test_current_settings_changed_after_adoption_still_block_dispatch(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config, "synthetic B")
        assert retry_admit(processor, source).status == JobStatus.READY
        processor.config = changed_prompt(config, "synthetic C")
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.FAILED and result.error_code == ErrorCode.STATE_FAILED
        assert processor.state.attempt_count(job.job_id) == 0 and provider.requests == []


def test_cli_input_retry_flag_is_explicit_for_process_and_watch():
    args = cli.build_parser().parse_args([
        "process", "--config", "synthetic.yaml", "--retry-input-rejection", "synthetic.JPG",
    ])
    assert args.retry_input_rejection is True
    assert cli.build_parser().parse_args([
        "process", "--config", "synthetic.yaml", "synthetic.JPG",
    ]).retry_input_rejection is False
    assert cli.build_parser().parse_args([
        "watch", "--config", "synthetic.yaml", "--retry-input-rejection",
    ]).retry_input_rejection is True
    assert cli.build_parser().parse_args([
        "watch", "--config", "synthetic.yaml",
    ]).retry_input_rejection is False


@pytest.mark.parametrize("console", ["json", "text"])
def test_cli_retry_stdout_exit_codes_and_redacted_diagnostic(tmp_path, quiet_logger, monkeypatch, capsys, console):
    path, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
    current = replace(changed_prompt(config), logging=replace(config.logging, console_format=console))

    class OfflineProcessor(Processor):
        def __init__(self, cfg, logger, **kwargs):
            super().__init__(cfg, logger, runner=InProcessAttemptRunner(provider), **kwargs)

    monkeypatch.setattr(cli, "load_config", lambda _, **kwargs: current)
    monkeypatch.setattr(cli, "Processor", OfflineProcessor)
    argv = ["process", "--config", str(path), str(source)]
    try:
        assert cli.main(argv) == 4
        captured = capsys.readouterr()
        assert captured.out == ""
        assert "Current request settings differ" in captured.err
        if console == "json":
            assert "INVALID_IMAGE" in captured.err
        assert "--retry-input-rejection" in captured.err
        assert "sk-synthetic-secret" not in captured.err
        assert str(source) not in captured.err
        assert provider.requests == []
        assert cli.main([*argv, "--retry-input-rejection"]) == 0
        captured = capsys.readouterr()
        assert len(captured.out.splitlines()) == 1
        assert Path(captured.out.strip()).is_file()
        assert provider.requests[0].job_id == job.job_id and len(provider.requests) == 1
        assert cli.main([*argv, "--retry-input-rejection"]) == 0
        assert len(provider.requests) == 1
    finally:
        logger = logging.getLogger("tcfcomic")
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_diagnostic_never_interpolates_control_names_or_request_secrets(tmp_path, quiet_logger, monkeypatch, capsys):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    logger = configure_logging(config.logging, config.paths.destination)
    try:
        with make_processor(config, logger) as processor:
            job = historical_failure(processor, source, monkeypatch)
            malicious = "forged\nline\x1b[2J; token=synthetic-secret.JPG"
            processor.state.connection.execute("UPDATE jobs SET source_name=? WHERE job_id=?", (malicious, job.job_id))
            processor.config = changed_prompt(config, "private prompt and endpoint")
            capsys.readouterr()
            with redact_secret("synthetic-secret"):
                assert processor.process_path(source).error_code == ErrorCode.INVALID_IMAGE
            captured = capsys.readouterr()
            records = [json.loads(line) for line in captured.err.splitlines()]
            record, = [r for r in records if r["event"] == "input_rejection_settings_mismatch"]
            assert captured.out == ""
            assert "\x1b" not in captured.err and "synthetic-secret" not in captured.err
            assert "private prompt" not in captured.err and "forged" not in record["message"]
            assert '--config "<config-file>"' in record["message"]
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_cas_stage_ownership_race_never_deletes_other_jobs_stage(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        other_source = write_image(config.paths.source / "other.png")
        other = admit(processor, other_source)
        processor.config = changed_prompt(config)
        real_cas = processor.state.requeue_never_sent_mpo
        raced_path = None

        def race(expected, snapshot, identity, check, **kwargs):
            nonlocal raced_path
            raced_path = snapshot.staged_path
            processor.state.connection.execute(
                "UPDATE jobs SET staged_path=? WHERE job_id=?", (str(raced_path), other.job_id),
            )
            return real_cas(expected, snapshot, identity, check, **kwargs)

        monkeypatch.setattr(processor.state, "requeue_never_sent_mpo", race)
        with pytest.raises(AppError, match="ownership"):
            retry_admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert raced_path.read_bytes() == source.read_bytes()


@pytest.mark.parametrize("field", ["normalized_path", "size", "mtime_ns", "sha256", "path"])
def test_explicit_state_cas_requires_full_source_binding(tmp_path, quiet_logger, monkeypatch, field):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        info = source.stat()
        snapshot = stage_candidate(
            StableCandidate(source, info.st_size, info.st_mtime_ns),
            processor.staging / ("c" * 32 + ".input"), config.limits.max_input_bytes,
            ShutdownToken(lambda: False),
        )
        value = {
            "normalized_path": str(source) + "other", "size": snapshot.size + 1,
            "mtime_ns": snapshot.mtime_ns + 1, "sha256": "0" * 64, "path": source.with_name("other.JPG"),
        }[field]
        assert not processor.state.requeue_never_sent_mpo(
            job, replace(snapshot, **{field: value}), processor._request_identity(),
            lambda: pytest.fail("Evidence must not override the source binding"),
            retry_input_rejection=True,
        )
        assert processor.state.get_job(job.job_id) == job


@pytest.mark.parametrize("kind", ["new", "succeeded"])
def test_flag_does_not_force_new_job_or_reprocess_success(tmp_path, quiet_logger, kind):
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "ordinary.png")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        original = processor.process_path(source) if kind == "succeeded" else None
        processor.config = changed_prompt(config)
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1
        assert len(processor.state.list_jobs()) == 1
        assert transitions(config) == []
        if original:
            assert result == original
        else:
            assert provider.requests[0].prompt == processor.config.provider.prompt


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("metadata", [False, True])
def test_explicit_retry_still_uploads_only_metadata_free_primary(
    tmp_path, quiet_logger, monkeypatch, azure, metadata,
):
    _, config = write_config(tmp_path)
    source = config.paths.source / "synthetic.JPG"
    if metadata:
        write_mpo(source)
    else:
        with Image.new("RGB", (24, 18), "red") as primary, Image.new("RGB", (24, 18), "blue") as other:
            primary.save(source, format="MPO", save_all=True, append_images=[other])
    original = source.read_bytes()

    class NormalizingFake(FakeProvider):
        def transform(self, request, output):
            with ExitStack() as stack:
                name, stream, mime = _prepare_upload(request, stack, azure=azure)
                assert (name, mime) == ("input.png", "image/png")
                payload = stream.read()
            with Image.open(io.BytesIO(payload)) as image:
                assert image.format == "PNG" and image.n_frames == 1
                assert image.size == ((18, 24) if metadata else (24, 18))
                assert image.info == {} and not image.getexif()
                assert image.getpixel((0, 0))[0] > 240
            assert b"private" not in payload
            return super().transform(request, output)

    provider = NormalizingFake()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = changed_prompt(config)
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.SUCCEEDED and result.job_id == job.job_id
        assert len(provider.requests) == 1
        assert source.read_bytes() == original


@pytest.mark.parametrize("failure", ["temp_collision", "link_collision", "crash_after_link"])
def test_transition_publication_collision_and_crash_safety(tmp_path, quiet_logger, monkeypatch, failure):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    provider = FakeProvider()
    current = changed_prompt(config)
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        provenance = (config.paths.quarantine / f"{job.job_id}.json").read_bytes()
        quarantine.preserve_mpo_failure_audit(job, config.paths.quarantine, provenance)
        processor.config = current
        with monkeypatch.context() as patch:
            if failure == "temp_collision":
                temp = config.paths.quarantine / f"{job.job_id}.{'f' * 32}.json.tmp"
                temp.write_bytes(b"unowned temp")
                patch.setattr(quarantine.uuid, "uuid4", lambda: SimpleNamespace(hex="f" * 32))
            elif failure == "link_collision":
                real_link = quarantine.os.link

                def collide(src, dst):
                    Path(dst).write_bytes(b"unowned transition")
                    real_link(src, dst)

                patch.setattr(quarantine.os, "link", collide)
            else:
                def crash(*args):
                    raise Crash()

                patch.setattr(quarantine, "_remove_owned_temp", crash)
            with pytest.raises(Crash if failure == "crash_after_link" else AppError):
                retry_admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert not list(processor.staging.glob("*.input"))
        assert provider.requests == []
        if failure == "temp_collision":
            assert temp.read_bytes() == b"unowned temp"
        elif failure == "link_collision":
            audit, = transitions(config)
            assert audit.read_bytes() == b"unowned transition"
            assert not list(config.paths.quarantine.glob("*.json.tmp"))
        else:
            audit, = transitions(config)
            assert audit.stat().st_nlink == 2
            evidence = audit.read_bytes()
    if failure == "crash_after_link":
        with make_processor(current, quiet_logger, provider) as processor:
            assert audit.stat().st_nlink == 1
            assert processor.process_path(source).status == JobStatus.FAILED
            assert provider.requests == []
            assert processor.process_path(source, retry_input_rejection=True).status == JobStatus.SUCCEEDED
            assert len(provider.requests) == 1
            assert transitions(config) == [audit] and audit.read_bytes() == evidence


@pytest.mark.parametrize("cancel_at", [None, "acquire", "validate"])
def test_explicit_adoption_keeps_token_before_claim_guards(tmp_path, quiet_logger, monkeypatch, cancel_at):
    _, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    source = write_mpo(config.paths.source / "synthetic.JPG")
    events = []
    shutdown = threading.Event()
    provider = FakeProvider()

    class Session(OfflineSession):
        def acquire(self, *args):
            check("acquire")
            return self

        def require_valid(self, *args):
            check("validate")

    class Runner(OfflineAuthenticatedRunner):
        def run_authenticated(self, *args):
            events.append("run")
            return super().run_authenticated(*args)

    def check(phase):
        events.append(phase)
        assert processor.state.get_job(job.job_id).status == JobStatus.READY
        assert processor.state.attempt_count(job.job_id) == 0
        if phase == cancel_at:
            shutdown.set()

    with Processor(config, quiet_logger, runner=Runner(provider), auth_session=Session()) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.config = replace(config, provider=replace(config.provider, authentication="interactive"))
        assert retry_admit(processor, source).status == JobStatus.READY
        real_claim = processor.state.claim_ready

        def claim(*args):
            events.append("claim")
            return real_claim(*args)

        monkeypatch.setattr(processor.state, "claim_ready", claim)
        if cancel_at:
            with pytest.raises(AppError) as caught:
                processor._dispatch_one(shutdown, job.job_id)
            assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
            assert events == ["acquire", "validate"]
            assert processor.state.get_job(job.job_id).status == JobStatus.READY
            assert processor.state.attempt_count(job.job_id) == 0
            assert provider.requests == []
        else:
            assert processor._dispatch_one(shutdown, job.job_id)
            assert events == ["acquire", "validate", "claim", "run"]
            assert processor.state.get_job(job.job_id).status == JobStatus.SUCCEEDED
            assert len(provider.requests) == 1


def test_explicit_adoption_cannot_bypass_provider_rate_cooldown(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    config = replace(config, provider=replace(config.provider, requests_per_minute=2))
    timer = ManualTime()
    provider = TimedProvider(timer)
    source = write_mpo(config.paths.source / "synthetic.JPG")
    with make_processor(config, quiet_logger, provider, clock=timer.clock()) as processor:
        job = historical_failure(processor, source, monkeypatch)
        other = write_image(config.paths.source / "other.png")
        assert processor.process_path(other).status == JobStatus.SUCCEEDED
        processor.config = changed_prompt(config)

        def while_waiting(event, seconds):
            assert processor.state.get_job(job.job_id).status == JobStatus.READY
            assert processor.state.attempt_count(job.job_id) == 0

        timer.on_wait = while_waiting
        result = processor.process_path(source, retry_input_rejection=True)
        assert result.status == JobStatus.SUCCEEDED
        assert provider.starts == [0, 30]
        assert result.attempts == 1 and result.job_id == job.job_id


def test_cli_flag_keeps_interactive_startup_before_idempotent_success(
    tmp_path, quiet_logger, monkeypatch, capsys,
):
    path, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    config = replace(config, provider=replace(config.provider, authentication="interactive"))
    source = write_image(config.paths.source / "synthetic.png")
    provider = FakeProvider()
    events = []
    with Processor(
        config, quiet_logger, runner=OfflineAuthenticatedRunner(provider), auth_session=OfflineSession(),
    ) as processor:
        original = processor.process_path(source)

    class Broker(OfflineSession):
        started = False

        def __init__(self, *args):
            pass

        def __enter__(self):
            return self

        def __exit__(self, *args):
            events.append("close")

        def start(self, *args):
            events.append("sign-in")
            self.started = True

    class OfflineProcessor(Processor):
        def __init__(self, cfg, logger, **kwargs):
            assert events == ["sign-in"]
            events.append("processor")
            super().__init__(cfg, logger, runner=OfflineAuthenticatedRunner(provider), **kwargs)

    monkeypatch.setattr(cli, "load_config", lambda _, **kwargs: config)
    monkeypatch.setattr(cli, "AuthenticationBroker", Broker)
    monkeypatch.setattr(cli, "Processor", OfflineProcessor)
    monkeypatch.setattr(cli, "configure_logging", lambda *args: quiet_logger)
    assert cli.main(["process", "--config", str(path), "--retry-input-rejection", str(source)]) == 0
    assert events == ["sign-in", "processor", "close"]
    captured = capsys.readouterr()
    assert captured.out.strip() == str(original.output_path)
    assert len(provider.requests) == 1
