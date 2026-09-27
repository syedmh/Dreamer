from datetime import UTC, datetime
from types import SimpleNamespace

import httpx
import pytest
import yaml

from conftest import write_config, write_image
from tcfcomic.config import load_config, sanitized_config_summary
from tcfcomic.domain import AppError, ErrorCode, RetryableProviderError, SourceSnapshot, TransformRequest
from tcfcomic.processor import Processor
from tcfcomic.providers.openai import OpenAIProvider, _retry_after_seconds
from tcfcomic.providers.worker import InProcessAttemptRunner, WorkerResult


@pytest.mark.parametrize("value", [None, True, False, 0, -1, 1.5, 2.0, "2", 60001])
def test_requests_per_minute_strict_invalid(tmp_path, value):
    path, _ = write_config(tmp_path)
    data = yaml.safe_load(path.read_text())
    data["provider"]["requests_per_minute"] = value
    path.write_text(yaml.safe_dump(data))
    with pytest.raises(AppError) as caught:
        load_config(path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "requests_per_minute" in caught.value.safe_message


@pytest.mark.parametrize("value", [1, 2, 60000])
def test_pacing_config_optional_summary_and_identity_unchanged(tmp_path, quiet_logger, value):
    path, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    assert config.provider.requests_per_minute is None
    assert "requests_per_minute" not in sanitized_config_summary(config)
    with Processor(config, quiet_logger) as processor:
        identity = processor._request_identity()
    data = yaml.safe_load(path.read_text())
    data["provider"]["requests_per_minute"] = value
    data["retry"].update(max_attempts=3, initial_delay_seconds=5, max_delay_seconds=10)
    path.write_text(yaml.safe_dump(data))
    config = load_config(path)
    assert sanitized_config_summary(config)["requests_per_minute"] == value
    with Processor(config, quiet_logger) as processor:
        assert processor._request_identity() == identity


@pytest.mark.parametrize("headers,expected", [
    ({}, 0),
    ({"Retry-After": "30"}, 30),
    ({"retry-after-ms": "1250"}, 1.25),
    ({"retry-after-ms": "2500", "Retry-After": "90"}, 2.5),
    ({"retry-after-ms": "bad", "Retry-After": "31"}, 31),
    ({"retry-after-ms": "NaN", "Retry-After": "31"}, 31),
    ({"retry-after-ms": "-1", "Retry-After": "31"}, 31),
    ({"retry-after-ms": "86400001", "Retry-After": "31"}, 31),
    ({"Retry-After": "Tue, 01 Jan 2030 00:01:00 GMT"}, 60),
    ({"retry-after-ms": "1000", "Retry-After": "Tue, 01 Jan 2030 00:01:00 GMT"}, 1),
    ({"Retry-After": "Mon, 31 Dec 2029 23:59:59 GMT"}, 0),
    ({"Retry-After": "86400"}, 86400),
    ({"retry-after-ms": "86400000"}, 86400),
    ({"Retry-After": "86401"}, 0),
    ({"Retry-After": "1e999"}, 0),
    ({"Retry-After": "NaN"}, 0),
    ({"Retry-After": "-1"}, 0),
    ({"Retry-After": "infinity"}, 0),
    ({"Retry-After": "secret-header-invalid"}, 0),
    ({"Retry-After": "x" * 1000}, 0),
    ({"Retry-After": "Wed, 02 Jan 2030 00:01:00 GMT"}, 0),
    ({"Retry-After": "Tue, 01 Jan 2030 00:01:00"}, 0),
])
def test_retry_headers_priority_bounds_and_dates(headers, expected):
    assert _retry_after_seconds(
        httpx.Headers(headers), now=datetime(2030, 1, 1, tzinfo=UTC),
    ) == expected


@pytest.mark.parametrize("status,headers,expected", [
    (429, {}, 0),
    (429, {"retry-after-ms": "3500", "Retry-After": "20"}, 3.5),
    (429, {"Retry-After": "30"}, 30),
    (429, {"Retry-After": "NaN"}, 0),
    (409, {"Retry-After": "50"}, None),
    (408, {"Retry-After": "50"}, None),
    (500, {"Retry-After": "50"}, None),
    (401, {"Retry-After": "50"}, 50),
])
def test_http_error_metadata_crosses_provider_worker_boundary(tmp_path, status, headers, expected):
    from fresh_response import capture_response
    source = write_image(tmp_path / "input.png")
    evidence = capture_response(source, status, headers=headers,
                                body={"message": "raw-provider-body-must-not-leak"})
    class Provider:
        def transform(self, request, output):
            raise evidence
    provider = Provider()
    info = source.stat()
    request = TransformRequest(
        "a" * 32, SourceSnapshot(source, str(source), info.st_size, info.st_mtime_ns, "b" * 64, source),
        "offline", "offline",
    )
    result = InProcessAttemptRunner(provider).run(None, request, tmp_path / "result.tmp", None)
    assert result.retry_after_seconds == expected
    assert result.outcome == (
        "retryable" if status in {409, 429}
        else "ambiguous" if status in {408, 500} else "permanent"
    )
    assert "raw-provider-body" not in result.safe_message
    assert "Retry-After" not in result.safe_message
    assert f"HTTP {status}." in result.safe_message
    if status == 401:
        # Definitive Azure API-key denials now stop the invocation and retain cooldown.
        assert result.error_code == ErrorCode.AUTHENTICATION_FAILED
        assert "credential is valid for the configured resource" in result.safe_message


@pytest.mark.parametrize("value", [-1, 86401, 10**1000, float("nan"), float("inf"), "30", True])
def test_error_delay_contract_is_bounded(value):
    with pytest.raises(ValueError):
        RetryableProviderError(ErrorCode.PROVIDER_RETRYABLE, "safe", retry_after_seconds=value)
    with pytest.raises(ValueError):
        WorkerResult("retryable", retry_after_seconds=value)
