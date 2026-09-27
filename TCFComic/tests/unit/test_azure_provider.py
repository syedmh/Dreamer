from __future__ import annotations

import base64
import hashlib
import io
import json
import logging
import sqlite3
from dataclasses import replace
from datetime import UTC, datetime
from email import policy
from email.message import Message
from email.parser import BytesParser
from pathlib import Path

import httpx
import openai
import pytest
import yaml
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.authentication import AccessToken
from tcfcomic.azure_endpoint import canonical_azure_endpoint
from tcfcomic.config import (
    DEFAULT_PROMPT,
    LimitsConfig,
    ProviderConfig,
    load_config,
    require_provider_credentials,
)
from tcfcomic.domain import (
    AmbiguousProviderError,
    AppError,
    ErrorCode,
    FailureRecord,
    PermanentProviderError,
    RetryableProviderError,
    SourceSnapshot,
    TransformRequest,
)
from tcfcomic.image_io import validate_input
from tcfcomic.logging_setup import configure_logging, log_event
from tcfcomic.processor import Processor
from tcfcomic.providers.openai import (
    OpenAIProvider,
    _BoundedInputBuffer,
    _safe_http_error_details,
    recorded_retryable_response,
)
from tcfcomic.providers.worker import InProcessAttemptRunner, _execute_config, _provider_from_config
from tcfcomic.quarantine import write_failure_record
from tcfcomic.redaction import sanitize_text

ENDPOINT = "https://sweepertestai.openai.azure.com"
DEPLOYMENT = "tcfcomic-gpt-image-2"


def request_for(path: Path, *, limit: int = 1_048_576) -> TransformRequest:
    validate_input(path, LimitsConfig(limit, limit, 1000, 1000, 1_000_000))
    info = path.stat()
    return TransformRequest(
        "a" * 32,
        SourceSnapshot(
            path, str(path), info.st_size, info.st_mtime_ns,
            hashlib.sha256(path.read_bytes()).hexdigest(), path,
        ),
        DEFAULT_PROMPT,
        DEPLOYMENT,
        max_input_bytes=limit,
    )


def multipart(request: httpx.Request) -> dict[str, Message]:
    message = BytesParser(policy=policy.default).parsebytes(
        f"Content-Type: {request.headers['content-type']}\r\n\r\n".encode()
        + request.content
    )
    return {
        part.get_param("name", header="content-disposition"): part
        for part in message.iter_parts()
    }


@pytest.fixture
def azure_http(monkeypatch):
    clients = []
    captured = []
    settings = []
    with io.BytesIO() as output:
        with Image.new("RGB", (2, 2), (1, 2, 3)) as image:
            image.save(output, format="PNG")
        png = output.getvalue()

    def install(handler=None):
        def dispatch(request):
            request.read()
            captured.append(request)
            if handler:
                return handler(request)
            return httpx.Response(
                200,
                json={"id": "a" * 32, "data": [
                    {"b64_json": base64.b64encode(png).decode("ascii")}
                ]},
            )

        def client(**kwargs):
            settings.append(kwargs)
            result = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
            clients.append(result)
            return result

        monkeypatch.setattr(openai, "DefaultHttpxClient", client)
        monkeypatch.setenv("AZURE_OPENAI_API_KEY", "  azure-test-key  ")
        monkeypatch.setenv("OPENAI_API_KEY", "public-unused-key")
        return captured, settings, png

    yield install
    for client in clients:
        client.close()


@pytest.mark.parametrize(
    "endpoint",
    [ENDPOINT, ENDPOINT + "/", "HTTPS://SWEEPERTESTAI.OPENAI.AZURE.COM/"],
)
def test_endpoint_canonicalization(endpoint):
    assert canonical_azure_endpoint(endpoint) == ENDPOINT


BAD_ENDPOINTS = [
    None, 123, "", " " + ENDPOINT, ENDPOINT + " ", ENDPOINT + "\n",
    "\t" + ENDPOINT, ENDPOINT + "\r", ENDPOINT + "\x00", ENDPOINT + "\x7f",
    "http://sweepertestai.openai.azure.com",
    "https://user@sweepertestai.openai.azure.com",
    "https://user:password@sweepertestai.openai.azure.com",
    ENDPOINT + ":443", ENDPOINT + ":",
    ENDPOINT + "?", ENDPOINT + "?api-version=preview", ENDPOINT + "#",
    ENDPOINT + "#fragment", ENDPOINT + "/openai/v1", ENDPOINT + "//",
    ENDPOINT + "/.", ENDPOINT + "/..", ENDPOINT + "/%2e",
    ENDPOINT + "\\", ENDPOINT + "\\@evil.example",
    "https://sweepertestai%2eopenai.azure.com",
    "https://sweepertestai.openai.azure.com.evil.example",
    "https://sweepertestai.openai.azure.com@evil.example",
    "https://sweepertestai.openai.azure.com.",
    "https://sweepertestai\u3002openai.azure.com",
    "https://\u017fweepertestai.openai.azure.com",
    "https://sweepertesta\u0130.openai.azure.com",
    "https://sweepertestai.openai.azur\u0435.com",
    "https://sub.sweepertestai.openai.azure.com",
    "https://-resource.openai.azure.com",
    "https://resource-.openai.azure.com",
    "https://resource_name.openai.azure.com",
    "https://" + ("a" * 64) + ".openai.azure.com",
    "https://.openai.azure.com", "https://127.0.0.1",
]


@pytest.mark.parametrize("endpoint", BAD_ENDPOINTS)
def test_endpoint_rejected_in_config_and_provider_before_sdk(
    tmp_path, monkeypatch, endpoint
):
    path, _ = write_config(tmp_path)
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    data["provider"].update(name="azure_openai", endpoint=endpoint)
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    calls = []
    monkeypatch.setattr(openai, "OpenAI", lambda **kwargs: calls.append(kwargs))
    with pytest.raises(AppError) as caught:
        load_config(path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "provider.endpoint" in caught.value.safe_message
    if endpoint is not None:
        with pytest.raises(PermanentProviderError) as provider_error:
            OpenAIProvider(2, azure_endpoint=endpoint)
        assert provider_error.value.code == ErrorCode.PROVIDER_PERMANENT
        assert provider_error.value.safe_message == (
            "expected an HTTPS Azure resource root endpoint "
            "(https://RESOURCE.openai.azure.com)"
        )
    else:
        with pytest.raises(PermanentProviderError):
            _provider_from_config(ProviderConfig("azure_openai", DEPLOYMENT, "p", 2))
    assert calls == []


@pytest.mark.parametrize("missing", ["endpoint", "model"])
def test_azure_requires_explicit_endpoint_and_deployment(tmp_path, missing):
    path, _ = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    data["provider"].pop(missing)
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(AppError, match=f"provider.{missing}"):
        load_config(path)


@pytest.mark.parametrize("provider", ["fake", "openai"])
@pytest.mark.parametrize("endpoint", [None, ENDPOINT])
def test_other_providers_forbid_endpoint_field(tmp_path, provider, endpoint):
    path, _ = write_config(tmp_path, provider=provider)
    data = yaml.safe_load(path.read_text(encoding="utf-8"))
    data["provider"]["endpoint"] = endpoint
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(AppError, match="provider.endpoint"):
        load_config(path)


def test_azure_example_and_legacy_example_load(tmp_path):
    project = Path(__file__).parents[2]
    for name in ("config.example.yaml", "config.azure.example.yaml"):
        path, config = write_config(tmp_path / name)
        example = yaml.safe_load((project / name).read_text(encoding="utf-8"))
        example["paths"] = {
            "source": str(config.paths.source),
            "destination": str(config.paths.destination),
        }
        path.write_text(yaml.safe_dump(example), encoding="utf-8")
        loaded = load_config(path)
        assert loaded.provider.prompt == DEFAULT_PROMPT
        if name == "config.azure.example.yaml":
            assert loaded.provider.endpoint == ENDPOINT
            assert loaded.provider.model == DEPLOYMENT
            assert loaded.provider.name == "azure_openai"
            assert "placeholder" in (project / name).read_text().lower()
        else:
            assert loaded.provider.name == "openai"
            assert loaded.provider.endpoint is None
            assert loaded.provider.model == "gpt-image-2"


@pytest.mark.parametrize("image_format,mode", [
    ("PNG", "RGB"), ("PNG", "RGBA"), ("JPEG", "RGB"),
    ("WEBP", "RGB"), ("WEBP", "RGBA"),
])
def test_real_sdk_upload_contract_bytes_pixels_and_alpha(
    tmp_path, azure_http, image_format, mode
):
    captured, settings, png = azure_http()
    source = tmp_path / "source.image"
    staged = tmp_path / "immutable.input"
    with Image.new(mode, (24, 18)) as image:
        for y in range(18):
            for x in range(24):
                pixel = (x * 9, y * 13, (x + y) * 5)
                image.putpixel((x, y), pixel + (80 + x * 7,) if mode == "RGBA" else pixel)
        image.save(source, format=image_format, lossless=True)
    original = source.read_bytes()
    staged.write_bytes(original)
    original_hash = hashlib.sha256(original).hexdigest()
    request = request_for(staged)
    provider = OpenAIProvider(17, azure_endpoint=ENDPOINT.upper() + "/")
    with io.BytesIO() as output:
        result = provider.transform(request, output)
        assert output.getvalue() == png
    assert result.provider_request_id == "a" * 32
    assert settings == [{"trust_env": False, "follow_redirects": False}]
    assert provider._client.max_retries == 0
    assert provider._client.timeout == 17
    assert len(captured) == 1
    http = captured[0]
    assert http.method == "POST"
    assert str(http.url) == ENDPOINT + "/openai/v1/images/edits?api-version=preview"
    assert http.url.params.multi_items() == [("api-version", "preview")]
    assert http.headers["api-key"] == "azure-test-key"
    assert "authorization" not in http.headers
    assert "openai-organization" not in http.headers
    assert "openai-project" not in http.headers
    assert b"public-unused-key" not in http.content
    assert http.extensions["timeout"] == dict.fromkeys(
        ("connect", "read", "write", "pool"), 17
    )
    parts = multipart(http)
    assert set(parts) == {"image", "model", "prompt", "output_format"}
    assert parts["model"].get_payload(decode=True) == DEPLOYMENT.encode()
    assert parts["prompt"].get_payload(decode=True) == DEFAULT_PROMPT.encode()
    assert parts["output_format"].get_payload(decode=True) == b"png"
    upload = parts["image"]
    jpeg = image_format == "JPEG"
    assert upload.get_filename() == ("input.jpg" if jpeg else "input.png")
    assert upload.get_content_type() == ("image/jpeg" if jpeg else "image/png")
    uploaded = upload.get_payload(decode=True)
    if image_format != "WEBP":
        assert uploaded == original
    else:
        with Image.open(io.BytesIO(uploaded)) as decoded, Image.open(source) as before:
            assert decoded.format == "PNG"
            assert decoded.mode == mode
            assert decoded.size == before.size
            assert decoded.tobytes() == before.tobytes()
    assert hashlib.sha256(source.read_bytes()).hexdigest() == original_hash
    assert hashlib.sha256(staged.read_bytes()).hexdigest() == original_hash
    # The provider must release the staged handle even on Windows.
    staged.rename(tmp_path / "released.input")


def test_webp_encoding_enforces_limit_during_write_before_http(
    tmp_path, azure_http, monkeypatch
):
    captured, _, _ = azure_http()
    staged = tmp_path / "large.input"
    with Image.new("RGB", (256, 256), (1, 2, 3)) as image:
        image.save(staged, format="WEBP", lossless=True)
    original = staged.read_bytes()
    request = request_for(staged, limit=len(original))
    buffers = []
    writes = []
    original_write = _BoundedInputBuffer.write

    def checked_write(buffer, payload):
        if buffer not in buffers:
            buffers.append(buffer)
        before = buffer.getvalue()
        try:
            return original_write(buffer, payload)
        except PermanentProviderError:
            assert buffer.getvalue() == before
            raise
        finally:
            writes.append(len(buffer.getvalue()))
            assert len(buffer.getvalue()) <= request.max_input_bytes

    monkeypatch.setattr(_BoundedInputBuffer, "write", checked_write)
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request, output)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
    assert captured == []
    assert writes and buffers and all(buffer.closed for buffer in buffers)
    assert staged.read_bytes() == original
    staged.rename(tmp_path / "released.input")


@pytest.mark.parametrize("failure,code", [
    (OSError, ErrorCode.INVALID_IMAGE),
    (RuntimeError, ErrorCode.INVALID_IMAGE),
    (Image.DecompressionBombError, ErrorCode.IMAGE_LIMIT_EXCEEDED),
    (Image.DecompressionBombWarning, ErrorCode.IMAGE_LIMIT_EXCEEDED),
    (MemoryError, ErrorCode.IMAGE_LIMIT_EXCEEDED),
])
def test_conversion_failure_is_sanitized_permanent_and_offline(
    tmp_path, azure_http, monkeypatch, failure, code
):
    captured, _, _ = azure_http()
    staged = write_image(tmp_path / "image.input", "WEBP")
    request = request_for(staged)

    def fail(*args, **kwargs):
        raise failure("hostile-decoder-detail azure-test-key")

    monkeypatch.setattr(Image.Image, "convert", fail)
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request, output)
    assert caught.value.code == code
    assert "hostile-decoder-detail" not in str(caught.value)
    assert "azure-test-key" not in str(caught.value)
    assert captured == []
    staged.rename(tmp_path / "released.input")


@pytest.mark.parametrize("variable", [
    "OPENAI_BASE_URL", "OPENAI_CUSTOM_HEADERS", "OPENAI_DEFAULT_HEADERS",
    "OPENAI_ADMIN_KEY", "OPENAI_ORG_ID", "OPENAI_PROJECT_ID",
    "OPENAI_API_VERSION", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_AD_TOKEN",
    "AZURE_OPENAI_BASE_URL", "AZURE_OPENAI_DEFAULT_HEADERS",
])
def test_azure_rejects_ambient_overrides_before_sdk(monkeypatch, variable):
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "azure")
    monkeypatch.setenv(variable, "hostile-override")
    calls = []
    monkeypatch.setattr(openai, "OpenAI", lambda **kwargs: calls.append(kwargs))
    with pytest.raises(PermanentProviderError) as caught:
        OpenAIProvider(2, azure_endpoint=ENDPOINT)
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert "hostile-override" not in str(caught.value)
    assert calls == []


@pytest.mark.parametrize("key", [None, "", " \t "])
def test_azure_key_has_no_public_fallback(tmp_path, monkeypatch, key):
    _, config = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    monkeypatch.setenv("OPENAI_API_KEY", "public-key")
    if key is not None:
        monkeypatch.setenv("AZURE_OPENAI_API_KEY", key)
    for operation in (
        lambda: require_provider_credentials(config),
        lambda: OpenAIProvider(2, azure_endpoint=ENDPOINT),
    ):
        with pytest.raises(AppError) as caught:
            operation()
        assert caught.value.code == ErrorCode.CREDENTIAL_MISSING
        assert "AZURE_OPENAI_API_KEY" in caught.value.safe_message
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "azure-key")
    require_provider_credentials(config)


@pytest.mark.parametrize("status,expected", [
    (400, PermanentProviderError), (401, PermanentProviderError),
    (403, PermanentProviderError), (404, PermanentProviderError), (408, AmbiguousProviderError),
    (409, RetryableProviderError), (429, RetryableProviderError),
    (500, AmbiguousProviderError), (503, AmbiguousProviderError), (599, AmbiguousProviderError),
])
def test_azure_real_sdk_errors_share_classification(tmp_path, azure_http, status, expected):
    captured, _, _ = azure_http(lambda _: httpx.Response(
        status, json={"error": {"message": "secret-response-body"}}
    ))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    request = request_for(write_image(tmp_path / "input.png"))
    with io.BytesIO() as output, pytest.raises(expected) as caught:
        provider.transform(request, output)
    assert len(captured) == 1
    assert "secret-response-body" not in str(caught.value)
    assert f"HTTP {status}" in caught.value.safe_message
    assert caught.value.code == (ErrorCode.AUTHENTICATION_FAILED if status in {401, 403} else {
        PermanentProviderError: ErrorCode.PROVIDER_PERMANENT,
        RetryableProviderError: ErrorCode.PROVIDER_RETRYABLE,
        AmbiguousProviderError: ErrorCode.PROVIDER_AMBIGUOUS,
    }[expected])


_PRIVATE_ERROR_TEXT = (
    "sk-fake-diagnostic-secret "
    "eyJhbGciOiJub25lIn0.eyJzdWIiOiJvZmZsaW5lIn0.c2lnbmF0dXJl "
    "PRIVATE_PROMPT_DO_NOT_LOG"
)


@pytest.mark.parametrize("status,hint", [
    (400, "parameters and model support for the image-edit API"),
    (401, "credential is valid for the configured resource"),
    (403, "permissions and network access rules"),
    (404, "resource, deployment name, and API route"),
])
def test_http_status_hints_do_not_echo_unknown_codes(tmp_path, azure_http, status, hint):
    azure_http(lambda _: httpx.Response(status, json={"error": {
        "code": _PRIVATE_ERROR_TEXT, "message": _PRIVATE_ERROR_TEXT,
    }}))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request_for(write_image(tmp_path / "input.png")), output)
    assert f"HTTP {status}" in caught.value.safe_message
    assert hint in caught.value.safe_message
    assert "Provider code" not in caught.value.safe_message
    for private in _PRIVATE_ERROR_TEXT.split():
        assert private not in caught.value.safe_message


@pytest.mark.parametrize("code,hint", [
    ("DeploymentNotFound", "deployment name and its availability"),
    ("ResourceNotFound", "configured resource and API route"),
    ("OperationNotSupported", "deployed model supports the image-edit operation"),
    ("InvalidApiVersionParameter", "API version support"),
    ("UnsupportedApiVersion", "API version support"),
    ("content_filter", "under its content policy"),
    ("content_policy_violation", "under its content policy"),
    ("ResponsibleAIPolicyViolation", "under its content policy"),
    ("InvalidRequest", "parameters against the model's image-edit requirements"),
    ("invalid_request_error", "parameters against the model's image-edit requirements"),
    ("invalid_parameter", "parameters against the model's image-edit requirements"),
    ("Unauthorized", "credential and its intended resource"),
    ("invalid_api_key", "credential and its intended resource"),
    ("AuthenticationError", "credential and its intended resource"),
    ("Forbidden", "resource permissions and access restrictions"),
    ("PermissionDenied", "resource permissions and access restrictions"),
    ("AccessDenied", "resource permissions and access restrictions"),
    ("permission_denied", "resource permissions and access restrictions"),
])
def test_http_known_sdk_codes_have_static_hints(tmp_path, azure_http, code, hint):
    azure_http(lambda _: httpx.Response(400, json={"error": {
        "code": code, "message": _PRIVATE_ERROR_TEXT, "param": _PRIVATE_ERROR_TEXT,
        "innererror": {"code": _PRIVATE_ERROR_TEXT},
    }}))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request_for(write_image(tmp_path / "input.png")), output)
    assert f"Provider code {code}." in caught.value.safe_message
    assert hint in caught.value.safe_message
    for private in _PRIVATE_ERROR_TEXT.split():
        assert private not in caught.value.safe_message


@pytest.mark.parametrize("code", [
    None, True, 404, 4.5, [], {}, ["DeploymentNotFound"],
    {"code": "DeploymentNotFound"}, {"error": {"code": "DeploymentNotFound"}},
    "", "UnknownCode", "deploymentnotfound", "DeploymentNotFound\n",
    "DeploymentNotFound" + "x" * 10_000, _PRIVATE_ERROR_TEXT,
])
def test_http_malformed_or_unrecognized_sdk_codes_are_not_reflected(
    tmp_path, azure_http, code,
):
    azure_http(lambda _: httpx.Response(
        404, headers={"x-request-id": _PRIVATE_ERROR_TEXT},
        json={"error": {
            "code": code, "message": _PRIVATE_ERROR_TEXT, "type": _PRIVATE_ERROR_TEXT,
            "innererror": {"code": "DeploymentNotFound", "message": _PRIVATE_ERROR_TEXT},
        }},
    ))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request_for(write_image(tmp_path / "input.png")), output)
    assert caught.value.safe_message == (
        "The provider rejected the request. HTTP 404. "
        "Check the configured resource, deployment name, and API route."
        " Provider code DeploymentNotFound. Check the deployment name and its availability in the configured resource."
        " Structured response classification is unavailable."
    )
    for private in _PRIVATE_ERROR_TEXT.split():
        assert private not in caught.value.safe_message


@pytest.mark.parametrize("status", [None, True, "404", 404.0, [], {}, 399, 600])
def test_http_diagnostic_status_requires_actual_bounded_integer(status):
    class StatusError(Exception):
        status_code = status
        code = "DeploymentNotFound"

        def __str__(self):
            raise AssertionError("Exception text must not be read")

        @property
        def body(self):
            raise AssertionError("Response body must not be read")

    assert _safe_http_error_details(StatusError()) == ""


def test_http_diagnostics_accept_status_only_exception():
    class StatusError(Exception):
        status_code = 404

        def __str__(self):
            raise AssertionError("Exception text must not be read")

        @property
        def body(self):
            raise AssertionError("Response body must not be read")

    assert _safe_http_error_details(StatusError()) == (
        " HTTP 404. Check the configured resource, deployment name, and API route."
        " Structured response classification is unavailable."
    )


@pytest.mark.parametrize("field", ["body", "code", "type", "param"])
def test_declared_classification_attribute_error_is_not_absence(field):
    def unreadable(self):
        raise AttributeError(_PRIVATE_ERROR_TEXT)

    error = type("Unreadable", (Exception,), {
        "status_code": 400, field: property(unreadable),
    })()
    details = _safe_http_error_details(error)
    assert details == (
        " HTTP 400. Check request parameters and model support for the image-edit API."
        " Structured response classification is unavailable."
    )
    assert recorded_retryable_response(
        "The provider rejected the request." + details, ErrorCode.PROVIDER_PERMANENT,
    ) is None


@pytest.mark.parametrize("body,diagnostic", [
    ({"type": "moderation_blocked", "error": {}}, "Provider code moderation_blocked."),
    ({"type": "unknown", "error": {}}, "Structured response classification is unavailable."),
    ({"error": {}, "message": "x" * 65_536}, "Structured response classification is unavailable."),
])
def test_sdk_unwrapped_error_cannot_hide_envelope_classification(
    tmp_path, azure_http, body, diagnostic,
):
    captured, _, _ = azure_http(lambda _: httpx.Response(400, json=body))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT, azure_response_retries=True)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request_for(write_image(tmp_path / "input.png")), output)
    assert diagnostic in caught.value.safe_message
    assert recorded_retryable_response(caught.value.safe_message, caught.value.code) is None
    assert len(captured) == 1
    assert len(caught.value.safe_message) < 2048


def test_http401_interactive_diagnostics_preserve_authentication_failure(
    tmp_path, azure_http,
):
    captured, _, _ = azure_http(lambda _: httpx.Response(401, json={"error": {
        "code": "Unauthorized", "message": _PRIVATE_ERROR_TEXT,
    }}))
    token = AccessToken("offline-token", int(datetime.now(UTC).timestamp()) + 600)
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT, access_token=token)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request_for(write_image(tmp_path / "input.png")), output)
    assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert "restart and sign in again" in caught.value.safe_message
    assert "HTTP 401" in caught.value.safe_message
    assert "Provider code Unauthorized." in caught.value.safe_message
    assert len(captured) == 1
    for private in (*_PRIVATE_ERROR_TEXT.split(), token.token):
        assert private not in caught.value.safe_message


def test_http404_preserves_safe_sdk_diagnostics(tmp_path, azure_http):
    captured, _, _ = azure_http(lambda _: httpx.Response(
        404, json={"error": {
            "code": "DeploymentNotFound", "message": "private-provider-message",
        }},
    ))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    request = request_for(write_image(tmp_path / "input.png"))
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request, output)
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert len(captured) == 1
    assert "HTTP 404" in caught.value.safe_message
    assert "DeploymentNotFound" in caught.value.safe_message
    assert "private-provider-message" not in caught.value.safe_message


@pytest.mark.parametrize("provider_code,synthetic_key", [
    ("DeploymentNotFound", "azure-test-key"),
    (_PRIVATE_ERROR_TEXT, "azure-test-key"),
    ("DeploymentNotFound", "DeploymentNotFound"),
])
def test_http404_diagnostics_reach_processor_logs_state_and_quarantine(
    tmp_path, azure_http, monkeypatch, capsys, provider_code, synthetic_key,
):
    import tcfcomic.cli as cli

    captured, _, _ = azure_http(lambda _: httpx.Response(
        404, json={"error": {
            "code": provider_code, "message": _PRIVATE_ERROR_TEXT,
        }},
    ))
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", synthetic_key)
    path, config = write_config(
        tmp_path, provider="azure_openai", endpoint=ENDPOINT, model=DEPLOYMENT,
    )
    source = write_image(config.paths.source / "input.png")
    original = source.read_bytes()
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    monkeypatch.setattr(cli, "Processor", lambda cfg, logger, **kwargs: Processor(
        cfg, logger, runner=InProcessAttemptRunner(provider), **kwargs,
    ))
    try:
        assert cli.main(["process", "--config", str(path), str(source)]) == 5
        console = capsys.readouterr().err
        log = (config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl").read_text()
        records = list(config.paths.quarantine.glob("*.json"))
        assert len(records) == 1
        quarantine = json.loads(records[0].read_text())
        with sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db") as db:
            attempts = db.execute(
                "SELECT state, error_code, safe_message FROM attempts"
            ).fetchall()
        assert len(attempts) == 1
        assert attempts[0][:2] == ("permanent", "PROVIDER_PERMANENT")
        messages = [quarantine["safe_message"], attempts[0][2]]
        for text in (console, log):
            events = [json.loads(line) for line in text.splitlines() if line.startswith("{")]
            event, = [event for event in events if event["event"] == "provider_attempt"]
            assert event["level"] == "INFO"
            messages.append(event.get("message", ""))
        assert all("HTTP 404" in message for message in messages), messages
        if provider_code == "DeploymentNotFound":
            expected_code = "[REDACTED]" if synthetic_key == provider_code else provider_code
            assert all(expected_code in message for message in messages), messages
        else:
            assert all("Provider code" not in message for message in messages), messages
        for private in (*_PRIVATE_ERROR_TEXT.split(), synthetic_key):
            assert private not in console + log + records[0].read_text() + attempts[0][2]
        assert cli.main(["process", "--config", str(path), str(source)]) == 5
        assert len(captured) == 1
        assert source.read_bytes() == original
        assert not list(config.paths.destination.glob("*.png"))
    finally:
        logger = logging.getLogger("tcfcomic")
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_azure_redirect_is_not_followed(tmp_path, azure_http):
    captured, _, _ = azure_http(lambda _: httpx.Response(
        307, headers={"Location": "https://attacker.example/steal"}
    ))
    provider = OpenAIProvider(2, azure_endpoint=ENDPOINT)
    request = request_for(write_image(tmp_path / "input.png"))
    with io.BytesIO() as output, pytest.raises(AppError):
        provider.transform(request, output)
    assert len(captured) == 1
    assert captured[0].url.host == "sweepertestai.openai.azure.com"


def test_factory_fails_closed_and_executes_azure(tmp_path, azure_http):
    captured, _, png = azure_http()
    request = request_for(write_image(tmp_path / "input.png"))
    config = ProviderConfig("azure_openai", DEPLOYMENT, DEFAULT_PROMPT, 2, ENDPOINT)
    output = tmp_path / "out.tmp"
    result = _execute_config(config, request, output)
    assert result.outcome == "succeeded"
    assert output.read_bytes() == png
    assert len(captured) == 1
    for invalid in (
        replace(config, name="typo"),
        replace(config, name="typo", endpoint=None),
        replace(config, name="fake"),
        replace(config, name="openai"),
        replace(config, endpoint=None),
        replace(config, model=""),
    ):
        result = _execute_config(invalid, request, tmp_path / "invalid.tmp")
        assert result.outcome == "permanent"
    assert len(captured) == 1
    assert not (tmp_path / "invalid.tmp").exists()


@pytest.mark.parametrize("provider", ["fake", "openai", "azure_openai"])
def test_exact_identity_preserves_legacy_and_versions_azure(tmp_path, quiet_logger, provider):
    _, config = write_config(
        tmp_path, provider=provider,
        endpoint=ENDPOINT.upper() + "/" if provider == "azure_openai" else None,
    )
    config = replace(config, provider=replace(config.provider, prompt="test \u00e9"))
    with Processor(config, quiet_logger) as processor:
        identity = processor._request_identity()
    expected = config.provider.prompt
    if provider == "azure_openai":
        expected = json.dumps(
            ["azure_openai.identity.v1", ENDPOINT, "preview", expected],
            ensure_ascii=False, separators=(",", ":"),
        )
    assert identity.prompt_hash == hashlib.sha256(expected.encode("utf-8")).hexdigest()
    assert identity.provider == provider
    assert identity.model == config.provider.model


def test_both_raw_and_trimmed_credentials_redacted_everywhere(
    tmp_path, monkeypatch, quiet_logger
):
    public = "  public-opaque-value \t"
    azure = "\t azure-opaque-value  "
    monkeypatch.setenv("OPENAI_API_KEY", public)
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", azure)
    message = "|".join((public, public.strip(), azure, azure.strip()))
    _, config = write_config(tmp_path)
    logger = configure_logging(config.logging, config.paths.destination)
    try:
        log_event(logger, logging.ERROR, "redaction", message=message)
        for handler in logger.handlers:
            handler.flush()
        source = write_image(config.paths.source / "source.png")
        with Processor(config, quiet_logger) as processor:
            snapshot = request_for(source).source
            admitted = processor.state.admit(snapshot, processor._request_identity())
            processor.state.claim_ready(admitted.job_id, datetime.now(UTC))
            attempt = processor.state.begin_attempt(admitted.job_id)
            processor.state.mark_retryable(
                admitted.job_id, attempt.attempt_no, 0,
                "2000-01-01T00:00:00+00:00", ErrorCode.PROVIDER_RETRYABLE, message,
            )
            processor.state.claim_ready(admitted.job_id, datetime.now(UTC))
            attempt = processor.state.begin_attempt(admitted.job_id)
            temp_name = "b" * 32 + ".tmp"
            processor.state.set_current_temp_name(admitted.job_id, temp_name)
            processor.state.mark_response_staged(
                admitted.job_id, attempt.attempt_no, 0, temp_name, message,
            )
            database = "\n".join(processor.state.connection.iterdump())
        record = FailureRecord(
            admitted.job_id, message, 1, 1, "a" * 64, "provider",
            ErrorCode.PROVIDER_PERMANENT, message, 1, "now", "now",
        )
        quarantine = write_failure_record(record, config.paths.quarantine).read_text()
        log = (config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl").read_text()
        for text in (sanitize_text(message), str(AppError(ErrorCode.STATE_FAILED, message)),
                     database, quarantine, log):
            assert public not in text and public.strip() not in text
            assert azure not in text and azure.strip() not in text
            assert "[REDACTED]" in text
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()
