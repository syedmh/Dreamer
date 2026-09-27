"""The persisted parser must consume only exact writer evidence, not HTTP prose."""
from types import SimpleNamespace

import pytest

from test_retry_cda7_classifier import sdk, tmp_path, outcome
from conftest import write_image
from fresh_response import capture_response
from tcfcomic.domain import ErrorCode
from tcfcomic.providers.openai import (
    _RESPONSE_PREFIXES, _safe_http_error_details, recorded_retryable_response,
)


@pytest.mark.parametrize("status", [400, 408, 409, 429, 500, 503, 599])
@pytest.mark.parametrize("request_id", [None, "a" * 32, "12345678-1234-1234-1234-123456789abc"])
@pytest.mark.parametrize("code", list(_RESPONSE_PREFIXES))
def test_legacy_formatter_never_authorizes_roundtrip(status, request_id, code):
    details = _safe_http_error_details(SimpleNamespace(status_code=status, body={}, request_id=request_id))
    message = _RESPONSE_PREFIXES[code] + details
    assert recorded_retryable_response(message, code) is None


@pytest.mark.parametrize("status", [400, 408, 409, 429, 500, 503, 599])
@pytest.mark.parametrize("request_id", [None, "a" * 32, "12345678-1234-1234-1234-123456789abc"])
@pytest.mark.parametrize("enabled", [False, True])
def test_emitted_canonical_roundtrip(tmp_path, status, request_id, enabled):
    error = capture_response(write_image(tmp_path / "fresh.png"), status,
                             request_id=request_id, enabled=enabled)
    assert recorded_retryable_response(error.safe_message, error.code) == status
    for other_code in _RESPONSE_PREFIXES:
        if other_code != error.code:
            assert recorded_retryable_response(error.safe_message, other_code) is None


@pytest.mark.parametrize("body", [
    {"type": "moderation_blocked"}, {"type": "insufficient_quota"},
    {"type": "unknown"}, {"type": []}, [{"code": "moderation_blocked"}],
    "malformed", {"code": "content_filter"}, {"param": "prompt"},
], ids=["moderation-type", "quota-type", "unknown-type", "list-type",
        "list-body", "string-body", "moderation-code-control", "parameter-control"])
def test_actual_unsafe_diagnostic_never_authorizes_historical_recovery(sdk, body):
    provider, request = sdk.install(400, body)
    error = outcome(provider, request)
    # Deliberately consume the actual emitted error, even if immediate retry was unsafe.
    assert recorded_retryable_response(error.safe_message, error.code) is None


@pytest.mark.parametrize("mutation", [
    lambda s: s[:-1], lambda s: s + " extra prose",
    lambda s: s + "\n", lambda s: s.replace("HTTP", "http"),
    lambda s: s.replace("400", "401"), lambda s: s.replace("400", "0400"),
    lambda s: s + " Parameter prompt.",
    lambda s: s + " Structured response classification is unavailable.",
    lambda s: s.replace(" HTTP", " Request ID unsafe. HTTP"),
    lambda s: s.replace("HTTP 400.", "HTTP 400. Request ID req_bad."),
    lambda s: s[:s.index(" Check")], lambda s: s + " " * (2048 - len(s)),
    lambda s: s + " " * (2049 - len(s)),
], ids=["truncated", "extra", "newline", "case", "status-hint-mismatch", "padded-status",
        "parameter", "uncertain", "prefix", "invalid-id", "missing-hint", "2048", "2049"])
def test_closed_grammar_rejects_near_misses(tmp_path, mutation):
    error = capture_response(write_image(tmp_path / "fresh.png"))
    code, message = error.code, error.safe_message
    assert recorded_retryable_response(message, code) == 400
    assert recorded_retryable_response(mutation(message), code) is None


@pytest.mark.parametrize("value", [None, 400, [], {}, True, b"HTTP 400."])
def test_parser_wrong_type_denies(value):
    assert recorded_retryable_response(value, ErrorCode.PROVIDER_PERMANENT) is None


def test_unanswered_ambiguity_and_wrong_error_code_deny():
    assert recorded_retryable_response(
        _RESPONSE_PREFIXES[ErrorCode.PROVIDER_AMBIGUOUS], ErrorCode.PROVIDER_AMBIGUOUS) is None
    assert recorded_retryable_response(
        _RESPONSE_PREFIXES[ErrorCode.PROVIDER_RETRYABLE] + " HTTP 503.",
        ErrorCode.PROVIDER_PERMANENT) is None
