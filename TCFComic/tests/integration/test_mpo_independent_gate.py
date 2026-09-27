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

import pytest
import yaml
from PIL import Image

from conftest import write_config, write_image, write_mpo
from test_mpo_recovery import admit, historical_failure, make_processor
from tcfcomic.domain import (
    AppError, AttemptState, ErrorCode, JobStatus, PermanentProviderError,
    ShutdownToken, SourceSnapshot, StableCandidate, TransformRequest,
)
from tcfcomic.logging_setup import configure_logging, log_event
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.openai import _prepare_upload
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.redaction import redact_secret
from tcfcomic.scanner import stage_candidate


def request_for(source, staged):
    data = source.read_bytes()
    staged.write_bytes(data)
    info = source.stat()
    return TransformRequest(
        "a" * 32,
        SourceSnapshot(source, str(source), len(data), info.st_mtime_ns,
                       hashlib.sha256(data).hexdigest(), staged),
        "synthetic prompt", "offline",
    )


def upload_bytes(request, azure):
    with ExitStack() as stack:
        name, stream, mime = _prepare_upload(request, stack, azure=azure)
        assert (name, mime) == ("input.png", "image/png")
        return stream.read()


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("orientation", range(1, 9))
def test_asymmetric_primary_all_orientations_strip_icc_exif_xmp(tmp_path, azure, orientation):
    source = tmp_path / "asymmetric.JpEg"
    pixels = bytes(component for y in range(18) for x in range(24)
                   for component in (x * 10, y * 14, (x + y) * 6))
    exif = Image.Exif()
    exif[274] = orientation
    exif[270] = "private EXIF description"
    exif[34853] = {1: "N", 2: (1.0, 2.0, 3.0)}
    with Image.frombytes("RGB", (24, 18), pixels) as primary, Image.new("RGB", (24, 18), "blue") as other:
        primary.save(
            source, format="MPO", save_all=True, append_images=[other], exif=exif,
            icc_profile=b"private ICC profile", xmp=b"private XMP", comment=b"private comment",
        )
    operations = {
        2: Image.Transpose.FLIP_LEFT_RIGHT, 3: Image.Transpose.ROTATE_180,
        4: Image.Transpose.FLIP_TOP_BOTTOM, 5: Image.Transpose.TRANSPOSE,
        6: Image.Transpose.ROTATE_270, 7: Image.Transpose.TRANSVERSE,
        8: Image.Transpose.ROTATE_90,
    }
    with Image.open(source) as decoded:
        assert decoded.n_frames == 2 and decoded.info["icc_profile"]
        decoded.seek(0)
        with (decoded.transpose(operations[orientation]) if orientation != 1 else decoded.copy()) as expected:
            expected_pixels, expected_size = expected.tobytes(), expected.size
    request = request_for(source, tmp_path / "staged.input")
    original = source.read_bytes()
    result = upload_bytes(request, azure)
    with Image.open(io.BytesIO(result)) as output:
        assert output.mode == "RGB"
        assert output.size == expected_size
        assert output.tobytes() == expected_pixels
        assert output.info == {} and not output.getexif()
    assert b"private" not in result
    assert source.read_bytes() == request.source.staged_path.read_bytes() == original


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("secondary", ["oversized", "truncated"])
def test_secondary_frame_is_neither_decoded_nor_subject_to_primary_limits(tmp_path, azure, secondary):
    source = tmp_path / "primary.jpg"
    with Image.new("RGB", (24, 18), "red") as primary, Image.new("RGB", (1200, 1001), "blue") as other:
        primary.save(source, format="MPO", save_all=True, append_images=[other])
    if secondary == "truncated":
        data = source.read_bytes()
        second_start = data.index(b"\xff\xd8", 2)
        source.write_bytes(data[:second_start + 4])
    else:
        with Image.open(source) as decoded:
            decoded.seek(1)
            assert decoded.size == (1200, 1001)
    request = replace(request_for(source, tmp_path / "staged.input"),
                      max_width=24, max_height=18, max_pixels=432)
    result = upload_bytes(request, azure)
    with Image.open(io.BytesIO(result)) as output:
        assert output.size == (24, 18) and output.getpixel((0, 0))[0] > 240


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("delta", [0, -1])
def test_converted_png_exact_byte_boundary(tmp_path, azure, delta):
    source = write_mpo(tmp_path / "noisy.jpg", noisy=True)
    request = request_for(source, tmp_path / "staged.input")
    expected = upload_bytes(request, azure)
    assert len(expected) > source.stat().st_size
    bounded = replace(request, max_input_bytes=len(expected) + delta)
    if delta == 0:
        assert upload_bytes(bounded, azure) == expected
    else:
        with pytest.raises(PermanentProviderError) as caught:
            upload_bytes(bounded, azure)
        assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED


@pytest.mark.parametrize("azure", [False, True])
def test_primary_exact_raw_and_oriented_dimensions_and_source_bytes(tmp_path, azure):
    source = write_mpo(tmp_path / "boundary.jpg")
    request = replace(
        request_for(source, tmp_path / "staged.input"), max_width=24,
        max_height=24, max_pixels=432, max_input_bytes=source.stat().st_size,
    )
    with Image.open(io.BytesIO(upload_bytes(request, azure))) as output:
        assert output.size == (18, 24)


@pytest.mark.parametrize("attempt_state", list(AttemptState))
def test_every_attempt_row_state_blocks_recovery(tmp_path, quiet_logger, monkeypatch, attempt_state):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.jpg")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.state.connection.execute(
            "INSERT INTO attempts (job_id,attempt_no,state,started_at) VALUES (?,1,?,?)",
            (job.job_id, attempt_state.value, job.created_at),
        )
        assert admit(processor, source).status == JobStatus.FAILED
        assert processor.state.get_job(job.job_id) == job
        assert processor.state.attempt_count(job.job_id) == 1
        assert not list(processor.staging.glob("*.input"))
        assert not list(config.paths.quarantine.glob("*.mpo-input-failure.json"))


@pytest.mark.parametrize("field", [
    "name", "model", "prompt", "endpoint", "authentication", "tenant_id", "client_id", "redirect_uri",
])
def test_all_request_identity_changes_block_recovery(tmp_path, quiet_logger, monkeypatch, field):
    _, config = write_config(tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com")
    if field in {"tenant_id", "client_id", "redirect_uri"}:
        # Admission alone never acquires credentials; use an injected inert session.
        config = replace(config, provider=replace(config.provider, authentication="interactive"))
    source = write_mpo(config.paths.source / "synthetic.jpg")

    class InertSession:
        started = True

    class NoAuthRunner(InProcessAttemptRunner):
        def run_authenticated(self, *args):
            pytest.fail("Recovery guard must not dispatch")

    with Processor(config, quiet_logger, runner=NoAuthRunner(FakeProvider()),
                   auth_session=InertSession()) as processor:
        job = historical_failure(processor, source, monkeypatch)
        values = {
            "name": "openai", "model": "different-model", "prompt": "different prompt",
            "endpoint": "https://other-offline.openai.azure.com", "authentication": "interactive",
            "tenant_id": "11111111-1111-1111-1111-111111111111",
            "client_id": "22222222-2222-2222-2222-222222222222",
            "redirect_uri": "http://localhost:8888",
        }
        processor.config = replace(config, provider=replace(config.provider, **{field: values[field]}))
        assert admit(processor, source).status == JobStatus.FAILED
        assert processor.state.get_job(job.job_id) == job
        assert processor.state.attempt_count(job.job_id) == 0
        assert not list(config.paths.quarantine.glob("*.mpo-input-failure.json"))


@pytest.mark.parametrize("status", [status for status in JobStatus if status != JobStatus.FAILED])
def test_transaction_refuses_every_nonfailed_status(tmp_path, quiet_logger, monkeypatch, status):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.jpg")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        processor.state.connection.execute("UPDATE jobs SET status=? WHERE job_id=?", (status.value, job.job_id))
        current = processor.state.get_job(job.job_id)
        info = source.stat()
        snapshot = stage_candidate(
            StableCandidate(source, info.st_size, info.st_mtime_ns),
            processor.staging / ("d" * 32 + ".input"), config.limits.max_input_bytes,
            ShutdownToken(lambda: False),
        )
        evidence_calls = []
        assert not processor.state.requeue_never_sent_mpo(
            current, snapshot, processor._request_identity(), lambda: evidence_calls.append(True) or True,
        )
        assert processor.state.get_job(job.job_id) == current
        assert evidence_calls == []


@pytest.mark.parametrize("field,value", [
    ("schema_version", True), ("schema_version", 1.0), ("attempts", False),
    ("attempts", 0.0), ("size", 1.0), ("mtime_ns", 1.0),
    ("source_version", []), ("safe_message", None), ("failed_at", None),
])
def test_provenance_types_cannot_coerce_to_valid_evidence(tmp_path, quiet_logger, monkeypatch, field, value):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.jpg")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        failure = config.paths.quarantine / f"{job.job_id}.json"
        data = json.loads(failure.read_bytes())
        if field in {"size", "mtime_ns"}:
            data["source_version"][field] = float(data["source_version"][field])
        else:
            data[field] = value
        failure.write_text(json.dumps(data), encoding="utf-8")
        before = failure.read_bytes()
        with pytest.raises(AppError):
            admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert failure.read_bytes() == before
        assert processor.state.attempt_count(job.job_id) == 0
        assert not list(processor.staging.glob("*.input"))


@pytest.mark.parametrize("kind", ["missing", "hardlink"])
def test_missing_or_hardlinked_provenance_blocks_recovery(tmp_path, quiet_logger, monkeypatch, kind):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.jpg")
    with make_processor(config, quiet_logger) as processor:
        job = historical_failure(processor, source, monkeypatch)
        failure = config.paths.quarantine / f"{job.job_id}.json"
        if kind == "missing":
            failure.unlink()
        else:
            os.link(failure, tmp_path / "other-evidence.json")
        with pytest.raises(AppError):
            admit(processor, source)
        assert processor.state.get_job(job.job_id) == job
        assert not list(processor.staging.glob("*.input"))


def test_repeated_readmission_before_dispatch_keeps_exactly_one_stage_and_attempt(tmp_path, quiet_logger, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / "synthetic.jpg")
    provider = FakeProvider()
    with make_processor(config, quiet_logger, provider) as processor:
        job = historical_failure(processor, source, monkeypatch)
        assert admit(processor, source).status == JobStatus.READY
        ready = processor.state.get_job(job.job_id)
        for _ in range(3):
            assert admit(processor, source).job_id == job.job_id
            assert processor.state.get_job(job.job_id) == ready
            assert list(processor.staging.glob("*.input")) == [Path(ready.staged_path)]
            assert processor.state.attempt_count(job.job_id) == 0
        assert processor._run_until_terminal(job.job_id, threading.Event()).status == JobStatus.SUCCEEDED
        for _ in range(3):
            assert admit(processor, source).status == JobStatus.SUCCEEDED
        assert len(provider.requests) == processor.state.attempt_count(job.job_id) == 1


@pytest.mark.parametrize("console", ["json", "text"])
def test_console_stderr_only_and_both_sinks_redact_control_payload_token(tmp_path, capsys, monkeypatch, console):
    _, config = write_config(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "synthetic-api-key")
    logger = configure_logging(replace(config.logging, console_format=console), config.paths.destination)
    payload = "YQ" * 100
    try:
        with redact_secret("synthetic-bearer-value"):
            log_event(
                logger, logging.INFO, "watch_heartbeat",
                message=f"Processing synthetic-api-key synthetic-bearer-value {payload}\nforged\tline\x1b[2J",
                source_name="synthetic-api-key.JPG", prompt="PRIVATE PROMPT", headers="PRIVATE HEADERS",
            )
        for handler in logger.handlers:
            handler.flush()
        captured = capsys.readouterr()
        assert captured.out == "" and len(captured.err.splitlines()) == 1
        file_text = (config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl").read_text()
        for text in (captured.err, file_text):
            assert not any(secret in text for secret in (
                "synthetic-api-key", "synthetic-bearer-value", payload, "PRIVATE PROMPT", "PRIVATE HEADERS",
            ))
            assert "\x1b" not in text and "\t" not in text
        record = json.loads(file_text)
        assert record["event"] == "watch_heartbeat" and "[REDACTED]" in record["message"]
        if console == "json":
            assert json.loads(captured.err)["message"] == record["message"]
        else:
            assert " INFO " in captured.err and record["message"] in captured.err
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


@pytest.mark.parametrize("cancel_at", [None, "acquire", "validate"])
def test_token_acquisition_and_validation_precede_claim_with_cancel(tmp_path, quiet_logger, monkeypatch, cancel_at):
    _, config = write_config(tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com")
    config = replace(config, provider=replace(config.provider, authentication="interactive"))
    events = []
    shutdown = threading.Event()

    class Session:
        started = True

        def acquire(self, *args):
            check("acquire")
            return self

        def require_valid(self, *args):
            check("validate")

    class Runner(InProcessAttemptRunner):
        def run_authenticated(self, provider, request, temp_path, deadline, token):
            events.append("run")
            assert processor.state.attempt_count(job.job_id) == 1
            return self.run(provider, request, temp_path, deadline)

    def check(phase):
        events.append(phase)
        assert processor.state.get_job(job.job_id).status == JobStatus.READY
        assert processor.state.attempt_count(job.job_id) == 0
        if cancel_at == phase:
            shutdown.set()

    with Processor(config, quiet_logger, runner=Runner(FakeProvider()), auth_session=Session()) as processor:
        job = admit(processor, write_image(config.paths.source / "synthetic.png"))
        original_claim = processor.state.claim_ready

        def claim(*args):
            events.append("claim")
            return original_claim(*args)

        monkeypatch.setattr(processor.state, "claim_ready", claim)
        if cancel_at is None:
            assert processor._dispatch_one(shutdown, job.job_id)
            assert events == ["acquire", "validate", "claim", "run"]
        else:
            with pytest.raises(AppError) as caught:
                processor._dispatch_one(shutdown, job.job_id)
            assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
            assert events == ["acquire", "validate"]
            assert processor.state.get_job(job.job_id).status == JobStatus.READY
            assert processor.state.attempt_count(job.job_id) == 0


@pytest.mark.parametrize("console", ["json", "text"])
@pytest.mark.parametrize("command", ["validate", "process", "watch"])
def test_cli_stdout_contract_and_visible_watch_input_failure(tmp_path, capsys, monkeypatch, console, command):
    import tcfcomic.cli as cli

    path, config = write_config(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["logging"]["console_format"] = console
    path.write_text(yaml.safe_dump(data))
    source = write_mpo(config.paths.source / "synthetic.JPG")
    if command == "watch":
        source.write_bytes(b"synthetic invalid input")

    class OfflineProcessor(Processor):
        def __init__(self, cfg, logger, **kwargs):
            super().__init__(cfg, logger, runner=InProcessAttemptRunner(FakeProvider()), **kwargs)

        def watch(self, **kwargs):
            return super().watch(max_cycles=4, **kwargs)

    monkeypatch.setattr(cli, "Processor", OfflineProcessor)
    argv = [command, "--config", str(path)]
    if command == "process":
        argv.append(str(source))
    try:
        assert cli.main(argv) == 0
        captured = capsys.readouterr()
        if command == "validate":
            assert json.loads(captured.out)["provider"] == "fake"
            assert captured.err == ""
            return
        if command == "process":
            assert len(captured.out.splitlines()) == 1
            assert Path(captured.out.strip()).is_file()
        else:
            assert captured.out == ""
        records = [json.loads(line) for line in (
            config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl"
        ).read_text().splitlines()]
        names = [record["event"] for record in records]
        if command == "watch":
            assert names.count("watch_started") == names.count("watch_stopped") == 1
            assert names.count("watch_item_failed") == names.count("source_detected") == 1
            assert "processing_started" not in names and "job_processed" not in names
            assert "INVALID_IMAGE" in captured.err
        else:
            assert names.count("job_queued") == names.count("processing_started") == names.count("job_processed") == 1
        if console == "json":
            assert [json.loads(line)["event"] for line in captured.err.splitlines()] == names
        else:
            assert all(not line.startswith("{") for line in captured.err.splitlines())
    finally:
        logger = logging.getLogger("tcfcomic")
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()
