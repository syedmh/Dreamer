from __future__ import annotations

import asyncio
import hashlib
import io
import json
import logging
import multiprocessing
import signal
import socket
import sqlite3
import threading
import time
import webbrowser
from dataclasses import replace
from functools import partial
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import httpx
import openai
import pytest
import yaml

from conftest import write_config, write_image
from tcfcomic import authentication, auth_probe, cli
from tcfcomic.authentication import AccessToken
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode, JobStatus, SourceSnapshot, TransformRequest
from tcfcomic.processor import Processor
from tcfcomic.providers.openai import OpenAIProvider
from tcfcomic.providers.worker import InProcessAttemptRunner, SubprocessAttemptRunner, _child_entry
from test_interactive_auth import _mock_auth_child

ENDPOINT = "https://offline-test.openai.azure.com"
REQUEST_ID = "12345678-1234-1234-1234-123456789abc"
CANARY = "private-credential-account-prompt-canary"


@pytest.fixture
def tmp_path(tmp_path_factory):
    return tmp_path_factory.mktemp("ad")


def forbidden(*args, **kwargs):
    raise AssertionError("Forbidden runtime/network/browser boundary")


def request_for(path):
    info = path.stat()
    return TransformRequest(
        "a" * 32,
        SourceSnapshot(path, str(path), info.st_size, info.st_mtime_ns,
                       hashlib.sha256(path.read_bytes()).hexdigest(), path),
        "Synthetic prompt.", "synthetic-deployment",
    )


def configuration(tmp_path, *, interactive=False):
    path, _ = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = {"first": "First.", "second": "Second.", "third": "Third."}
    if interactive:
        data["provider"]["authentication"] = "interactive"
        data["provider"]["tenant_id"] = "11111111-2222-3333-4444-555555555555"
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    return path, load_config(path, prepare_paths=False)


def test_check_auth_parser_exists():
    args = cli.build_parser().parse_args(["check-auth", "--config", "synthetic.yaml"])
    assert args.command == "check-auth"
    assert args.config == Path("synthetic.yaml")


def test_first_azure_403_stops_before_second_variant(tmp_path, monkeypatch, quiet_logger):
    _, config = configuration(tmp_path)
    image = write_image(config.paths.source / "synthetic.png")
    calls = []

    def dispatch(request):
        calls.append(request)
        return httpx.Response(403, headers={"apim-request-id": REQUEST_ID}, json={
            "error": {"code": "unknown-" + CANARY, "message": CANARY},
        })

    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(dispatch)) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            for variant in config.provider.variants:
                processor._admit(image, candidate=None, shutdown_event=threading.Event(), variant=variant)
            caught = None
            try:
                processor.process_variants(image)
            except AppError as exc:
                caught = exc
            assert len(calls) == 1, "An access denial must not dispatch another variant"
            assert caught is not None and caught.code == ErrorCode.AUTHENTICATION_FAILED
            jobs = processor.state.list_jobs()
            assert [job.status for job in jobs] == [JobStatus.FAILED, JobStatus.READY, JobStatus.READY]
            for job in jobs[1:]:
                assert processor.state.attempt_count(job.job_id) == 0
                assert Path(job.staged_path).is_file()
            attempts = processor.state.completed_attempts(jobs[0].job_id)
            assert len(attempts) == 1 and attempts[0].finished_at is not None
            assert "HTTP 403" in attempts[0].safe_message
            assert CANARY not in attempts[0].safe_message
            assert "one possibility, not a confirmed cause." in attempts[0].safe_message
            assert attempts[0].safe_message.endswith("Unattempted queued work is retained.")
            assert len(attempts[0].safe_message) < 500


def install_probe(monkeypatch, handler):
    calls, clients, settings = [], [], []
    original_client = httpx.AsyncClient

    async def dispatch(request):
        calls.append(request)
        result = handler(request)
        return await result if asyncio.iscoroutine(result) else result

    def transport(**kwargs):
        assert kwargs == {"retries": 0, "trust_env": False, "verify": True}
        return httpx.MockTransport(dispatch)

    def client(**kwargs):
        settings.append(kwargs)
        assert kwargs["trust_env"] is False
        assert kwargs["follow_redirects"] is False
        assert kwargs["verify"] is True
        assert 0 < kwargs["timeout"] <= 30
        result = original_client(**kwargs)
        clients.append(result)
        return result

    monkeypatch.setattr(httpx, "AsyncHTTPTransport", transport)
    monkeypatch.setattr(httpx, "AsyncClient", client)
    monkeypatch.setattr(
        auth_probe, "_run_in_child",
        lambda endpoint, authentication, secret, timeout, event, cleanup:
        asyncio.run(auth_probe._run_probe(endpoint, authentication, secret, timeout, event)),
    )
    for name in ("DestinationSession", "Processor", "configure_logging"):
        monkeypatch.setattr(cli, name, forbidden)
    monkeypatch.setattr(webbrowser, "open", forbidden)
    return calls, clients, settings


def test_catalog_success_one_get_no_state_no_disclosure(tmp_path, monkeypatch, capsys, caplog):
    path, config = configuration(tmp_path)
    before = {p.relative_to(tmp_path): p.read_bytes() for p in tmp_path.rglob("*") if p.is_file()}
    directories = {p.relative_to(tmp_path) for p in tmp_path.rglob("*") if p.is_dir()}
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", " " + CANARY + " ")
    monkeypatch.setenv("OPENAI_API_KEY", "public-unused-canary")
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(
        200, headers={"apim-request-id": REQUEST_ID},
        json={"data": [{"id": "private-model-canary"}], "extra": CANARY},
    ))
    caplog.set_level(logging.DEBUG)
    previous_logging = logging.root.manager.disable
    assert cli.main(["check-auth", "--config", str(path)]) == 0
    assert logging.root.manager.disable == previous_logging
    assert len(calls) == 1 and all(client.is_closed for client in clients)
    request = calls[0]
    assert request.method == "GET"
    assert str(request.url) == ENDPOINT + "/openai/models?api-version=2024-10-21"
    assert request.content == b""
    assert request.headers["api-key"] == CANARY
    assert "authorization" not in request.headers
    assert "openai-organization" not in request.headers
    assert "openai-project" not in request.headers
    assert request.extensions["timeout"]["read"] <= config.provider.request_timeout_seconds
    output = str(capsys.readouterr())
    assert "catalog request accepted" in output and "not image-edit permission" in output
    assert "HTTP 200" in output and REQUEST_ID in output and "api_key" in output
    for secret in (CANARY, "private-model-canary", "public-unused-canary"):
        assert secret not in output + caplog.text
    assert directories == {p.relative_to(tmp_path) for p in tmp_path.rglob("*") if p.is_dir()}
    assert before == {p.relative_to(tmp_path): p.read_bytes() for p in tmp_path.rglob("*") if p.is_file()}


@pytest.mark.parametrize("status,body,hint", [
    (401, {"error": {"message": CANARY}}, "credential was not accepted"),
    (403, {"error": {"code": "AuthenticationTypeDisabled", "message": CANARY}}, "resource reported"),
    (403, {"error": {"code": "KeyBasedAuthenticationNotPermitted", "message": CANARY}}, "key-based authentication is not permitted"),
    (403, {"error": {"code": "NetworkAccessDenied", "message": CANARY}}, "network access restriction"),
    (403, {"error": {"code": "ForbiddenByFirewall", "message": CANARY}}, "network access restriction"),
    (403, {"error": {"code": "403", "message": CANARY + " Key based authentication is disabled for this resource."}}, "resource reported that key-based authentication is disabled"),
    (403, {"error": {"code": 403, "message": "ACCESS DENIED DUE TO VIRTUAL NETWORK/FIREWALL RULES." + CANARY}}, "network access restriction"),
    (403, {"error": {"code": CANARY, "message": "key disabled permissions " + CANARY}}, "exact access restriction is unknown"),
    (403, {"error": {"code": 403, "message": CANARY}}, "exact access restriction is unknown"),
    (403, {"error": {"code": "moderation_blocked", "message": CANARY}}, "exact access restriction is unknown"),
    (403, {}, "exact access restriction is unknown"),
])
def test_catalog_denial_static_hints(tmp_path, monkeypatch, capsys, caplog, status, body, hint):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(
        status, json=body, headers={"x-request-id": CANARY, "apim-request-id": REQUEST_ID},
    ))
    assert cli.main(["check-auth", "--config", str(path)]) == 3
    output = str(capsys.readouterr())
    assert hint in output
    assert f"HTTP {status}" in output and REQUEST_ID in output
    assert CANARY not in output + caplog.text
    assert len(calls) == 1 and all(client.is_closed for client in clients)


@pytest.mark.parametrize("request_id", [
    "unknown-id", REQUEST_ID + "\n" + CANARY, "\x1b[31m" + REQUEST_ID, "a" * 1000,
])
def test_catalog_untrusted_request_id_not_printed(tmp_path, monkeypatch, capsys, request_id):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    install_probe(monkeypatch, lambda request: httpx.Response(
        403, json={}, headers={"x-request-id": request_id},
    ))
    assert cli.main(["check-auth", "--config", str(path)]) == 3
    output = str(capsys.readouterr())
    assert "Request ID" not in output and CANARY not in output and "\\x1b" not in output


@pytest.mark.parametrize("status,hint", [
    (404, "route is unavailable"), (429, "rate-limited"), (500, "transient"),
    (503, "transient"), (302, "unexpected status"), (201, "unexpected status"),
    (204, "unexpected status"),
])
def test_catalog_inconclusive_never_retries_or_redirects(tmp_path, monkeypatch, capsys, status, hint):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(
        status, content=CANARY, headers={"location": "https://untrusted.example/" + CANARY},
    ))
    assert cli.main(["check-auth", "--config", str(path)]) == 5
    output = str(capsys.readouterr())
    assert "inconclusive" in output and hint in output
    assert CANARY not in output and "AUTHENTICATION_FAILED" not in output
    assert len(calls) == 1 and all(client.is_closed for client in clients)


@pytest.mark.parametrize("body", [b"not-json", b"{}", b'{"data":{}}', b"[]", b"\xff", b"[" * 2000])
def test_catalog_invalid_schema_is_not_success(tmp_path, monkeypatch, capsys, body):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls, _, _ = install_probe(monkeypatch, lambda request: httpx.Response(200, content=body))
    assert cli.main(["check-auth", "--config", str(path)]) == 5
    assert "inconclusive" in capsys.readouterr().err and len(calls) == 1


class CatalogStream(httpx.AsyncByteStream):
    def __init__(self, count, *, stall=False):
        self.count = count
        self.stall = stall
        self.reads = 0
        self.closed = False

    async def __aiter__(self):
        if self.stall:
            await asyncio.sleep(60)
        for _ in range(self.count):
            self.reads += 1
            yield b"x" * 8192

    async def aclose(self):
        self.closed = True


@pytest.mark.parametrize("status,limit,expected", [
    (200, auth_probe.MAX_CATALOG_BYTES, 5), (403, auth_probe.MAX_ERROR_BYTES, 3),
])
def test_catalog_stream_limit_closes_without_full_read(tmp_path, monkeypatch, capsys, status, limit, expected):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    stream = CatalogStream(1000)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(status, stream=stream))
    assert cli.main(["check-auth", "--config", str(path)]) == expected
    assert stream.reads == limit // 8192 + 1 and stream.closed
    assert len(calls) == 1 and all(client.is_closed for client in clients)
    assert "accepted" not in capsys.readouterr().out


def test_catalog_compressed_response_not_decoded(tmp_path, monkeypatch, capsys):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    stream = CatalogStream(1000)
    install_probe(monkeypatch, lambda request: httpx.Response(
        200, stream=stream, headers={"content-encoding": "gzip"},
    ))
    assert cli.main(["check-auth", "--config", str(path)]) == 5
    assert stream.reads == 0 and stream.closed


@pytest.mark.parametrize("status,expected", [(200, 5), (403, 3)])
def test_catalog_total_body_timeout_closes(tmp_path, monkeypatch, capsys, status, expected):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    monkeypatch.setattr(cli, "PROBE_TIMEOUT_SECONDS", 0.15)
    stream = CatalogStream(1, stall=True)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(status, stream=stream))
    started = time.monotonic()
    assert cli.main(["check-auth", "--config", str(path)]) == expected
    assert time.monotonic() - started < 2
    assert stream.closed and len(calls) == 1 and all(client.is_closed for client in clients)


@pytest.mark.parametrize("failure", ["blank", "nonazure", "badendpoint", "badconfig", "ambient", "missingoutput"])
def test_invalid_probe_never_authenticates_or_sends(tmp_path, monkeypatch, capsys, failure):
    path, _ = configuration(tmp_path)
    data = yaml.safe_load(path.read_text())
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    expected = 2
    if failure == "blank":
        monkeypatch.setenv("AZURE_OPENAI_API_KEY", " \t")
        expected = 3
    elif failure == "nonazure":
        data["provider"]["name"] = "fake"
        data["provider"].pop("endpoint")
    elif failure == "badendpoint":
        data["provider"]["endpoint"] = "https://untrusted.example"
    elif failure == "badconfig":
        data["secret"] = CANARY
    elif failure == "missingoutput":
        data["paths"]["destination"] = str(tmp_path / "absent-output")
    else:
        monkeypatch.setenv("AZURE_OPENAI_ENDPOINT", ENDPOINT)
        expected = 5
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    calls, _, _ = install_probe(monkeypatch, forbidden)
    assert cli.main(["check-auth", "--config", str(path)]) == expected
    assert not calls and CANARY not in str(capsys.readouterr())
    assert not (tmp_path / "absent-output").exists()


def test_help_and_validate_are_offline(tmp_path, monkeypatch):
    path, _ = configuration(tmp_path, interactive=True)
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    calls, _, _ = install_probe(monkeypatch, forbidden)
    with pytest.raises(SystemExit) as exited:
        cli.main(["check-auth", "--help"])
    assert exited.value.code == 0
    assert cli.main(["validate", "--config", str(path)]) == 0
    assert not calls


def test_catalog_does_not_open_existing_queue(tmp_path, monkeypatch):
    path, config = configuration(tmp_path)
    internal = config.paths.destination / ".tcfcomic"
    internal.mkdir(exist_ok=True)
    for name in ("state.db", "runtime.lock", "sentinel"):
        (internal / name).write_bytes(b"synthetic-opaque-state")
    before = {p: p.read_bytes() for p in internal.iterdir() if p.is_file()}
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    install_probe(monkeypatch, lambda request: httpx.Response(200, json={"data": []}))
    assert cli.main(["check-auth", "--config", str(path)]) == 0
    assert before == {p: p.read_bytes() for p in internal.iterdir() if p.is_file()}


@pytest.mark.parametrize("phase", ["headers", "body"])
def test_catalog_signal_closes_and_restores_handlers(tmp_path, monkeypatch, phase):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    previous = signal.getsignal(signal.SIGINT)
    stream = CatalogStream(1, stall=True)

    async def dispatch(request):
        asyncio.get_running_loop().call_later(
            0.05, lambda: signal.getsignal(signal.SIGINT)(signal.SIGINT, None),
        )
        if phase == "headers":
            await asyncio.sleep(60)
        return httpx.Response(200, stream=stream)

    calls, clients, _ = install_probe(monkeypatch, dispatch)
    start = time.monotonic()
    assert cli.main(["check-auth", "--config", str(path)]) == 130
    assert time.monotonic() - start < 2
    assert signal.getsignal(signal.SIGINT) == previous
    assert len(calls) == 1 and all(client.is_closed for client in clients)
    if phase == "body":
        assert stream.closed


def test_catalog_interactive_real_ipc_fake_credential(tmp_path, monkeypatch, capsys, caplog):
    path, config = configuration(tmp_path, interactive=True)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "unused-key-canary")
    before = {child.pid for child in multiprocessing.active_children()}
    sessions = []

    def broker(*args):
        session = authentication.AuthenticationBroker(
            config.provider, 1, target=partial(_mock_auth_child, scenario="opaque"),
        )
        sessions.append(session)
        return session

    monkeypatch.setattr(cli, "AuthenticationBroker", broker)
    calls, clients, _ = install_probe(monkeypatch, lambda request: httpx.Response(200, json={"data": []}))
    assert cli.main(["check-auth", "--config", str(path)]) == 0
    assert len(calls) == 1 and "api-key" not in calls[0].headers
    assert calls[0].headers["authorization"] == "Bearer opaque-sensitive-value_2"
    assert len(sessions) == 1 and sessions[0]._process is None
    assert all(client.is_closed for client in clients)
    output = str(capsys.readouterr()) + caplog.text
    assert "opaque-sensitive-value" not in output and "unused-key-canary" not in output
    assert {child.pid for child in multiprocessing.active_children()} == before


@pytest.mark.parametrize("cancel", [False, True])
def test_catalog_bounded_signin_no_orphan(tmp_path, monkeypatch, cancel):
    path, config = configuration(tmp_path, interactive=True)
    before = {child.pid for child in multiprocessing.active_children()}
    previous = signal.getsignal(signal.SIGINT)
    monkeypatch.setattr(cli, "PROBE_TIMEOUT_SECONDS", 0.25)

    class Broker(authentication.AuthenticationBroker):
        def start(self, *args, **kwargs):
            timer = None
            if cancel:
                timer = threading.Timer(0.1, lambda: signal.getsignal(signal.SIGINT)(signal.SIGINT, None))
                timer.start()
            try:
                return super().start(*args, **kwargs)
            finally:
                if timer is not None:
                    timer.join()

    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: Broker(
        config.provider, 0.5, target=partial(_mock_auth_child, scenario="hang_start"),
    ))
    calls, _, _ = install_probe(monkeypatch, forbidden)
    start = time.monotonic()
    assert cli.main(["check-auth", "--config", str(path)]) == (130 if cancel else 3)
    assert time.monotonic() - start < 2
    assert not calls and signal.getsignal(signal.SIGINT) == previous
    assert {child.pid for child in multiprocessing.active_children()} == before


@pytest.mark.parametrize("command", ["process", "watch"])
@pytest.mark.parametrize("quarantine_failure", [None, "os", "app"])
def test_access_denial_cli_durable_before_cleanup(tmp_path, monkeypatch, capsys, command, quarantine_failure):
    path, config = configuration(tmp_path)
    image = write_image(config.paths.source / "synthetic.png")
    calls = []

    def dispatch(request):
        calls.append(request)
        return httpx.Response(403, headers={"retry-after": "45"}, json={
            "error": {"code": 403, "message": CANARY},
        })

    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(dispatch)) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)

        class OfflineProcessor(Processor):
            def __init__(self, cfg, logger, **kwargs):
                super().__init__(cfg, logger, runner=InProcessAttemptRunner(provider), **kwargs)
                for variant in cfg.provider.variants:
                    self._admit(image, candidate=None, shutdown_event=threading.Event(), variant=variant)

            def watch(self, **kwargs):
                return super().watch(max_cycles=5, **kwargs)

            def _quarantine(self, job, stage, error, attempts):
                if quarantine_failure:
                    assert self.state.get_job(job.job_id).status == JobStatus.FAILED
                    history = self.state.completed_attempts(job.job_id)
                    assert len(history) == 1 and history[0].finished_at
                    assert history[0].retry_after_seconds == 45
                    if quarantine_failure == "os":
                        raise OSError(CANARY)
                    raise AppError(ErrorCode.STATE_FAILED, CANARY)
                return super()._quarantine(job, stage, error, attempts)

        monkeypatch.setattr(cli, "Processor", OfflineProcessor)
        args = [command, "--config", str(path)]
        if command == "process":
            args.append(str(image))
        assert cli.main(args) == 3
        assert len(calls) == 1
    output = str(capsys.readouterr())
    assert CANARY not in output
    if quarantine_failure:
        assert "cleanup failed" in output
    with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
        jobs = db.execute("SELECT status, staged_path FROM jobs ORDER BY variant").fetchall()
        assert [row[0] for row in jobs] == ["FAILED", "READY", "READY"]
        assert all(Path(row[1]).is_file() for row in jobs[1:])
        attempts = db.execute("SELECT finished_at,error_code,safe_message,retry_after_seconds FROM attempts").fetchall()
        assert len(attempts) == 1 and attempts[0][0]
        assert attempts[0][1] == "AUTHENTICATION_FAILED" and "HTTP 403" in attempts[0][2]
        assert attempts[0][3] == 45


@pytest.mark.parametrize("status", [401, 403])
@pytest.mark.parametrize("interactive", [False, True])
def test_azure_definitive_access_guard_precedes_unknown_classification(tmp_path, monkeypatch, status, interactive):
    image = write_image(tmp_path / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(
        status, json={"error": {"code": CANARY, "param": CANARY}}, headers={"x-request-id": REQUEST_ID},
    ))) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        token = AccessToken("synthetic-token", int(time.time()) + 3600) if interactive else None
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT, access_token=token)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(image), io.BytesIO())
    assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert CANARY not in raised.value.safe_message and REQUEST_ID in raised.value.safe_message
    if interactive:
        assert "Disabled key-based authentication is one possibility" not in raised.value.safe_message


@pytest.mark.parametrize("status", [401, 403])
def test_fabricated_status_without_response_stays_ambiguous(tmp_path, monkeypatch, status):
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)

    class Fabricated(Exception):
        status_code = status

    class Edits:
        def edit(self, **kwargs):
            raise Fabricated(CANARY)

    class Client:
        images = Edits()

    monkeypatch.setattr(openai, "OpenAI", lambda **kwargs: Client())
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with pytest.raises(AppError) as raised:
        provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == ErrorCode.PROVIDER_AMBIGUOUS


@pytest.mark.parametrize("azure,status,code", [
    (False, 401, ErrorCode.PROVIDER_PERMANENT), (False, 403, ErrorCode.PROVIDER_PERMANENT),
    (True, 400, ErrorCode.PROVIDER_PERMANENT), (True, 429, ErrorCode.PROVIDER_RETRYABLE),
])
def test_public_and_moderation_and_rate_classifications_unchanged(tmp_path, monkeypatch, azure, status, code):
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    monkeypatch.setenv("OPENAI_API_KEY", CANARY)
    body = {"error": {"code": "moderation_blocked"}} if status == 400 else {}
    calls = []

    def dispatch(request):
        calls.append(request)
        return httpx.Response(status, json=body)

    with httpx.Client(transport=httpx.MockTransport(dispatch)) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT if azure else None)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == code and len(calls) == 1


def denial_image_child(config, request, temp_path, result_queue, access_token=None):
    with httpx.Client(transport=httpx.MockTransport(lambda req: httpx.Response(
        403, json={"error": {"code": CANARY, "message": CANARY}},
    ))) as client:
        with (
            patch.object(openai, "DefaultHttpxClient", lambda **kwargs: client),
            patch.object(socket.socket, "connect", forbidden),
            patch.object(socket, "getaddrinfo", forbidden),
            patch.object(webbrowser, "open", forbidden),
        ):
            _child_entry(config, request, temp_path, result_queue, access_token)


def test_access_denial_crosses_real_worker_ipc(tmp_path, monkeypatch, quiet_logger, capsys):
    _, config = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    before = {child.pid for child in multiprocessing.active_children()}
    image = write_image(config.paths.source / "input.png")
    with Processor(config, quiet_logger, runner=SubprocessAttemptRunner(target=denial_image_child)) as processor:
        with pytest.raises(AppError) as raised:
            processor.process_variants(image)
        assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
        assert len(processor.state.completed_attempts(processor.state.list_jobs()[0].job_id)) == 1
    assert CANARY not in str(capsys.readouterr())
    assert {child.pid for child in multiprocessing.active_children()} == before


@pytest.mark.parametrize("stage", ["headers", "body"])
def test_probe_transport_failure_is_inconclusive_no_retry(tmp_path, monkeypatch, capsys, stage):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)

    class BrokenStream(httpx.AsyncByteStream):
        closed = False

        async def __aiter__(self):
            raise httpx.ReadTimeout(CANARY)
            yield b""  # Async iterator protocol, unreachable after the transport fault.

        async def aclose(self):
            self.closed = True

    stream = BrokenStream()

    def dispatch(request):
        if stage == "headers":
            raise httpx.ConnectTimeout(CANARY)
        return httpx.Response(200, stream=stream)

    calls, clients, _ = install_probe(monkeypatch, dispatch)
    assert cli.main(["check-auth", "--config", str(path)]) == 5
    output = str(capsys.readouterr())
    assert "inconclusive" in output and CANARY not in output
    assert len(calls) == 1 and all(client.is_closed for client in clients)
    if stage == "body":
        assert stream.closed


def test_http403_moderation_is_permanent_rejection_not_access_failure(tmp_path, monkeypatch):
    # Deliberate behavior change (VP decision): an allowlisted content-policy code on 401/403 is
    # classified like the 400 moderation case, not as AUTHENTICATION_FAILED.
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(
        403, json={"error": {"code": "moderation_blocked", "message": CANARY}},
    ))) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == ErrorCode.PROVIDER_PERMANENT
    assert "moderation_blocked" in raised.value.safe_message
    assert "disabled" not in raised.value.safe_message.lower()
    assert "check-auth" not in raised.value.safe_message
    assert CANARY not in raised.value.safe_message


@pytest.mark.parametrize("status", [401, 403])
@pytest.mark.parametrize("body", [
    {"error": {"code": "AuthenticationTypeDisabled", "innererror": {"code": "moderation_blocked"}}},
    {"error": {"code": "moderation_blocked", "innererror": {"code": "NetworkAccessDenied"}}},
    {"error": {"code": "PermissionDenied", "innererror": {"code": "content_filter"}}},
    {"error": {"code": "Unauthorized", "type": "moderation_blocked"}},
    {"error": {"code": "moderation_blocked", "type": "Forbidden"}},
    {"error": {"code": "moderation_blocked",
               "message": "Access denied due to Virtual Network/Firewall rules."}},
])
def test_http_access_denial_with_content_and_access_codes_still_stops(
    tmp_path, monkeypatch, status, body,
):
    # A content-policy code only reclassifies a 401/403 when no access code is present.
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(
        lambda request: httpx.Response(status, json=body),
    )) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert "check-auth" in raised.value.safe_message


@pytest.mark.parametrize("status", [401, 403])
def test_http_content_code_without_access_code_is_permanent(tmp_path, monkeypatch, status):
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    body = {"error": {"code": "moderation_blocked", "innererror": {"code": "content_filter"}}}
    with httpx.Client(transport=httpx.MockTransport(
        lambda request: httpx.Response(status, json=body),
    )) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == ErrorCode.PROVIDER_PERMANENT


@pytest.mark.parametrize("status", [401, 403])
@pytest.mark.parametrize("error", [
    None, {"code": "unknown-" + CANARY}, {"code": "AuthenticationTypeDisabled"},
    {"code": "PermissionDenied"}, {"code": "Forbidden"},
])
def test_http_access_denial_without_content_code_still_stops(tmp_path, monkeypatch, status, error):
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    body = {"error": error} if error is not None else {}
    with httpx.Client(transport=httpx.MockTransport(
        lambda request: httpx.Response(status, json=body),
    )) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with pytest.raises(AppError) as raised:
            provider.transform(request_for(write_image(tmp_path / "input.png")), io.BytesIO())
    assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert CANARY not in raised.value.safe_message


def _moderation403(calls):
    def dispatch(request):
        calls.append(request)
        return httpx.Response(403, json={"error": {"code": "moderation_blocked", "message": CANARY}})
    return dispatch


def test_moderation403_continues_remaining_prompts(tmp_path, monkeypatch, quiet_logger):
    _, config = configuration(tmp_path)
    image = write_image(config.paths.source / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls = []
    with httpx.Client(transport=httpx.MockTransport(_moderation403(calls))) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            results = processor.process_variants(image)
            assert len(calls) == len(results) == 3
            assert all(result.error_code == ErrorCode.PROVIDER_PERMANENT for result in results)


def test_moderation403_triggers_configured_fallback(tmp_path, monkeypatch, quiet_logger):
    path, _ = configuration(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["provider"]["fallbacks"] = {"first": "second"}
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    config = load_config(path, prepare_paths=False)
    assert list(config.provider.primary_variants) == ["first", "third"]
    image = write_image(config.paths.source / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls = []
    with httpx.Client(transport=httpx.MockTransport(_moderation403(calls))) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            processor.process_variants(image)
            jobs = {job.variant: job for job in processor.state.list_jobs()}
    assert set(jobs) == {"first", "second", "third"}
    assert jobs["first"].error_code == ErrorCode.PROVIDER_PERMANENT.value
    assert jobs["second"].status == JobStatus.FAILED
    assert len(calls) == 3


def test_moderation400_continues_remaining_prompts(tmp_path, monkeypatch, quiet_logger):
    _, config = configuration(tmp_path)
    image = write_image(config.paths.source / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls = []

    def dispatch(request):
        calls.append(request)
        return httpx.Response(400, json={"error": {"code": "moderation_blocked"}})

    with httpx.Client(transport=httpx.MockTransport(dispatch)) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            results = processor.process_variants(image)
            assert len(calls) == len(results) == 3
            assert all(result.error_code == ErrorCode.PROVIDER_PERMANENT for result in results)
            assert all(result.attempts == 1 for result in results)


def test_access_failures_never_reopen_across_invocations(tmp_path, monkeypatch, quiet_logger):
    _, config = configuration(tmp_path)
    image = write_image(config.paths.source / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    calls = []

    def dispatch(request):
        calls.append(request)
        return httpx.Response(403, json={"error": {"code": 403}})

    with httpx.Client(transport=httpx.MockTransport(dispatch)) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        for index in range(3):
            with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
                with pytest.raises(AppError) as raised:
                    processor.process_variants(image)
                assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
                assert len(calls) == index + 1
                assert all(processor.state.attempt_count(job.job_id) == 1 for job in processor.state.list_jobs())
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            results = processor.process_variants(image)
            assert all(result.status == JobStatus.FAILED and result.attempts == 1 for result in results)
            assert len(calls) == 3


def test_failure_preserves_two_rpm_completion_gate(tmp_path, monkeypatch, quiet_logger):
    from datetime import datetime

    _, config = configuration(tmp_path)
    config = replace(config, provider=replace(config.provider, requests_per_minute=2))
    image = write_image(config.paths.source / "input.png")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    with httpx.Client(transport=httpx.MockTransport(lambda request: httpx.Response(403, json={}))) as client:
        monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: client)
        provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
        with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
            with pytest.raises(AppError):
                processor.process_variants(image)
            attempt = processor.state.completed_attempts(processor.state.list_jobs()[0].job_id)[0]
            gate = processor.state.provider_not_before(2)
            assert (gate - datetime.fromisoformat(attempt.finished_at)).total_seconds() >= 30


@pytest.fixture
def probe_ipc_boundary(monkeypatch):
    def install(*, boundary, result=None, cancel=False):
        event = threading.Event()
        clock = [0.0]
        polls = []
        received = []
        connections_closed = []

        class Parent:
            def poll(self, timeout):
                polls.append(timeout)
                assert len(polls) <= 2, "Terminal IPC checks must not drain indefinitely"
                if cancel:
                    event.set()
                return result is not None and (boundary == "deadline" or len(polls) > 1)

            def recv_bytes(self, limit):
                assert limit == auth_probe.MAX_RESULT_BYTES
                received.append(result)
                return json.dumps(result).encode("ascii")

            def close(self):
                connections_closed.append("parent")

        class Child:
            def close(self):
                connections_closed.append("child")

        class Process:
            pid = None
            closed = False
            started = False

            def start(self):
                self.started = True
                if boundary == "deadline":
                    clock[0] = 2.0

            def is_alive(self):
                return boundary == "deadline"

            def close(self):
                self.closed = True

        process = Process()

        def make_process(**kwargs):
            assert kwargs["args"][4] == 2.0
            return process

        context = SimpleNamespace(Pipe=lambda **kwargs: (Parent(), Child()), Process=make_process)
        monkeypatch.setattr(auth_probe.multiprocessing, "get_context", lambda method: context)
        monkeypatch.setattr(auth_probe, "time", SimpleNamespace(monotonic=lambda: clock[0]))
        monkeypatch.setattr(cli, "time", auth_probe.time)
        return SimpleNamespace(
            event=event, polls=polls, received=received, process=process,
            connections_closed=connections_closed,
        )

    return install


@pytest.mark.parametrize("boundary", ["deadline", "death"])
@pytest.mark.parametrize("status", [401, 403])
def test_probe_terminal_boundary_preserves_queued_denial(probe_ipc_boundary, boundary, status):
    message = f"Azure denied catalog access. Authentication api_key. HTTP {status}."
    state = probe_ipc_boundary(boundary=boundary, result={"denial": message})
    with pytest.raises(AppError) as raised:
        auth_probe._run_in_child(ENDPOINT, "api_key", CANARY, 2.0, state.event, 1.0)
    assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert raised.value.safe_message == message
    assert state.received == [{"denial": message}]
    assert state.polls == ([0] if boundary == "deadline" else [0.05, 0])
    assert state.process.started and state.process.closed
    assert "parent" in state.connections_closed and "child" in state.connections_closed


@pytest.mark.parametrize("boundary", ["deadline", "death"])
@pytest.mark.parametrize("result", [None, {"message": "HTTP 403"}, {"denial": ""}])
def test_probe_terminal_boundary_without_valid_denial_is_inconclusive(
    tmp_path, monkeypatch, capsys, probe_ipc_boundary, boundary, result,
):
    path, config = configuration(tmp_path)
    assert config.provider.request_timeout_seconds == 2
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    state = probe_ipc_boundary(boundary=boundary, result=result)
    assert cli.main(["check-auth", "--config", str(path)]) == 5
    output = str(capsys.readouterr())
    assert "inconclusive" in output
    assert "AUTHENTICATION_FAILED" not in output and CANARY not in output
    assert state.process.started and state.process.closed
    if result is None:
        assert not state.received
        assert ("timed out" if boundary == "deadline" else "stopped without a result") in output


@pytest.mark.parametrize("boundary", ["deadline", "death"])
def test_probe_terminal_boundary_cancellation_precedes_queued_denial(probe_ipc_boundary, boundary):
    state = probe_ipc_boundary(
        boundary=boundary, result={"denial": "Azure denied catalog access. HTTP 403."}, cancel=True,
    )
    with pytest.raises(AppError) as raised:
        auth_probe._run_in_child(ENDPOINT, "api_key", CANARY, 2.0, state.event, 1.0)
    assert raised.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert not state.received and state.process.closed


def mock_probe_worker(
    connection, endpoint, mode, secret, timeout, *, status=200, scenario="response", request_started=None,
):
    calls = []
    original_connect = socket.socket.connect
    original_connect_ex = socket.socket.connect_ex

    def loopback_only(original, sock, address):
        # Windows asyncio may create a loopback socket pair for its wakeup pipe.
        if isinstance(address, tuple) and address[0] in {"127.0.0.1", "::1"}:
            return original(sock, address)
        return forbidden(sock, address)

    async def dispatch(request):
        calls.append(request)
        assert len(calls) == 1
        assert request.method == "GET" and request.content == b""
        assert str(request.url) == ENDPOINT + "/openai/models?api-version=2024-10-21"
        if mode == "api_key":
            assert request.headers["api-key"] == CANARY
            assert "authorization" not in request.headers
        else:
            assert request.headers["authorization"] == "Bearer opaque-sensitive-value_2"
            assert "api-key" not in request.headers
        if request_started is not None:
            request_started.set()
        logging.getLogger("httpcore").critical(CANARY)
        if scenario == "blocked-resolver":
            time.sleep(60)  # Simulate a non-cancellable platform resolver.
        if scenario == "body-stall":
            return httpx.Response(status, stream=CatalogStream(1, stall=True))
        return httpx.Response(status, headers={"apim-request-id": REQUEST_ID}, json={
            "data": [{"id": CANARY}], "error": {"code": CANARY, "message": CANARY},
        })

    with (
        patch.object(httpx, "AsyncHTTPTransport", lambda **kwargs: httpx.MockTransport(dispatch)),
        patch.object(socket.socket, "connect", lambda sock, address: loopback_only(original_connect, sock, address)),
        patch.object(socket.socket, "connect_ex", lambda sock, address: loopback_only(original_connect_ex, sock, address)),
        patch.object(socket, "getaddrinfo", forbidden),
        patch.object(webbrowser, "open", forbidden),
    ):
        auth_probe._probe_child(connection, endpoint, mode, secret, timeout)


@pytest.mark.parametrize("status,scenario,expected", [
    (200, "response", 0), (401, "response", 3), (403, "response", 3),
    (403, "body-stall", 3), (200, "body-stall", 5), (200, "blocked-resolver", 5),
])
def test_real_probe_worker_mock_http_bounded_and_secret_safe(
    tmp_path, monkeypatch, capsys, caplog, status, scenario, expected,
):
    path, _ = configuration(tmp_path)
    # Spawn imports this test module (including OpenAI/Pillow) before reaching HTTP.
    # Keep a bounded startup allowance; terminal_boundary tests retain the 2s deadline.
    request_budget = 5
    data = yaml.safe_load(path.read_text())
    data["provider"]["request_timeout_seconds"] = request_budget
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    request_started = multiprocessing.get_context("spawn").Event()
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    for name in ("DestinationSession", "Processor", "configure_logging"):
        monkeypatch.setattr(cli, name, forbidden)
    monkeypatch.setattr(auth_probe, "_probe_child", partial(
        mock_probe_worker, status=status, scenario=scenario, request_started=request_started,
    ))
    before = {child.pid for child in multiprocessing.active_children()}
    previous = signal.getsignal(signal.SIGINT)
    started = time.monotonic()
    exit_code = cli.main(["check-auth", "--config", str(path)])
    elapsed = time.monotonic() - started
    assert request_started.is_set(), "Spawn must reach the mock HTTP transport, not just time out during imports"
    assert exit_code == expected
    assert elapsed < request_budget + 2
    if scenario != "response":
        assert elapsed >= request_budget - 0.5
    assert {child.pid for child in multiprocessing.active_children()} == before
    assert signal.getsignal(signal.SIGINT) == previous
    output = str(capsys.readouterr()) + caplog.text
    assert CANARY not in output
    if expected == 3:
        assert f"HTTP {status}" in output


def test_real_interactive_and_http_workers_use_only_memory_tokens(tmp_path, monkeypatch, capsys):
    path, config = configuration(tmp_path, interactive=True)
    data = yaml.safe_load(path.read_text())
    data["provider"]["request_timeout_seconds"] = 5
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "unused-key-canary")
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: authentication.AuthenticationBroker(
        config.provider, 1, target=partial(_mock_auth_child, scenario="opaque"),
    ))
    monkeypatch.setattr(auth_probe, "_probe_child", mock_probe_worker)
    for name in ("DestinationSession", "Processor", "configure_logging"):
        monkeypatch.setattr(cli, name, forbidden)
    before = {child.pid for child in multiprocessing.active_children()}
    assert cli.main(["check-auth", "--config", str(path)]) == 0
    output = str(capsys.readouterr())
    assert "opaque-sensitive-value" not in output and "unused-key-canary" not in output
    assert {child.pid for child in multiprocessing.active_children()} == before


def test_real_probe_worker_cancellation_kills_blocked_resolver(tmp_path, monkeypatch):
    path, _ = configuration(tmp_path)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", CANARY)
    monkeypatch.setattr(auth_probe, "_probe_child", partial(mock_probe_worker, scenario="blocked-resolver"))
    before = {child.pid for child in multiprocessing.active_children()}
    previous = signal.getsignal(signal.SIGINT)
    timer = threading.Timer(0.9, lambda: signal.getsignal(signal.SIGINT)(signal.SIGINT, None))
    timer.start()
    started = time.monotonic()
    try:
        assert cli.main(["check-auth", "--config", str(path)]) == 130
    finally:
        timer.join()
    assert time.monotonic() - started < 2.5
    assert signal.getsignal(signal.SIGINT) == previous
    assert {child.pid for child in multiprocessing.active_children()} == before
