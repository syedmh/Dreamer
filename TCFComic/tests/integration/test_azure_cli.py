from __future__ import annotations

import base64
import hashlib
import io
import json
import socket
import sqlite3
import threading
from dataclasses import replace
from datetime import UTC, datetime
from email import policy
from email.parser import BytesParser
from pathlib import Path
from unittest.mock import patch

import httpx
import openai
import pytest
import yaml
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.cli import main
from tcfcomic.domain import ErrorCode, JobStatus
from tcfcomic.processor import Processor
from tcfcomic.providers.worker import SubprocessAttemptRunner, _child_entry

ENDPOINT = "https://sweepertestai.openai.azure.com"
DEPLOYMENT = "tcfcomic-gpt-image-2"


def _azure_mock_child(config, request, temp_path, result_queue):
    """Test-only spawn target: real child factory and SDK, no network transport."""
    clients = []

    def dispatch(http):
        assert config.name == "azure_openai"
        assert request.max_input_bytes == 1_048_576
        assert request.source.staged_path.suffix == ".input"
        assert http.method == "POST"
        assert str(http.url) == ENDPOINT + "/openai/v1/images/edits?api-version=preview"
        assert http.headers["api-key"] == "offline-azure-key"
        assert "authorization" not in http.headers
        message = BytesParser(policy=policy.default).parsebytes(
            f"Content-Type: {http.headers['content-type']}\r\n\r\n".encode()
            + http.read()
        )
        parts = {
            part.get_param("name", header="content-disposition"): part
            for part in message.iter_parts()
        }
        assert parts["model"].get_payload(decode=True) == DEPLOYMENT.encode()
        uploaded = parts["image"].get_payload(decode=True)
        with Image.open(io.BytesIO(uploaded)) as image, io.BytesIO() as buffer:
            image.save(buffer, format="PNG")
            payload = buffer.getvalue()
        return httpx.Response(200, json={
            "id": "offline-azure-child",
            "data": [{"b64_json": base64.b64encode(payload).decode("ascii")}],
        })

    def client(**kwargs):
        assert kwargs == {"trust_env": False, "follow_redirects": False}
        result = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
        clients.append(result)
        return result

    def no_network(*args, **kwargs):
        raise AssertionError("Application network is forbidden in the Azure child")

    try:
        with (
            patch.object(openai, "DefaultHttpxClient", client),
            patch.object(socket.socket, "connect", no_network),
            patch.object(socket.socket, "connect_ex", no_network),
        ):
            _child_entry(config, request, temp_path, result_queue)
    finally:
        for client in clients:
            client.close()


def _azure_config(tmp_path):
    return write_config(
        tmp_path, provider="azure_openai", endpoint=ENDPOINT, model=DEPLOYMENT,
    )


def test_azure_cli_validate_without_key_and_process_missing_key(
    tmp_path, monkeypatch, capsys
):
    path, config = _azure_config(tmp_path)
    monkeypatch.setenv("OPENAI_API_KEY", "public-is-not-a-fallback")
    source = write_image(config.paths.source / "input.png")
    assert main(["validate", "--config", str(path)]) == 0
    summary = json.loads(capsys.readouterr().out)
    assert summary["provider"] == "azure_openai"
    assert summary["endpoint"] == ENDPOINT
    assert summary["model"] == DEPLOYMENT
    assert "prompt" not in summary
    assert main(["process", "--config", str(path), str(source)]) == 3
    assert "AZURE_OPENAI_API_KEY" in capsys.readouterr().err
    assert not (config.paths.destination / ".tcfcomic" / "state.db").exists()


@pytest.mark.parametrize("image_format", ["PNG", "JPEG", "WEBP"])
def test_offline_azure_cli_real_worker_publication_and_restart(
    tmp_path, monkeypatch, capsys, image_format
):
    import tcfcomic.cli as cli

    path, config = _azure_config(tmp_path)
    source = write_image(config.paths.source / f"input.{image_format.lower()}", image_format)
    original = source.read_bytes()
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", " offline-azure-key ")
    monkeypatch.setenv("OPENAI_API_KEY", "unused-public-key")
    runner = SubprocessAttemptRunner(target=_azure_mock_child)
    monkeypatch.setattr(
        cli, "Processor", lambda cfg, logger, **kwargs: Processor(cfg, logger, runner=runner, **kwargs)
    )
    assert cli.main(["process", "--config", str(path), str(source)]) == 0
    first = Path(capsys.readouterr().out.strip())
    assert first.is_file()
    with Image.open(first) as output, Image.open(source) as input_image:
        assert output.format == "PNG"
        assert output.size == input_image.size
        assert output.tobytes() == input_image.tobytes()
    assert cli.main(["process", "--config", str(path), str(source)]) == 0
    assert Path(capsys.readouterr().out.strip()) == first
    assert hashlib.sha256(source.read_bytes()).digest() == hashlib.sha256(original).digest()
    state = config.paths.destination / ".tcfcomic" / "state.db"
    with sqlite3.connect(state) as database:
        assert database.execute(
            "SELECT status, provider, model FROM jobs"
        ).fetchall() == [("SUCCEEDED", "azure_openai", DEPLOYMENT)]
        assert database.execute(
            "SELECT state, provider_request_id FROM attempts"
        ).fetchall() == [("succeeded", None)]
    assert b"offline-azure-child" not in state.read_bytes()
    assert len(list(config.paths.destination.glob("*.png"))) == 1
    assert not list(config.paths.quarantine.glob("*.json"))


def test_offline_azure_cli_webp_expansion_fails_without_upload(
    tmp_path, monkeypatch, capsys
):
    import tcfcomic.cli as cli

    path, config = _azure_config(tmp_path)
    source = config.paths.source / "expands.webp"
    with Image.new("RGB", (256, 256), (1, 2, 3)) as image:
        image.save(source, format="WEBP", lossless=True)
    original = source.read_bytes()
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    data["limits"]["max_input_bytes"] = len(original)
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "offline-azure-key")
    runner = SubprocessAttemptRunner(target=_azure_mock_child)
    monkeypatch.setattr(
        cli, "Processor", lambda cfg, logger, **kwargs: Processor(cfg, logger, runner=runner, **kwargs)
    )
    assert cli.main(["process", "--config", str(path), str(source)]) == 4
    capsys.readouterr()
    with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
        assert db.execute(
            "SELECT status, error_code FROM jobs"
        ).fetchall() == [("FAILED", "IMAGE_LIMIT_EXCEEDED")]
        assert db.execute(
            "SELECT state, error_code FROM attempts"
        ).fetchall() == [("permanent", "IMAGE_LIMIT_EXCEEDED")]
    assert source.read_bytes() == original
    assert not list(config.paths.destination.glob("*.png"))
    records = list(config.paths.quarantine.glob("*.json"))
    assert len(records) == 1
    assert json.loads(records[0].read_text())["error_code"] == "IMAGE_LIMIT_EXCEEDED"


@pytest.mark.parametrize("retry", [False, True])
@pytest.mark.parametrize("equivalent", [False, True])
def test_azure_queued_endpoint_drift_and_canonical_equivalence(
    tmp_path, quiet_logger, monkeypatch, retry, equivalent
):
    _, config = _azure_config(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "offline-azure-key")
    source = write_image(config.paths.source / "queued.png")
    event = threading.Event()
    with Processor(config, quiet_logger) as processor:
        admitted = processor._admit(source, candidate=None, shutdown_event=event)
        if retry:
            from fresh_response import capture_response
            evidence = capture_response(source, 429)
            processor.state.claim_ready(admitted.job_id, datetime.now(UTC))
            attempt = processor.state.begin_attempt(admitted.job_id)
            processor.state.mark_retryable(
                admitted.job_id, attempt.attempt_no, 0,
                "2000-01-01T00:00:00+00:00", ErrorCode.PROVIDER_RETRYABLE, evidence.safe_message,
            )
        assert processor.state.get_job(admitted.job_id).status == (
            JobStatus.READY_RETRY if retry else JobStatus.READY
        )
    endpoint = ENDPOINT.upper() + "/" if equivalent else "https://other.openai.azure.com"
    changed = replace(config, provider=replace(config.provider, endpoint=endpoint))
    calls = []
    real_runner = SubprocessAttemptRunner(target=_azure_mock_child)

    class CountingRunner:
        def run(self, *args):
            calls.append(args)
            return real_runner.run(*args)

    with Processor(changed, quiet_logger, runner=CountingRunner()) as processor:
        assert processor._dispatch_one(event, admitted.job_id)
        job = processor.state.get_job(admitted.job_id)
        assert processor.state.attempt_count(job.job_id) == int(retry) + int(equivalent)
        assert job.status == (JobStatus.SUCCEEDED if equivalent else JobStatus.FAILED)
        if not equivalent:
            assert job.error_code == ErrorCode.STATE_FAILED.value
    assert len(calls) == int(equivalent)
    if not equivalent:
        records = list(config.paths.quarantine.glob("*.json"))
        assert len(records) == 1
        assert json.loads(records[0].read_text())["error_code"] == "STATE_FAILED"
