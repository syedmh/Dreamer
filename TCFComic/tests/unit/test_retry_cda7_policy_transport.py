"""Policy boundaries and actual spawn -> worker -> pinned SDK offline contract."""
from __future__ import annotations

import io
import json
import os
import socket
import time
from dataclasses import replace
from datetime import UTC, datetime
from unittest.mock import patch

import httpx
import openai
import pytest
import yaml

from conftest import write_config, write_image
from test_retry_cda7_classifier import sdk, tmp_path, request_for, outcome, ENDPOINT
from tcfcomic.authentication import AccessToken
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode
from tcfcomic.providers.openai import _retry_after_seconds
from tcfcomic.providers.worker import SubprocessAttemptRunner, _execute_config


def offline_sdk_child(config, request, temp_path, result_queue, access_token=None):
    """Runs in a real spawned interpreter; never inherits provider/HTTP transport."""
    for key in tuple(os.environ):
        if key.upper().startswith(("OPENAI_", "AZURE_OPENAI_")):
            del os.environ[key]
    os.environ["OPENAI_API_KEY"] = "dummy-public"
    os.environ["AZURE_OPENAI_API_KEY"] = "dummy-azure"
    calls, settings, clients = [], [], []

    def deny(*args, **kwargs):
        raise AssertionError("network forbidden in synthetic child")

    def dispatch(http_request):
        http_request.read()
        calls.append({
            "url": str(http_request.url), "method": http_request.method,
            "api-key": http_request.headers.get("api-key"),
            "authorization": http_request.headers.get("authorization"),
        })
        return httpx.Response(400, json={})

    def client(**kwargs):
        settings.append(kwargs)
        value = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
        clients.append(value)
        return value

    with patch.object(socket.socket, "connect", deny), patch.object(socket, "getaddrinfo", deny), \
            patch.object(openai, "DefaultHttpxClient", client):
        result = _execute_config(config, request, temp_path, access_token)
    temp_path.with_suffix(".trace").write_text(json.dumps({
        "pid": os.getpid(), "calls": calls, "settings": settings,
        "request_flag": request.azure_response_retries,
    }), encoding="utf-8")
    for client in clients:
        client.close()
    result_queue.put(result)


@pytest.mark.parametrize("enabled", [False, True])
@pytest.mark.parametrize("mode", ["azure-key", "azure-token", "public"])
def test_spawned_worker_policy_and_auth_contract(tmp_path, enabled, mode):
    azure = mode != "public"
    _, config = write_config(tmp_path, provider="azure_openai" if azure else "openai",
                             endpoint=ENDPOINT if azure else None)
    token = AccessToken("dummy-synthetic-token", int(time.time()) + 3600) if mode == "azure-token" else None
    provider = replace(config.provider, authentication="interactive" if token else "api_key")
    source = write_image(config.paths.source / "in.png")
    request = request_for(source, azure_response_retries=enabled)
    temp = config.paths.destination / "attempt.tmp"
    runner = SubprocessAttemptRunner(target=offline_sdk_child)
    if token:
        result = runner.run_authenticated(provider, request, temp, None, token)
    else:
        result = runner.run(provider, request, temp, None)
    trace = json.loads(temp.with_suffix(".trace").read_text())
    assert trace["pid"] != os.getpid()
    assert trace["request_flag"] is enabled
    assert len(trace["calls"]) == 1
    assert trace["settings"][0]["trust_env"] is False
    call = trace["calls"][0]
    assert call["method"] == "POST"
    if azure:
        assert trace["settings"][0]["follow_redirects"] is False
        assert call["url"].startswith(ENDPOINT + "/openai/v1/images/edits?")
        assert "api-version=" in call["url"]
        assert call["api-key"] == (None if token else "dummy-azure")
        assert call["authorization"] == ("Bearer dummy-synthetic-token" if token else None)
    else:
        assert call["url"] == "https://api.openai.com/v1/images/edits"
        assert call["authorization"] == "Bearer dummy-public"
    assert result.error_code == (
        ErrorCode.PROVIDER_RETRYABLE if azure and enabled else ErrorCode.PROVIDER_PERMANENT)
    assert "HTTP 400." in result.safe_message
    assert not temp.exists()


@pytest.mark.parametrize("status", [400, 429, 503, 307])
def test_sdk_never_retries_or_follows_redirects(sdk, status):
    def response(request):
        return httpx.Response(status, json={}, headers={"location": "https://must-not-follow.invalid/image"})
    provider, request = sdk.install(handler=response)
    outcome(provider, request)
    assert provider._client.max_retries == 0
    assert sdk.settings == [{"trust_env": False, "follow_redirects": False}]
    assert len(sdk.calls) == 1


@pytest.mark.parametrize("value", ["omitted", False, True, None, 0, 1, "true", [], {}])
@pytest.mark.parametrize("provider", ["fake", "openai", "azure_openai"])
def test_exact_boolean_and_azure_only_config(tmp_path, value, provider):
    path, _ = write_config(tmp_path, provider=provider,
                           endpoint=ENDPOINT if provider == "azure_openai" else None)
    data = yaml.safe_load(path.read_text())
    if value != "omitted":
        data["retry"]["azure_response_retries"] = value
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    valid = value == "omitted" or type(value) is bool and (not value or provider == "azure_openai")
    if valid:
        config = load_config(path, prepare_paths=False)
        assert config.retry.azure_response_retries is (value is True)
    else:
        with pytest.raises(AppError) as error:
            load_config(path, prepare_paths=False)
        assert error.value.code == ErrorCode.CONFIG_INVALID


@pytest.mark.parametrize("attempts", [0, 1, 4, 5])
def test_opted_in_budget_boundaries(tmp_path, attempts):
    path, _ = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text())
    data["retry"].update(azure_response_retries=True, max_attempts=attempts)
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    if attempts in {1, 4}:
        assert load_config(path, prepare_paths=False).retry.max_attempts == attempts
    else:
        with pytest.raises(AppError) as error:
            load_config(path, prepare_paths=False)
        assert error.value.code == ErrorCode.CONFIG_INVALID


@pytest.mark.parametrize("headers,expected", [
    ({"retry-after-ms": "1500", "retry-after": "8"}, 1.5),
    ({"retry-after-ms": "invalid", "retry-after": "8"}, 8),
    ({"retry-after-ms": "-1", "retry-after": "8"}, 8),
    ({"retry-after": "0"}, 0), ({"retry-after": "86400"}, 86400),
    ({"retry-after": "86401"}, None), ({"retry-after": "-1"}, None),
    ({"retry-after": "nan"}, None), ({"retry-after": "inf"}, None),
    ({"retry-after": "Wed, 01 Jan 2031 00:01:00 GMT"}, 60),
    ({"retry-after": "Tue, 01 Jan 2030 00:00:00 GMT"}, 0),
    ({"retry-after": "x" * 129}, None), ({}, None),
])
def test_bounded_retry_after_preference_and_fallback(headers, expected):
    assert _retry_after_seconds(headers, now=datetime(2031, 1, 1, tzinfo=UTC), default=None) == expected


@pytest.mark.parametrize("enabled", [False, True])
@pytest.mark.parametrize("interactive", [False, True])
def test_processor_config_to_request_to_spawned_sdk(tmp_path, quiet_logger, enabled, interactive):
    import importlib.util
    import sys
    from pathlib import Path
    if "test_retry_cda7_pacing" not in sys.modules:
        spec = importlib.util.spec_from_file_location(
            "test_retry_cda7_pacing",
            Path(__file__).resolve().parents[1] / "integration/test_retry_cda7_pacing.py")
        helper = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = helper
        spec.loader.exec_module(helper)
    from test_retry_cda7_pacing import Clock, config_for
    from tcfcomic.processor import Processor
    config = config_for(tmp_path, attempts=1, enabled=enabled)
    timer = Clock()
    source = write_image(config.paths.source / "original.png")
    token = AccessToken("dummy-synthetic-token", int(time.time()) + 3600)
    class Auth:
        started = True
        calls = 0
        def acquire(self, *args):
            self.calls += 1
            return token
    auth = Auth()
    if interactive:
        config = replace(config, provider=replace(config.provider, authentication="interactive"))
    with Processor(config, quiet_logger, clock=timer.clock(),
                   runner=SubprocessAttemptRunner(target=offline_sdk_child),
                   **({"auth_session": auth} if interactive else {})) as processor:
        result = processor.process_path(source)
        assert result.attempts == 1
        assert result.error_code == (ErrorCode.PROVIDER_RETRYABLE if enabled else ErrorCode.PROVIDER_PERMANENT)
    trace_path, = config.paths.destination.glob("*.trace")
    trace = json.loads(trace_path.read_text())
    assert trace["request_flag"] is enabled
    assert len(trace["calls"]) == 1 and trace["pid"] != os.getpid()
    assert auth.calls == (1 if interactive else 0)
