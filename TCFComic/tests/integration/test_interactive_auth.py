from __future__ import annotations

import base64
import io
import json
import logging
import multiprocessing
import os
import signal
import socket
import sqlite3
import sys
import threading
import time
import webbrowser
from dataclasses import replace
from datetime import UTC, datetime
from functools import partial
from unittest.mock import patch

import httpx
import openai
import pytest
import yaml
from azure.core.credentials import AccessToken as SDKToken
from azure.core.exceptions import ClientAuthenticationError
from azure.identity import AuthenticationRequiredError
from PIL import Image

from conftest import write_config, write_image
from tcfcomic import authentication as auth
from tcfcomic import cli
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.processor import Processor
from tcfcomic.providers.worker import SubprocessAttemptRunner, ShutdownDeadline, _child_entry
from tcfcomic.redaction import _secrets

ENDPOINT = "https://sweepertestai.openai.azure.com"
TOKEN_PREFIX = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.signature_"
OPAQUE_PREFIX = "opaque-sensitive-value_"


def forbidden(*args, **kwargs):
    raise AssertionError("No network, DNS, or browser in offline verification")


def _mock_auth_child(connection, config, *, scenario="success"):
    """Spawn the real broker protocol, mocking only the credential boundary."""
    import azure.identity

    class Credential:
        def __init__(self, **kwargs):
            assert kwargs["authority"] == auth.AUTHORITY
            assert kwargs["tenant_id"] == (config.tenant_id or auth.DEFAULT_TENANT)
            assert kwargs["client_id"] == (config.client_id or auth.DEFAULT_CLIENT)
            assert kwargs["disable_automatic_authentication"] is True
            assert kwargs["enable_support_logging"] is False
            assert kwargs["logging_enable"] is False
            assert kwargs["use_env_settings"] is False
            assert "cache_persistence_options" not in kwargs
            assert kwargs.get("redirect_uri") == config.redirect_uri
            self.logins = 0
            self.calls = 0
        def authenticate(self, *, scopes):
            assert scopes == [auth.SCOPE]
            self.logins += 1
            assert self.logins == 1
            if scenario == "login_fail":
                raise ClientAuthenticationError("DO_NOT_PRINT_ACCOUNT_OR_CLAIMS")
            if scenario == "browser_unavailable":
                raise OSError("DO_NOT_PRINT_BROWSER_DETAILS")
            if scenario == "login_cancel":
                raise AuthenticationRequiredError([auth.SCOPE], "DO_NOT_PRINT_LOGIN_CANCEL")
            if scenario == "hang_start":
                time.sleep(60)
        def get_token(self, *scopes):
            assert scopes == (auth.SCOPE,)
            assert self.logins == 1
            self.calls += 1
            if scenario == "refresh_fail" and self.calls > 1:
                raise AuthenticationRequiredError([auth.SCOPE], "DO_NOT_PRINT_REFRESH_CLAIMS")
            if scenario == "hang_refresh" and self.calls > 1:
                time.sleep(60)
            lifetime = 1 if (
                scenario == "short_lifetime"
                or (scenario == "short_refresh" and self.calls > 1)
            ) else 3600
            token = (OPAQUE_PREFIX if scenario == "opaque" else TOKEN_PREFIX) + str(self.calls)
            assert token not in os.environ.values()
            assert token not in " ".join(sys.argv)
            logging.getLogger("azure.identity").critical(token)
            return SDKToken(token, int(time.time()) + lifetime)
        def close(self):
            pass

    with (
        patch.object(azure.identity, "InteractiveBrowserCredential", Credential),
        patch.object(socket, "getaddrinfo", forbidden),
        patch.object(socket.socket, "connect", forbidden),
        patch.object(socket.socket, "connect_ex", forbidden),
        patch.object(webbrowser, "open", forbidden),
    ):
        auth._auth_child(connection, config)


def _mock_image_child(config, request, temp_path, result_queue, token, *, status=200, hang=False):
    assert token.token not in os.environ.values()
    assert token.token not in " ".join(sys.argv)
    assert not hasattr(config, "token")
    assert not hasattr(request, "token")
    clients = []
    def dispatch(http):
        assert http.headers["authorization"] == "Bearer " + token.token
        assert "api-key" not in http.headers
        assert str(http.url) == ENDPOINT + "/openai/v1/images/edits?api-version=preview"
        if hang:
            time.sleep(60)
        if status != 200:
            return httpx.Response(status, json={"error": {"message": token.token}})
        with Image.new("RGB", (3, 2), (1, 2, 3)) as image, io.BytesIO() as buffer:
            image.save(buffer, format="PNG")
            encoded = base64.b64encode(buffer.getvalue()).decode()
        return httpx.Response(200, json={"id": token.token, "data": [{"b64_json": encoded}]})
    def client(**kwargs):
        assert kwargs == {"trust_env": False, "follow_redirects": False}
        result = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
        clients.append(result)
        return result
    try:
        with (
            patch.object(openai, "DefaultHttpxClient", client),
            patch.object(socket, "getaddrinfo", forbidden),
            patch.object(socket.socket, "connect", forbidden),
            patch.object(socket.socket, "connect_ex", forbidden),
            patch.object(webbrowser, "open", forbidden),
        ):
            _child_entry(config, request, temp_path, result_queue, token)
    finally:
        for client in clients:
            client.close()


def configuration(tmp_path):
    path, _ = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text())
    data["provider"]["authentication"] = "interactive"
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    return path, cli.load_config(path)


def broker(config, scenario="success"):
    return auth.AuthenticationBroker(
        config.provider, config.shutdown.timeout_seconds,
        target=partial(_mock_auth_child, scenario=scenario),
    )


def deadline(event=None):
    event = event or threading.Event()
    return ShutdownDeadline(event.is_set, 1)


def assert_no_children(before):
    assert {child.pid for child in multiprocessing.active_children()} == before


def test_broker_one_login_refreshes_without_browser_and_bounds_redaction(tmp_path, capsys, monkeypatch):
    _, config = configuration(tmp_path)
    monkeypatch.setenv("AZURE_AUTHORITY_HOST", "https://not-used.example")
    monkeypatch.setenv("AZURE_TENANT_ID", "ambient-not-used")
    before = {child.pid for child in multiprocessing.active_children()}
    original_secrets = dict(_secrets)
    with broker(config) as session:
        session.start(37, deadline())
        pid = session._process.pid
        for index in range(2, 5):
            token = session.acquire(37, deadline())
            assert token.token == TOKEN_PREFIX + str(index)
            assert session._process.pid == pid
            assert len(_secrets) == len(original_secrets) + 1
        assert token.token not in repr(token)
    assert _secrets == original_secrets
    assert TOKEN_PREFIX not in str(capsys.readouterr())
    assert_no_children(before)


@pytest.mark.parametrize("scenario", [
    "login_fail", "login_cancel", "browser_unavailable", "short_lifetime",
])
def test_startup_failures_are_safe_no_state_or_leaks(tmp_path, monkeypatch, capsys, scenario):
    path, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: broker(config, scenario))
    image = write_image(config.paths.source / "input.png")
    assert cli.main(["process", "--config", str(path), str(image)]) == 3
    output = capsys.readouterr().err
    assert "AUTHENTICATION_FAILED" in output
    assert "DO_NOT_PRINT" not in output and TOKEN_PREFIX not in output
    assert not (config.paths.destination / ".tcfcomic" / "state.db").exists()
    assert_no_children(before)


@pytest.mark.parametrize("command,expected", [("process", 130), ("watch", 0)])
def test_signal_during_startup_closes_broker_before_processor(tmp_path, monkeypatch, command, expected):
    path, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    class CancellingBroker(auth.AuthenticationBroker):
        def start(self, *args):
            timer = threading.Timer(0.4, lambda: signal.getsignal(signal.SIGINT)(signal.SIGINT, None))
            timer.start()
            try:
                return super().start(*args)
            finally:
                timer.join()
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: CancellingBroker(
        config.provider, 1, target=partial(_mock_auth_child, scenario="hang_start"),
    ))
    arguments = [command, "--config", str(path)]
    if command == "process":
        arguments.append(str(write_image(config.paths.source / "input.png")))
    start = time.monotonic()
    assert cli.main(arguments) == expected
    assert time.monotonic() - start < 3
    assert not (config.paths.destination / ".tcfcomic" / "state.db").exists()
    assert_no_children(before)


def test_startup_timeout_and_cleanup(tmp_path, monkeypatch):
    _, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    monkeypatch.setattr(auth, "STARTUP_TIMEOUT_SECONDS", 0.2)
    start = time.monotonic()
    with broker(config, "hang_start") as session, pytest.raises(AppError, match="timed out"):
        session.start(37, deadline())
    assert time.monotonic() - start < 3
    assert_no_children(before)


@pytest.mark.parametrize("command", ["process", "watch"])
@pytest.mark.parametrize("scenario", ["success", "opaque"])
def test_cli_real_spawned_broker_and_image_workers_no_disclosure(
    tmp_path, monkeypatch, capsys, command, scenario,
):
    path, config = configuration(tmp_path)
    sources = [write_image(config.paths.source / f"input-{i}.png") for i in range(2)]
    before = {child.pid for child in multiprocessing.active_children()}
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: broker(config, scenario))
    real_processor = Processor
    class BoundedProcessor(real_processor):
        def __init__(self, cfg, logger, **kwargs):
            super().__init__(cfg, logger, runner=SubprocessAttemptRunner(target=_mock_image_child), **kwargs)
        def watch(self, **kwargs):
            return super().watch(max_cycles=2, **kwargs)
    monkeypatch.setattr(cli, "Processor", BoundedProcessor)
    args = [command, "--config", str(path)]
    if command == "process":
        args.append(str(sources[0]))
    assert cli.main(args) == 0
    output = str(capsys.readouterr())
    assert TOKEN_PREFIX not in output and OPAQUE_PREFIX not in output
    with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
        attempts = db.execute("SELECT state, provider_request_id FROM attempts").fetchall()
        assert attempts == [("succeeded", None)] * (2 if command == "watch" else 1)
    for file in config.paths.destination.rglob("*"):
        if file.is_file():
            assert TOKEN_PREFIX.encode() not in file.read_bytes()
            assert OPAQUE_PREFIX.encode() not in file.read_bytes()
    assert_no_children(before)


def test_empty_watch_still_signs_in_once(tmp_path, monkeypatch):
    path, config = configuration(tmp_path)
    sessions = []
    def create(*args):
        result = broker(config)
        sessions.append(result)
        return result
    monkeypatch.setattr(cli, "AuthenticationBroker", create)
    class EmptyWatch(Processor):
        def watch(self, **kwargs):
            assert self.auth_session.started
            return super().watch(max_cycles=1, **kwargs)
    monkeypatch.setattr(cli, "Processor", EmptyWatch)
    assert cli.main(["watch", "--config", str(path)]) == 0
    assert len(sessions) == 1 and not sessions[0].started


@pytest.mark.parametrize("retry", [False, True])
@pytest.mark.parametrize("scenario", ["refresh_fail", "hang_refresh", "short_refresh"])
def test_refresh_failure_before_claim_preserves_entire_queue(
    tmp_path, quiet_logger, monkeypatch, retry, scenario,
):
    _, config = configuration(tmp_path)
    monkeypatch.setattr(auth, "REFRESH_TIMEOUT_SECONDS", 0.2)
    before = {child.pid for child in multiprocessing.active_children()}
    with broker(config, scenario) as session:
        session.start(37, deadline())
        with Processor(config, quiet_logger, auth_session=session) as processor:
            source = write_image(config.paths.source / "input.png")
            admitted = processor._admit(source, candidate=None, shutdown_event=threading.Event())
            if retry:
                from fresh_response import capture_response
                evidence = capture_response(source, 429)
                processor.state.claim_ready(admitted.job_id, datetime.now(UTC))
                attempt = processor.state.begin_attempt(admitted.job_id)
                processor.state.mark_retryable(
                    admitted.job_id, attempt.attempt_no, 0, "2000-01-01T00:00:00+00:00",
                    ErrorCode.PROVIDER_RETRYABLE, evidence.safe_message,
                )
            job_before = processor.state.get_job(admitted.job_id)
            with pytest.raises(AppError) as caught:
                processor._dispatch_one(threading.Event(), admitted.job_id)
            assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
            assert processor.state.get_job(admitted.job_id) == job_before
            assert processor.state.attempt_count(admitted.job_id) == int(retry)
    assert_no_children(before)


def _bad_protocol_child(connection, config, *, reply):
    del config
    connection.recv_bytes(auth.MAX_MESSAGE_BYTES)
    if reply == "oversized":
        connection.send_bytes(b"x" * (auth.MAX_MESSAGE_BYTES + 1))
    else:
        connection.send_bytes(json.dumps(reply).encode())
    connection.close()


@pytest.mark.parametrize("reply", [
    [], None, {"status": "ok"}, {"status": "ok", "token": "x", "expires_on": True},
    {"status": "ok", "token": 123, "expires_on": 123456},
    {"status": "ok", "token": "bad\r\nheader", "expires_on": 123456},
    {"status": "ok", "token": "x" * (auth.MAX_TOKEN_LENGTH + 1), "expires_on": 123456},
    {"status": "ok", "token": "x", "expires_on": "99999999999"},
    {"status": "ok", "token": "x", "expires_on": 99999999999},
    {"status": "ok", "token": "x", "expires_on": 0},
    {"status": "error", "category": "not_allowlisted", "claims": "DO_NOT_PRINT"},
    "oversized",
])
def test_broker_rejects_malformed_bounded_protocol(tmp_path, reply, capsys):
    _, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    with auth.AuthenticationBroker(
        config.provider, 1, target=partial(_bad_protocol_child, reply=reply),
    ) as session, pytest.raises(AppError) as caught:
        session.start(37, deadline())
    assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert "DO_NOT_PRINT" not in str(capsys.readouterr())
    assert_no_children(before)


def test_refresh_signal_at_successful_acquisition_cannot_claim(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    event = threading.Event()
    class CancellingSession:
        started = True
        def acquire(self, minimum_lifetime, shutdown):
            event.set()
            return auth.AccessToken("offline-token", int(time.time()) + 3600)
    with Processor(config, quiet_logger, auth_session=CancellingSession()) as processor:
        job = processor._admit(
            write_image(config.paths.source / "input.png"), candidate=None, shutdown_event=event,
        )
        snapshot = processor.state.get_job(job.job_id)
        with pytest.raises(AppError) as caught:
            processor._dispatch_one(event)
        assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
        assert processor.state.get_job(job.job_id) == snapshot
        assert processor.state.attempt_count(job.job_id) == 0


def test_direct_preflight_cannot_dispatch_insufficient_lifetime(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    class ShortSession:
        started = True
        def acquire(self, minimum_lifetime, shutdown):
            return auth.AccessToken("offline-token", int(time.time()) + 2)
    with Processor(config, quiet_logger, auth_session=ShortSession()) as processor:
        job = processor._admit(
            write_image(config.paths.source / "input.png"), candidate=None,
            shutdown_event=threading.Event(),
        )
        snapshot = processor.state.get_job(job.job_id)
        with pytest.raises(AppError) as caught:
            processor._dispatch_one(threading.Event())
        assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
        assert processor.state.get_job(job.job_id) == snapshot
        assert processor.state.attempt_count(job.job_id) == 0


def test_unchanged_terminal_source_not_replayed_after_auth_change(tmp_path, quiet_logger):
    from tcfcomic.providers.worker import InProcessAttemptRunner
    from tcfcomic.providers.fake import FakeProvider

    _, interactive = configuration(tmp_path)
    key_config = replace(interactive, provider=replace(interactive.provider, authentication="api_key"))
    source = write_image(key_config.paths.source / "input.png")
    with Processor(key_config, quiet_logger, runner=InProcessAttemptRunner(FakeProvider())) as processor:
        original = processor.process_path(source)
        assert original.status == JobStatus.SUCCEEDED
    with broker(interactive, "refresh_fail") as session:
        session.start(37, deadline())
        with Processor(interactive, quiet_logger, auth_session=session) as processor:
            result = processor.process_path(source)
            assert result == original
            assert processor.state.attempt_count(result.job_id) == 1


def test_actual_credential_constructor_memory_cache_silent_and_fixed_authority(monkeypatch):
    from azure.identity import InteractiveBrowserCredential

    monkeypatch.setenv("AZURE_AUTHORITY_HOST", "https://evil.example")
    monkeypatch.setenv("AZURE_CLIENT_ID", "ambient-ignored")
    with InteractiveBrowserCredential(
        authority=auth.AUTHORITY, tenant_id=auth.DEFAULT_TENANT, client_id=auth.DEFAULT_CLIENT,
        disable_automatic_authentication=True, enable_support_logging=False,
        logging_enable=False, redirect_uri="http://localhost:8400/",
        use_env_settings=False,
    ) as credential:
        assert credential._authority == auth.AUTHORITY
        assert credential._client_id == auth.DEFAULT_CLIENT
        assert credential._tenant_id == auth.DEFAULT_TENANT
        assert credential._cache_options is None
        assert credential._enable_support_logging is False
        monkeypatch.setattr(credential, "_request_token", forbidden)
        with pytest.raises(AuthenticationRequiredError):
            credential.get_token(auth.SCOPE)


def test_signin_transport_ignores_environment_proxy_and_ca(monkeypatch):
    from azure.identity import InteractiveBrowserCredential

    monkeypatch.setenv("HTTPS_PROXY", "http://offline-proxy.invalid:8181")
    monkeypatch.setenv("REQUESTS_CA_BUNDLE", r"C:\offline-only-missing-ca.pem")
    monkeypatch.setenv("CURL_CA_BUNDLE", r"C:\offline-only-missing-curl-ca.pem")
    with InteractiveBrowserCredential(
        authority=auth.AUTHORITY, tenant_id=auth.DEFAULT_TENANT,
        client_id=auth.DEFAULT_CLIENT, disable_automatic_authentication=True,
        enable_support_logging=False, logging_enable=False, use_env_settings=False,
    ) as credential:
        transport = credential._client._pipeline._transport
        transport.open()
        assert transport.session.trust_env is False
        settings = transport.session.merge_environment_settings(
            auth.AUTHORITY + "/organizations/oauth2/v2.0/token",
            {}, False, True, None,
        )
        assert settings["proxies"] == {}
        assert settings["verify"] is True


def test_empty_and_delayed_queue_do_not_refresh(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    with broker(config, "refresh_fail") as session:
        session.start(37, deadline())
        with Processor(config, quiet_logger, auth_session=session) as processor:
            event = threading.Event()
            assert not processor._dispatch_one(event)
            job = processor._admit(
                write_image(config.paths.source / "input.png"), candidate=None, shutdown_event=event,
            )
            processor.state.claim_ready(job.job_id, datetime.now(UTC))
            attempt = processor.state.begin_attempt(job.job_id)
            processor.state.mark_retryable(
                job.job_id, attempt.attempt_no, 0, "2999-01-01T00:00:00+00:00",
                ErrorCode.PROVIDER_RETRYABLE, "offline",
            )
            assert not processor._dispatch_one(event)
            assert not processor._dispatch_one(event, job.job_id)


def test_watch_refresh_failure_is_terminal_exit3(tmp_path, monkeypatch):
    path, config = configuration(tmp_path)
    write_image(config.paths.source / "input.png")
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: broker(config, "refresh_fail"))
    assert cli.main(["watch", "--config", str(path)]) == 3
    with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
        assert db.execute("SELECT status FROM jobs").fetchall() == [("READY",)]
        assert db.execute("SELECT COUNT(*) FROM attempts").fetchone() == (0,)


@pytest.mark.parametrize("status,expected", [(401, JobStatus.FAILED), (408, JobStatus.AMBIGUOUS)])
def test_http_errors_not_refreshed_or_replayed(tmp_path, quiet_logger, status, expected):
    _, config = configuration(tmp_path)
    with broker(config) as session:
        session.start(37, deadline())
        runner = SubprocessAttemptRunner(target=partial(_mock_image_child, status=status))
        with Processor(config, quiet_logger, auth_session=session, runner=runner) as processor:
            source = write_image(config.paths.source / "input.png")
            if status == 401:
                with pytest.raises(AppError) as caught:
                    processor.process_path(source)
                assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
            else:
                assert processor.process_path(source).status == expected
            job = processor.state.list_jobs()[0]
            assert job.status == expected
            assert processor.state.attempt_count(job.job_id) == 1
    for file in config.paths.destination.rglob("*"):
        if file.is_file():
            assert TOKEN_PREFIX.encode() not in file.read_bytes()


def test_interactive_http401_stops_watch_exit3_without_retry(tmp_path, monkeypatch):
    path, config = configuration(tmp_path)
    for index in range(2):
        write_image(config.paths.source / f"input-{index}.png")
    monkeypatch.setattr(cli, "AuthenticationBroker", lambda *args: broker(config))
    monkeypatch.setattr(cli, "Processor", lambda cfg, logger, **kwargs: Processor(
        cfg, logger, runner=SubprocessAttemptRunner(
            target=partial(_mock_image_child, status=401),
        ), **kwargs,
    ))
    assert cli.main(["watch", "--config", str(path)]) == 3
    with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
        assert db.execute("SELECT state, error_code FROM attempts").fetchall() == [
            ("permanent", "AUTHENTICATION_FAILED"),
        ]
        assert sorted(row[0] for row in db.execute("SELECT status FROM jobs")) == ["FAILED", "READY"]


def test_expired_token_worker_fails_explicitly_before_sdk(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    class ExpiringRunner(SubprocessAttemptRunner):
        def run_authenticated(self, cfg, request, temp, shutdown, token):
            return super().run_authenticated(
                cfg, request, temp, shutdown, auth.AccessToken(token.token, int(time.time()) - 1),
            )
    with broker(config) as session:
        session.start(37, deadline())
        with Processor(
            config, quiet_logger, auth_session=session,
            runner=ExpiringRunner(target=_mock_image_child),
        ) as processor:
            with pytest.raises(AppError) as caught:
                processor.process_path(write_image(config.paths.source / "input.png"))
            assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
            job = processor.state.list_jobs()[0]
            assert job.status == JobStatus.FAILED
            assert processor.state.attempt_count(job.job_id) == 1


def test_cancel_refresh_preserves_ready_and_cleans_broker(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    with broker(config, "hang_refresh") as session:
        session.start(37, deadline())
        with Processor(config, quiet_logger, auth_session=session) as processor:
            event = threading.Event()
            job = processor._admit(
                write_image(config.paths.source / "input.png"), candidate=None, shutdown_event=event,
            )
            snapshot = processor.state.get_job(job.job_id)
            timer = threading.Timer(0.2, event.set)
            timer.start()
            try:
                with pytest.raises(AppError) as caught:
                    processor._dispatch_one(event)
                assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
                assert processor.state.get_job(job.job_id) == snapshot
                assert processor.state.attempt_count(job.job_id) == 0
            finally:
                timer.join()
    assert_no_children(before)


def test_cancel_image_cleans_both_children(tmp_path, quiet_logger):
    _, config = configuration(tmp_path)
    before = {child.pid for child in multiprocessing.active_children()}
    dispatched = multiprocessing.get_context("spawn").Event()
    event = threading.Event()
    def cancel():
        assert dispatched.wait(10)
        event.set()
    thread = threading.Thread(target=cancel)
    thread.start()
    try:
        with broker(config) as session:
            session.start(37, deadline())
            runner = SubprocessAttemptRunner(
                target=partial(_mock_image_child, hang=True), dispatch_event=dispatched,
            )
            with Processor(config, quiet_logger, auth_session=session, runner=runner) as processor:
                result = processor.process_path(
                    write_image(config.paths.source / "input.png"), shutdown_event=event,
                )
                assert result.status == JobStatus.AMBIGUOUS
    finally:
        thread.join(timeout=10)
    assert not thread.is_alive()
    assert_no_children(before)
