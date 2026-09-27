"""Independent security regressions: real SDK and synthetic intercepted HTTP.

The historical messages below deliberately remain UNVERSIONED. New evidence is
obtained from the running adapter, never from an assumed completeness marker.
"""
from __future__ import annotations

import base64
import io
import json

import httpx
import openai
import pytest
from openai import DefaultHttpxClient as RealDefaultHttpxClient

from test_retry_cda7_classifier import sdk, tmp_path, outcome
from tcfcomic.domain import ErrorCode
from tcfcomic.providers.openai import OpenAIProvider, recorded_retryable_response


@pytest.mark.parametrize("code,message", [
    (ErrorCode.PROVIDER_PERMANENT,
     "The provider rejected the request. HTTP 400. Check request parameters "
     "and model support for the image-edit API."),
    (ErrorCode.PROVIDER_RETRYABLE,
     "The provider is temporarily unavailable. HTTP 429."),
    (ErrorCode.PROVIDER_AMBIGUOUS,
     "The provider request ended with an uncertain dispatch result. HTTP 503."),
])
def test_unversioned_diagnostic_never_authorizes_history(code, message):
    assert recorded_retryable_response(message, code) is None


@pytest.mark.parametrize("raw", [
    b'{"code":"moderation_blocked","code":null,"error":{}}',
    b'{"error":{"type":"moderation_blocked"},"error":{}}',
    b'{"error":{"innererror":{"code":"moderation_blocked","code":null}}}',
    b'{"error":{"type":"moderation_blocked","type":null}}',
], ids=["root-code", "root-error", "nested-code", "nested-type"])
def test_duplicate_json_members_never_authorize_retry(sdk, raw):
    provider, request = sdk.install(handler=lambda req: httpx.Response(
        400, content=raw, headers={"content-type": "application/json"}))
    error = outcome(provider, request)
    historical_status = recorded_retryable_response(error.safe_message, error.code)
    print("RED_OBSERVATION " + json.dumps({
        "case": "duplicate-json", "error_code": error.code.value,
        "historical_status": historical_status, "calls": len(sdk.calls),
    }, sort_keys=True))
    assert len(sdk.calls) == 1
    assert request.prompt not in error.safe_message
    assert (error.code, historical_status) == (ErrorCode.PROVIDER_PERMANENT, None)


@pytest.mark.parametrize("azure", [False, True], ids=["public", "azure"])
@pytest.mark.parametrize("field", ["code", "type"])
@pytest.mark.parametrize("code", ["moderation_blocked", "content_policy_violation"])
def test_default_429_moderation_is_terminal(sdk, azure, field, code):
    # Never-retry-content-policy outranks the default 429 compatibility rule.
    provider, request = sdk.install(
        429, {"error": {field: code}}, enabled=False, azure=azure)
    error = outcome(provider, request)
    assert len(sdk.calls) == 1
    assert code in error.safe_message
    assert recorded_retryable_response(error.safe_message, error.code) is None
    assert error.code == ErrorCode.PROVIDER_PERMANENT


@pytest.mark.parametrize("azure", [False, True], ids=["public", "azure-control"])
def test_307_never_forwards_image_or_prompt_with_actual_sdk_defaults(sdk, monkeypatch, azure):
    calls = []

    def dispatch(request):
        request.read()
        calls.append(request)
        if request.url.host != "redirect.invalid":
            return httpx.Response(
                307, headers={"location": "https://redirect.invalid/collect"})
        return httpx.Response(400, json={})

    initial, request = sdk.install(azure=azure, enabled=azure)
    initial._client.close()
    # A bare httpx.Client has different redirect defaults and masks this bug.
    monkeypatch.setattr(openai, "DefaultHttpxClient", lambda **kwargs: RealDefaultHttpxClient(
        transport=httpx.MockTransport(dispatch), **kwargs))
    provider = OpenAIProvider(
        2, azure_endpoint="https://offline.openai.azure.com" if azure else None,
        azure_response_retries=azure,
    )
    try:
        outcome(provider, request)
    finally:
        provider._client.close()
    forwarded = [call for call in calls if call.url.host == "redirect.invalid"]
    copied = any(
        sdk.source.read_bytes() in call.content
        and request.prompt.encode() in call.content for call in forwarded
    )
    print("RED_OBSERVATION " + json.dumps({
        "case": "307", "azure": azure, "calls": len(calls),
        "image_and_prompt_forwarded": copied,
        "authorization_forwarded": any("authorization" in call.headers for call in forwarded),
    }, sort_keys=True))
    assert not forwarded, "A 307 must not submit the original multipart body to another origin"
    assert len(calls) == 1


@pytest.mark.parametrize("identifier", [
    "SYNTHETIC PRIVATE RESPONSE PROSE",
    "synthetic-person@example.invalid",
    "SYNTHETIC\nPRIVATE\tPROSE",
    "a" * 129,
    "",
], ids=["private-prose", "email", "controls", "overlength", "empty"])
def test_invalid_success_id_is_omitted_at_adapter_boundary(sdk, identifier):
    provider, request = sdk.install(handler=lambda req: httpx.Response(200, json={
        "id": identifier,
        "data": [{"b64_json": base64.b64encode(sdk.source.read_bytes()).decode("ascii")}],
    }))
    output = io.BytesIO()
    result = provider.transform(request, output)
    assert output.getvalue() == sdk.source.read_bytes()
    assert len(sdk.calls) == 1
    assert result.provider_request_id is None


@pytest.mark.parametrize("identifier", [
    "a" * 32, "01234567-89ab-cdef-0123-456789abcdef", "req_" + "b" * 32,
], ids=["hex", "uuid", "request-prefix"])
def test_valid_bounded_success_id_is_retained(sdk, identifier):
    provider, request = sdk.install(handler=lambda req: httpx.Response(200, json={
        "id": identifier,
        "data": [{"b64_json": base64.b64encode(sdk.source.read_bytes()).decode("ascii")}],
    }))
    result = provider.transform(request, io.BytesIO())
    assert len(sdk.calls) == 1
    assert result.provider_request_id == identifier


def test_current_complete_unclassified_400_remains_eligible(sdk):
    provider, request = sdk.install(400, {})
    error = outcome(provider, request)
    assert len(sdk.calls) == 1
    assert error.code == ErrorCode.PROVIDER_RETRYABLE
    assert recorded_retryable_response(error.safe_message, error.code) == 400
