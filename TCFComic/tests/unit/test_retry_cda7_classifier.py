"""Independent safe-contract regressions; real pinned SDK, intercepted HTTP only."""
from __future__ import annotations

import hashlib
import io
from types import SimpleNamespace

import httpx
import openai
import pytest

from conftest import write_image
from tcfcomic.domain import AppError, ErrorCode, SourceSnapshot, TransformRequest
from tcfcomic.providers.openai import (
    OpenAIProvider, _http_fields, _safe_http_error_details, recorded_retryable_response,
)

SECRET = "PRIVATE-PROVIDER-PROSE-cda7"
ENDPOINT = "https://offline.openai.azure.com"


@pytest.fixture
def tmp_path(tmp_path_factory):
    """Avoid Windows audit-path confounding, including long parametrized node names."""
    root = tmp_path_factory.mktemp("f")
    assert len(str(root)) < 90
    return root


def request_for(path, **kwargs):
    info = path.stat()
    return TransformRequest(
        "a" * 32,
        SourceSnapshot(path, str(path), info.st_size, info.st_mtime_ns,
                       hashlib.sha256(path.read_bytes()).hexdigest(), path),
        "synthetic private prompt", "synthetic-deployment", **kwargs,
    )


@pytest.fixture
def sdk(monkeypatch, tmp_path):
    clients, calls, settings = [], [], []
    source = write_image(tmp_path / "in.png")

    def install(status=400, body=None, *, enabled=True, azure=True, handler=None, token=None):
        def dispatch(request):
            request.read()
            calls.append(request)
            if handler is not None:
                return handler(request)
            return httpx.Response(status, json=body, headers={"x-request-id": "a" * 32})

        def client(**kwargs):
            settings.append(kwargs)
            result = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
            clients.append(result)
            return result

        monkeypatch.setattr(openai, "DefaultHttpxClient", client)
        monkeypatch.setenv("AZURE_OPENAI_API_KEY", "dummy-azure-only")
        monkeypatch.setenv("OPENAI_API_KEY", "dummy-public-only")
        provider = OpenAIProvider(
            2, azure_endpoint=ENDPOINT if azure else None,
            access_token=token, azure_response_retries=enabled,
        )
        return provider, request_for(source, azure_response_retries=enabled)

    yield SimpleNamespace(install=install, calls=calls, settings=settings, source=source)
    for client in clients:
        client.close()


def outcome(provider, request):
    with pytest.raises(AppError) as raised:
        provider.transform(request, io.BytesIO())
    assert SECRET not in raised.value.safe_message
    return raised.value


def located(where, field, value):
    node = {field: value, "message": SECRET}
    if where == "root":
        return node
    if where == "error":
        return {"error": node}
    if where.startswith("error."):
        return {"error": {where.split(".")[1]: node}}
    return {where: node}


@pytest.mark.parametrize("enabled", [False, True])
@pytest.mark.parametrize("status", [400, 408, 409, 429, 500, 503, 599, 401, 403, 404, 405, 413, 422, 499])
def test_real_sdk_status_policy_controls(sdk, enabled, status):
    provider, request = sdk.install(status, {}, enabled=enabled)
    error = outcome(provider, request)
    expected = ErrorCode.PROVIDER_PERMANENT
    if status in {409, 429} or (enabled and (status in {400, 408} or status >= 500)):
        expected = ErrorCode.PROVIDER_RETRYABLE
    elif status == 408 or status >= 500:
        expected = ErrorCode.PROVIDER_AMBIGUOUS
    elif status in {401, 403}:
        expected = ErrorCode.AUTHENTICATION_FAILED
    assert error.code == expected
    assert len(sdk.calls) == 1


@pytest.mark.parametrize("where", ["root", "error", "innererror", "inner_error", "error.innererror", "error.inner_error"])
@pytest.mark.parametrize("field", ["code", "type"])
@pytest.mark.parametrize("status,value", [
    (400, "moderation_blocked"), (429, "insufficient_quota"),
    (503, "AuthenticationError"), (409, "PermissionDenied"),
    (500, "invalid_parameter"), (408, "DeploymentNotFound"),
])
def test_terminal_classification_all_bounded_locations(sdk, where, field, status, value):
    provider, request = sdk.install(status, located(where, field, value))
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_PERMANENT
    assert f"Provider code {value}." in error.safe_message
    assert recorded_retryable_response(error.safe_message, error.code) is None
    assert len(sdk.calls) == 1


@pytest.mark.parametrize("body", [
    {"type": "unrecognized"}, {"type": 7}, {"type": {}}, {"type": []},
    {"code": False}, {"param": 0}, {"param": "private-unknown"},
    [{"code": "moderation_blocked"}], "malformed", 1, True,
    {"error": []}, {"error": "invalid"}, {"innererror": 4},
    {"error": {"innererror": {"innererror": {"type": "moderation_blocked"}}}},
], ids=["unknown-type", "numeric-type", "dict-type", "list-type", "bool-code",
        "numeric-param", "unknown-param", "list-body", "string-body", "int-body",
        "bool-body", "list-error", "string-error", "numeric-inner", "too-deep"])
def test_uninspectable_response_is_terminal_and_uncertain(sdk, body):
    provider, request = sdk.install(400, body)
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_PERMANENT
    assert "Structured response classification is unavailable." in error.safe_message
    assert recorded_retryable_response(error.safe_message, error.code) is None


@pytest.mark.parametrize("field", ["code", "type", "param"])
def test_null_optional_fields_are_absent(sdk, field):
    provider, request = sdk.install(400, {field: None})
    assert outcome(provider, request).code == ErrorCode.PROVIDER_RETRYABLE


@pytest.mark.parametrize("field", ["code", "type"])
@pytest.mark.parametrize("status", [400, 409, 429, 503])
def test_known_transient_is_not_unclassified_400(sdk, field, status):
    provider, request = sdk.install(status, {field: "ServiceUnavailable"})
    error = outcome(provider, request)
    assert error.code == (ErrorCode.PROVIDER_PERMANENT if status == 400 else ErrorCode.PROVIDER_RETRYABLE)
    assert "Provider code ServiceUnavailable." in error.safe_message


def test_terminal_precedence_over_transient_and_unknown(sdk):
    provider, request = sdk.install(503, {
        "code": "ServiceUnavailable", "type": "unknown",
        "error": {"inner_error": {"type": "moderation_blocked"}},
    })
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_PERMANENT
    assert "Provider code moderation_blocked." in error.safe_message
    assert "Structured response classification is unavailable." in error.safe_message


@pytest.mark.parametrize("field", ["body", "code", "type", "param"])
def test_raising_classification_property_is_nonthrowing_uncertainty(field):
    def unreadable(self):
        raise RuntimeError(SECRET)
    cls = type("Unreadable", (Exception,), {field: property(unreadable)})
    error = cls()
    error.status_code = 404
    details = _safe_http_error_details(error)
    assert details == (
        " HTTP 404. Check the configured resource, deployment name, and API route."
        " Structured response classification is unavailable."
    )
    assert SECRET not in details


@pytest.mark.parametrize("status", [None, "404", True, 399, 600])
def test_invalid_status_never_inspects_body(status):
    class Unreadable(Exception):
        @property
        def body(self):
            pytest.fail("invalid-status formatter inspected body")
    error = Unreadable()
    error.status_code = status
    assert _safe_http_error_details(error) == ""


def test_mapping_subclass_body_is_not_trusted():
    class NotPlain(dict):
        def get(self, *args):
            pytest.fail("arbitrary mapping method invoked")
    assert _http_fields(SimpleNamespace(body=NotPlain())).uncertain


@pytest.mark.parametrize("cancel", [KeyboardInterrupt, SystemExit])
def test_classification_does_not_swallow_process_cancellation(cancel):
    class Cancel(Exception):
        @property
        def body(self):
            raise cancel()
    with pytest.raises(cancel):
        _http_fields(Cancel())


def test_one_classification_snapshot_prevents_contradictory_retry(sdk, monkeypatch):
    provider, request = sdk.install()

    class Changing(openai.APIStatusError):
        reads = 0

        @property
        def body(self):
            self.reads += 1
            return {"code": "moderation_blocked"} if self.reads == 1 else {}

        @body.setter
        def body(self, value):
            pass

    exc = Changing(SECRET, response=httpx.Response(
        400, request=httpx.Request("POST", ENDPOINT)), body={})
    exc.reads = 0
    def fail(**kwargs):
        raise exc
    monkeypatch.setattr(provider._client.images, "edit", fail)
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_PERMANENT
    assert "Provider code moderation_blocked." in error.safe_message
    assert exc.reads == 1


def test_status_impostor_does_not_authorize_expanded_retry(sdk, monkeypatch):
    provider, request = sdk.install()
    class Impostor(Exception):
        status_code = 400
        body = {}
    def fail(**kwargs):
        raise Impostor(SECRET)
    monkeypatch.setattr(provider._client.images, "edit", fail)
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_AMBIGUOUS
    assert recorded_retryable_response(error.safe_message, error.code) is None


@pytest.mark.parametrize("failure", [httpx.ReadTimeout, httpx.ReadError, httpx.RemoteProtocolError])
def test_no_response_never_retries(sdk, failure):
    def fail(request):
        raise failure(SECRET, request=request)
    provider, request = sdk.install(handler=fail)
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_AMBIGUOUS
    assert recorded_retryable_response(error.safe_message, error.code) is None
    assert len(sdk.calls) == 1


@pytest.mark.parametrize("where", ["root", "error", "innererror", "inner_error", "error.innererror", "error.inner_error"])
def test_known_parameter_at_every_bounded_location_denies(sdk, where):
    provider, request = sdk.install(503, located(where, "param", "image[]"))
    error = outcome(provider, request)
    assert error.code == ErrorCode.PROVIDER_PERMANENT
    assert "Parameter image[]." in error.safe_message
    assert recorded_retryable_response(error.safe_message, error.code) is None


@pytest.mark.parametrize("field", ["code", "type"])
def test_sdk_attribute_terminal_evidence_without_body(field):
    error = SimpleNamespace(status_code=400, body=None, **{field: "moderation_blocked"})
    fields = _http_fields(error)
    assert "moderation_blocked" in fields.codes
    assert "Provider code moderation_blocked." in _safe_http_error_details(error)


@pytest.mark.parametrize("body", [None, {}])
def test_absent_body_unclassified_positive_control(body):
    fields = _http_fields(SimpleNamespace(body=body))
    assert not fields.codes and not fields.parameters and not fields.uncertain


def test_unreadable_body_does_not_erase_known_terminal_attribute():
    class Unreadable(Exception):
        status_code = 400
        code = "moderation_blocked"
        @property
        def body(self):
            raise RuntimeError(SECRET)
    details = _safe_http_error_details(Unreadable())
    assert "Provider code moderation_blocked." in details
    assert "Structured response classification is unavailable." in details
    assert SECRET not in details
