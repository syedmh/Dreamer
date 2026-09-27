"""Closed evidence grammar and bounded real-response writer regressions."""
import io
from types import SimpleNamespace

import httpx
import openai
import pytest

from test_retry_cda7_classifier import sdk, tmp_path
from tcfcomic.domain import AppError, ErrorCode
from tcfcomic.providers.openai import (
    _COMPLETE_RESPONSE, _ERROR_PARAMETERS, _HTTP_STATUS_HINTS,
    _HttpFields, _PROVIDER_CODE_DIAGNOSTICS, _RESPONSE_PREFIXES,
    _format_http_error_details, _safe_http_error_details, recorded_retryable_response,
)
from tcfcomic.redaction import sanitize_text


@pytest.mark.parametrize("content", [b"null", b"[]", b"1", b"true", b'"text"'])
def test_nonobject_json_is_not_complete_absence(sdk, content):
    provider, request = sdk.install(handler=lambda req: httpx.Response(429, content=content))
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert _COMPLETE_RESPONSE not in caught.value.safe_message
    assert recorded_retryable_response(caught.value.safe_message, caught.value.code) is None


def test_empty_actual_response_body_is_complete_absence(sdk):
    provider, request = sdk.install(handler=lambda req: httpx.Response(429, content=b""))
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    assert caught.value.code == ErrorCode.PROVIDER_RETRYABLE
    assert recorded_retryable_response(caught.value.safe_message, caught.value.code) == 429


def test_sdk_body_and_original_envelope_are_both_classification_evidence(sdk, monkeypatch):
    provider, request = sdk.install()
    response = httpx.Response(
        429, json={}, request=httpx.Request("POST", "https://offline.invalid"))
    error = openai.APIStatusError(
        "offline", response=response,
        body={"innererror": {"code": "moderation_blocked"}},
    )
    def reject(**kwargs):
        raise error
    monkeypatch.setattr(provider._client.images, "edit", reject)
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert "Provider code moderation_blocked." in caught.value.safe_message
    assert recorded_retryable_response(caught.value.safe_message, caught.value.code) is None


def test_formatter_cannot_attest_status_shaped_object():
    details = _safe_http_error_details(SimpleNamespace(status_code=400, body={}))
    assert _COMPLETE_RESPONSE not in details
    message = _RESPONSE_PREFIXES[ErrorCode.PROVIDER_PERMANENT] + details
    assert recorded_retryable_response(message, ErrorCode.PROVIDER_PERMANENT) is None


@pytest.mark.parametrize("mutate", [
    lambda s: s.replace("v1 complete.", "v2 complete."),
    lambda s: s + _COMPLETE_RESPONSE,
    lambda s: s + " ",
    lambda s: _COMPLETE_RESPONSE + s,
    lambda s: s.replace(_COMPLETE_RESPONSE, ""),
    lambda s: s[:-1],
])
def test_actual_versioned_writer_rejects_marker_mutations(sdk, mutate):
    provider, request = sdk.install(400, {})
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    error = caught.value
    assert recorded_retryable_response(error.safe_message, error.code) == 400
    assert recorded_retryable_response(mutate(error.safe_message), error.code) is None


def test_all_static_diagnostics_survive_persistence_bound_byte_for_byte():
    # Exercise the full Cartesian formatting bound without issuing even mock HTTP.
    # These synthetic formatter strings are never passed to any replay authority.
    longest = 0
    exc = SimpleNamespace(request_id="req_" + "a" * 32, response=None)
    for prefix in _RESPONSE_PREFIXES.values():
        for status in (*_HTTP_STATUS_HINTS, 429, 599):
            for code in _PROVIDER_CODE_DIAGNOSTICS:
                for parameter in _ERROR_PARAMETERS:
                    fields = _HttpFields((code,), (parameter,), False, True)
                    message = prefix + _format_http_error_details(exc, status, fields) + _COMPLETE_RESPONSE
                    longest = max(longest, len(message))
                    assert sanitize_text(message) == message
    assert longest < 500


@pytest.mark.parametrize("cause", [httpx.ReadTimeout, httpx.WriteTimeout, httpx.ReadError, httpx.WriteError])
def test_possible_send_transport_never_becomes_not_sent(sdk, cause):
    def handler(request):
        # A nested connect failure cannot launder the direct read/write cause.
        try:
            raise httpx.ConnectError("offline nested", request=request)
        except httpx.ConnectError as nested:
            raise cause("offline direct", request=request) from nested
    provider, request = sdk.install(handler=handler)
    with pytest.raises(AppError) as caught:
        provider.transform(request, io.BytesIO())
    assert caught.value.code == ErrorCode.PROVIDER_AMBIGUOUS
    assert "not-sent" not in caught.value.safe_message
    assert recorded_retryable_response(caught.value.safe_message, caught.value.code) is None
