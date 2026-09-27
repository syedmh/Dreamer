from __future__ import annotations

import base64
import binascii
import io
import json
import os
import math
import re
from contextlib import ExitStack
from datetime import UTC, datetime
from email.utils import parsedate_to_datetime
from inspect import getattr_static
from collections.abc import Mapping
from dataclasses import dataclass
from typing import BinaryIO

from PIL import Image, ImageOps

from ..config import LimitsConfig
from ..image_io import validate_input, validate_dimensions
from ..azure_endpoint import AZURE_API_VERSION, canonical_azure_endpoint
from ..authentication import AccessToken
from ..azure_access import ACCESS_CODE_HINTS, access_hint, require_supported_environment
from ..redaction import _REQUEST_ID, validated_request_id
from ..domain import (
    AmbiguousProviderError,
    AppError,
    ErrorCode,
    PermanentProviderError,
    ProviderResult,
    RetryableProviderError,
    TransformRequest,
)


_HTTP_STATUS_HINTS = {
    400: "Check request parameters and model support for the image-edit API.",
    401: "Check that the credential is valid for the configured resource (API key or signed-in identity).",
    403: "Check resource permissions and network access rules.",
    404: "Check the configured resource, deployment name, and API route.",
}
_PROVIDER_CODE_DIAGNOSTICS = {
    code: f"Provider code {code}. {hint}"
    for codes, hint in (
        (("DeploymentNotFound",), "Check the deployment name and its availability in the configured resource."),
        (("ResourceNotFound",), "Check the configured resource and API route."),
        (("OperationNotSupported",), "Check whether the deployed model supports the image-edit operation."),
        (("InvalidApiVersionParameter", "UnsupportedApiVersion"), "Check API version support for this resource and operation."),
        (("content_filter", "contentFilter", "content_policy_violation", "ResponsibleAIPolicyViolation", "moderation_blocked"), "The provider blocked the input or generated output under its content policy."),
        (("InvalidRequest", "InvalidPayload", "invalid_request_error", "invalid_parameter"), "Check request parameters against the model's image-edit requirements."),
        (("Unauthorized", "invalid_api_key", "AuthenticationError"), "Check the credential and its intended resource."),
        (("Forbidden", "PermissionDenied", "AccessDenied", "permission_denied"), "Check resource permissions and access restrictions."),
        (("insufficient_quota", "quota_exceeded", "QuotaExceeded", "InsufficientQuota", "billing_hard_limit_reached"), "The resource quota or billing limit is exhausted."),
        (("InvalidParameter", "InvalidArgument", "BadArgument", "invalid_request", "InvalidRequestError", "UnsupportedOperation", "ModelNotFound", "model_not_found"), "Check request parameters and model support for the image-edit API."),
    )
    for code in codes
}
_TRANSIENT_CODES = frozenset({
    "rate_limit_exceeded", "RateLimitExceeded", "TooManyRequests",
    "InternalServerError", "ServiceUnavailable", "RequestTimeout", "Timeout", "Conflict",
})
_PROVIDER_CODE_DIAGNOSTICS.update({
    code: f"Provider code {code}. The provider reported a temporary response failure."
    for code in _TRANSIENT_CODES
})
_PROVIDER_CODE_DIAGNOSTICS.update({
    code: f"Provider code {code}. {hint}" for code, hint in ACCESS_CODE_HINTS.items()
})
_CONTENT_FILTER_CODES = frozenset({
    "content_filter", "contentFilter", "content_policy_violation",
    "ResponsibleAIPolicyViolation", "moderation_blocked",
})
_ACCESS_CODES = frozenset(ACCESS_CODE_HINTS) | frozenset({
    "Unauthorized", "invalid_api_key", "AuthenticationError",
    "Forbidden", "PermissionDenied", "AccessDenied", "permission_denied",
})
_ERROR_PARAMETERS = frozenset({
    "prompt", "image", "image[]", "mask", "model", "size", "n", "quality",
    "output_format", "output_compression", "background", "input_fidelity",
    "moderation",
})
_UNCLASSIFIABLE = " Structured response classification is unavailable."
_COMPLETE_RESPONSE = " Classification evidence v1 complete."
_NOT_SENT = (
    "The provider connection could not be established. Dispatch evidence v1 not-sent."
)
_RESPONSE_PREFIXES = {
    ErrorCode.PROVIDER_PERMANENT: "The provider rejected the request.",
    ErrorCode.PROVIDER_RETRYABLE: "The provider is temporarily unavailable.",
    ErrorCode.PROVIDER_AMBIGUOUS: "The provider request ended with an uncertain dispatch result.",
}


@dataclass(frozen=True)
class _HttpFields:
    codes: tuple[str, ...]
    parameters: tuple[str, ...]
    uncertain: bool
    definitive: bool = False


def _unique_response_members(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate response member")
        result[key] = value
    return result


def _http_fields(exc: Exception) -> _HttpFields:
    uncertain = False
    definitive = False

    def read(name: str):
        nonlocal uncertain
        try:
            return getattr(exc, name)
        except AttributeError:
            # A declared property raising AttributeError is unreadable, not absent.
            if getattr_static(exc, name, None) is not None:
                uncertain = True
        except Exception:
            uncertain = True
        return None

    body = read("body")
    envelope = None
    # The SDK unwraps `error`, losing sibling root classifications. Inspect the
    # already-buffered envelope, never fetching it or emitting its freeform text.
    from httpx import Response
    from openai import APIStatusError

    if isinstance(exc, APIStatusError):
        response = read("response")
        if type(response) is Response:
            try:
                status = read("status_code")
                definitive = (
                    type(status) is int and 400 <= status <= 599
                    and response.status_code == status
                )
                if not definitive:
                    uncertain = True
                content = response.content
                if content:
                    if len(content) > 65_536:
                        uncertain = True
                    else:
                        envelope = json.loads(content, object_pairs_hook=_unique_response_members)
                        if type(envelope) is not dict:
                            uncertain = True
                elif type(body) is str and body == "":
                    # The SDK represents an empty wire body as ""; unlike a
                    # JSON null/scalar, the verified empty response proves
                    # absence of envelope fields. Nonempty SDK body evidence
                    # and classification attributes must still be inspected.
                    body = None
            except Exception:
                uncertain = True
        else:
            uncertain = True
    if body is not None and type(body) is not dict:
        uncertain = True
    # Neither the original envelope nor the independently captured SDK body
    # may hide the other's classifications. Read each attribute only once.
    nodes = [value for value in (body, envelope) if type(value) is dict]
    for node in tuple(nodes):
        if type(node.get("error")) is dict:
            nodes.append(node["error"])
    for node in tuple(nodes):
        for key in ("innererror", "inner_error"):
            inner = node.get(key)
            if type(inner) is dict:
                nodes.append(inner)
    codes = [read("code"), read("type")]
    codes.extend(node.get(key) for node in nodes for key in ("code", "type"))
    parameters = [read("param"), *(node.get("param") for node in nodes)]
    recognized = tuple(
        code for code in codes
        if type(code) is str and code in _PROVIDER_CODE_DIAGNOSTICS
    )
    known_parameters = tuple(
        value for value in parameters if type(value) is str and value in _ERROR_PARAMETERS
    )
    # Unknown structured fields must not become an apparently unclassified legacy 400.
    uncertain = uncertain or any(
        value is not None and (
            type(value) is not str or value not in allowed
        )
        for values, allowed in (
            (codes, _PROVIDER_CODE_DIAGNOSTICS), (parameters, _ERROR_PARAMETERS),
        )
        for value in values
    ) or any(
        node.get(key) is not None and (
            type(node[key]) is not dict
            or not any(node[key] is inspected for inspected in nodes)
        )
        for node in nodes for key in ("error", "innererror", "inner_error")
    )
    return _HttpFields(recognized, known_parameters, uncertain, definitive)


def _eligible_response(status: int, fields: _HttpFields) -> bool:
    return (
        (status in {400, 408, 409, 429} or 500 <= status <= 599)
        and not any(code not in _TRANSIENT_CODES for code in fields.codes)
        and not (status == 400 and fields.codes)
        and not fields.parameters and not fields.uncertain
    )


def recorded_retryable_response(message: str, error_code: ErrorCode) -> int | None:
    """Parse only our complete, bounded diagnostic grammar, never provider prose."""
    prefix = _RESPONSE_PREFIXES.get(error_code)
    if type(message) is not str or len(message) > 2048 or prefix is None:
        return None
    if not message.endswith(_COMPLETE_RESPONSE):
        return None
    message = message[:-len(_COMPLETE_RESPONSE)]
    match = re.fullmatch(
        re.escape(prefix) + r" HTTP ([45][0-9]{2})\.(?: Request ID (" + _REQUEST_ID.pattern + r")\.)?(.*)",
        message,
    )
    if match is None:
        return None
    status = int(match[1])
    tail = match[3]
    hint = _HTTP_STATUS_HINTS.get(status)
    had_hint = False
    if hint and tail.startswith(" " + hint):
        tail = tail[len(hint) + 1:]
        had_hint = True
    codes = ()
    for code, diagnostic in _PROVIDER_CODE_DIAGNOSTICS.items():
        if tail.startswith(" " + diagnostic):
            codes = (code,)
            tail = tail[len(diagnostic) + 1:]
            break
    if bool(hint and not (status == 400 and codes)) != had_hint:
        return None
    # Parameter fields, unknown markers, or any other trailing text fail closed.
    if tail or not _eligible_response(status, _HttpFields(codes, (), False)):
        return None
    if error_code == ErrorCode.PROVIDER_AMBIGUOUS and status != 408 and status < 500:
        return None
    return status


def _retry_after_seconds(
    headers: Mapping[str, str], *, now: datetime | None = None,
    default: float | None = 0.0,
) -> float | None:
    """Only return bounded delays; never persist or report raw header values."""
    if not isinstance(headers, Mapping):
        return default
    for name, divisor in (("retry-after-ms", 1000), ("retry-after", 1)):
        value = headers.get(name)
        if not isinstance(value, str) or len(value) > 128:
            continue
        try:
            seconds = float(value) / divisor
        except (ValueError, OverflowError):
            if name != "retry-after":
                continue
            try:
                date = parsedate_to_datetime(value)
                if date.tzinfo is None:
                    continue
                seconds = max(0.0, (date - (now or datetime.now(UTC))).total_seconds())
            except (TypeError, ValueError, OverflowError):
                continue
        if math.isfinite(seconds) and 0 <= seconds <= 86_400:
            return seconds
    return default


def _safe_http_error_details(exc: Exception) -> str:
    status = getattr(exc, "status_code", None)
    if type(status) is not int or not 400 <= status <= 599:
        return ""
    return _format_http_error_details(exc, status, _http_fields(exc))


def _http_identity_details(exc: Exception, status: int) -> list[str]:
    details = [f"HTTP {status}."]
    request_ids = [getattr(exc, "request_id", None)]
    headers = getattr(getattr(exc, "response", None), "headers", None)
    if isinstance(headers, Mapping):
        request_ids.extend(
            headers.get(name)
            for name in ("x-request-id", "apim-request-id", "x-ms-request-id")
        )
    for request_id in request_ids:
        if validated_request_id(request_id) is not None:
            details.append(f"Request ID {request_id}.")
            break
    return details


def _format_http_error_details(exc: Exception, status: int, fields: _HttpFields) -> str:
    """Format only the classification snapshot used to decide this response."""
    details = _http_identity_details(exc, status)
    recognized = fields.codes
    code = next(
        (code for code in recognized if code in _CONTENT_FILTER_CODES),
        next((code for code in recognized if code not in _TRANSIENT_CODES),
             recognized[0] if recognized else None),
    )
    if status in _HTTP_STATUS_HINTS and not (status == 400 and code is not None):
        details.append(_HTTP_STATUS_HINTS[status])
    if code is not None:
        details.append(_PROVIDER_CODE_DIAGNOSTICS[code])
    if fields.parameters:
        details.append(f"Parameter {fields.parameters[0]}.")
    return " " + " ".join(details) + (_UNCLASSIFIABLE if fields.uncertain else "")


class _BoundedInputBuffer(io.BytesIO):
    def __init__(self, limit: int) -> None:
        super().__init__()
        self._limit = limit

    def write(self, payload: bytes) -> int:
        if self.tell() + len(payload) > self._limit:
            raise PermanentProviderError(
                ErrorCode.IMAGE_LIMIT_EXCEEDED,
                "The converted input exceeds the configured byte limit.",
            )
        return super().write(payload)


def _prepare_upload(
    request: TransformRequest, stack: ExitStack, *, azure: bool,
) -> tuple[str, BinaryIO, str] | BinaryIO:
    """Validate at point of use and normalize without modifying staged/source bytes."""
    try:
        limits = LimitsConfig(
            request.max_input_bytes, request.max_output_bytes,
            request.max_width, request.max_height, request.max_pixels,
        )
        info = validate_input(request.source.staged_path, limits, request.source.path.name)
        staged = stack.enter_context(request.source.staged_path.open("rb"))
        if info.format != "MPO" and not azure:
            return staged
        with Image.open(staged) as image:
            if image.format in {"PNG", "JPEG"}:
                image_format = image.format
            elif image.format in {"WEBP", "MPO"}:
                image.seek(0)
                image.load()
                converted = stack.enter_context(
                    _BoundedInputBuffer(request.max_input_bytes)
                )
                mode = "RGBA" if "A" in image.getbands() else "RGB"
                with ExitStack() as images:
                    primary = (
                        images.enter_context(ImageOps.exif_transpose(image))
                        if image.format == "MPO" else image
                    )
                    pixels = images.enter_context(primary.convert(mode))
                    validate_dimensions(*pixels.size, limits)
                    pixels.info.clear()
                    pixels.save(converted, format="PNG", exif=b"")
                converted.seek(0)
                return ("input.png", converted, "image/png")
            else:
                raise ValueError("unsupported input format")
        staged.seek(0)
        if image_format == "PNG":
            return ("input.png", staged, "image/png")
        return ("input.jpg", staged, "image/jpeg")
    except PermanentProviderError:
        raise
    except AppError as exc:
        raise PermanentProviderError(exc.code, exc.safe_message) from None
    except (Image.DecompressionBombWarning, Image.DecompressionBombError, MemoryError):
        raise PermanentProviderError(
            ErrorCode.IMAGE_LIMIT_EXCEEDED,
            "The input conversion exceeds image resource limits.",
        ) from None
    except (OSError, ValueError, SyntaxError, TypeError, RuntimeError, OverflowError):
        raise PermanentProviderError(
            ErrorCode.INVALID_IMAGE,
            "The staged image could not be prepared for the provider.",
        ) from None


def _is_definitive_pre_dispatch_connect_failure(exc: BaseException) -> bool:
    """Recognize failures that prove no HTTP request reached the provider."""
    import httpx
    from openai import APIConnectionError

    # Only a direct genuine SDK transport cause proves not-sent. A deeper
    # connect error inside a read/write failure does not establish that fact.
    return (
        isinstance(exc, APIConnectionError)
        and getattr(exc, "response", None) is None
        and getattr(exc, "status_code", None) is None
        and type(exc.__cause__) in {httpx.ConnectError, httpx.ConnectTimeout, httpx.PoolTimeout}
    )


class OpenAIProvider:
    def __init__(
        self, request_timeout_seconds: float, *, azure_endpoint: str | None = None,
        access_token: AccessToken | None = None,
        azure_response_retries: bool = False,
    ) -> None:
        self._azure_endpoint = None
        if azure_endpoint is not None:
            try:
                self._azure_endpoint = canonical_azure_endpoint(azure_endpoint)
            except ValueError as exc:
                raise PermanentProviderError(
                    ErrorCode.PROVIDER_PERMANENT, str(exc)
                ) from None
        azure = self._azure_endpoint is not None
        self._azure_response_retries = azure and azure_response_retries
        self._access_token = access_token
        self._request_timeout_seconds = request_timeout_seconds
        if access_token is not None:
            if not azure:
                raise PermanentProviderError(
                    ErrorCode.AUTHENTICATION_FAILED, "Interactive authentication is Azure-only."
                )
            self._check_token()
        variable = "AZURE_OPENAI_API_KEY" if azure else "OPENAI_API_KEY"
        label = "Azure OpenAI" if azure else "OpenAI"
        api_key = access_token.token if access_token is not None else os.environ.get(variable, "").strip()
        if not api_key:
            raise PermanentProviderError(
                ErrorCode.CREDENTIAL_MISSING,
                f"The {variable} environment variable is required for the {label} provider.",
            )
        require_supported_environment(azure=azure)
        from openai import DefaultHttpxClient, OpenAI

        self._api_key = api_key
        self._client = OpenAI(
            api_key=api_key,
            base_url=(
                self._azure_endpoint + "/openai/v1/"
                if self._azure_endpoint is not None else "https://api.openai.com/v1"
            ),
            max_retries=0,
            timeout=request_timeout_seconds,
            http_client=DefaultHttpxClient(trust_env=False, follow_redirects=False),
        )

    def _check_token(self) -> None:
        from ..domain import AppError

        if self._access_token is not None:
            try:
                self._access_token.require_valid(self._request_timeout_seconds)
            except AppError:
                raise PermanentProviderError(
                    ErrorCode.AUTHENTICATION_FAILED,
                    "The sign-in token expired before dispatch. Restart and sign in again.",
                ) from None

    def transform(self, request: TransformRequest, output: BinaryIO) -> ProviderResult:
        try:
            with ExitStack() as stack:
                if self._azure_endpoint is not None:
                    from openai import omit

                    upload = _prepare_upload(request, stack, azure=True)
                    self._check_token()
                    response = self._client.images.edit(
                        model=request.model,
                        image=upload,
                        prompt=request.prompt,
                        output_format="png",
                        extra_query={"api-version": AZURE_API_VERSION},
                        extra_headers=(
                            {"api-key": omit}
                            if self._access_token is not None
                            else {"api-key": self._api_key, "Authorization": omit}
                        ),
                    )
                else:
                    staged_input = _prepare_upload(request, stack, azure=False)
                    response = self._client.images.edit(
                        model=request.model,
                        image=staged_input,
                        prompt=request.prompt,
                        output_format="png",
                    )
            data = getattr(response, "data", None)
            if type(data) is not list or not data:
                raise TypeError("invalid provider data")
            item = data[0]
            encoded = getattr(item, "b64_json", None)
            if type(encoded) is not str or not encoded:
                raise TypeError("invalid provider image")
            max_encoded = ((request.max_output_bytes + 2) // 3) * 4
            if len(encoded) > max_encoded:
                raise PermanentProviderError(
                    ErrorCode.OUTPUT_LIMIT_EXCEEDED,
                    "The provider output exceeds the configured byte limit.",
                )
            written = 0
            for offset in range(0, len(encoded), 32_768):
                chunk = encoded[offset : offset + 32_768]
                payload = base64.b64decode(chunk, validate=True)
                written += len(payload)
                if written > request.max_output_bytes:
                    raise PermanentProviderError(
                        ErrorCode.OUTPUT_LIMIT_EXCEEDED,
                        "The provider output exceeds the configured byte limit.",
                    )
                output.write(payload)
            return ProviderResult(
                provider_request_id=validated_request_id(getattr(response, "id", None)),
                output_size=written,
                media_type="image/png",
            )
        except PermanentProviderError:
            raise
        except (binascii.Error, IndexError, AttributeError, TypeError, ValueError):
            raise PermanentProviderError(
                ErrorCode.PROVIDER_PERMANENT,
                "The provider returned an invalid image response.",
            ) from None
        except Exception as exc:
            status = getattr(exc, "status_code", None)
            if type(status) is not int or not 400 <= status <= 599:
                status = None
            fields = _http_fields(exc) if status is not None else None
            details = (
                _format_http_error_details(exc, status, fields)
                if status is not None else ""
            )
            # Only this adapter exception path may mint complete response
            # evidence, using exactly the snapshot that decides eligibility.
            if fields is not None and fields.definitive and not fields.uncertain:
                details += _COMPLETE_RESPONSE
            name = type(exc).__name__.lower()
            if status is not None:
                if not fields.definitive:
                    raise AmbiguousProviderError(
                        ErrorCode.PROVIDER_AMBIGUOUS,
                        "The provider request ended without a definitive response.",
                    ) from None
                content_code = next((code for code in fields.codes if code in _CONTENT_FILTER_CODES), None)
                if self._azure_endpoint is not None and status in {401, 403}:
                    authentication = "interactive" if self._access_token is not None else "api_key"
                    try:
                        body = exc.response.content
                    except Exception:
                        body = b""
                    hint = access_hint(status, authentication, body)
                    # A 401/403 carrying an allowlisted content-policy code is a moderation
                    # rejection (PROVIDER_PERMANENT, so a configured fallback may run) only when
                    # no access code or recognized access diagnostic is also present.
                    access_signal = (
                        any(code in _ACCESS_CODES for code in fields.codes)
                        or hint != access_hint(status, authentication)
                    )
                else:
                    access_signal = False
                if (
                    self._azure_endpoint is not None and status in {401, 403}
                    and (content_code is None or access_signal)
                ):
                    code = next(
                        (code for code in fields.codes if code in _ACCESS_CODES),
                        next(iter(fields.codes), None),
                    )
                    raise PermanentProviderError(
                        ErrorCode.AUTHENTICATION_FAILED,
                        f"Azure blocked {authentication} access. "
                        + " ".join(_http_identity_details(exc, status))
                        + (f" Provider code {code}." if code is not None else "")
                        + " " + hint
                        + " Run check-auth --config with the same configuration. Unattempted queued work is retained."
                        + (" Verify access, then restart and sign in again." if self._access_token is not None else ""),
                        retry_after_seconds=_retry_after_seconds(exc.response.headers, default=None),
                    ) from None
                if (
                    fields.uncertain or fields.parameters
                    or any(code not in _TRANSIENT_CODES for code in fields.codes)
                ):
                    raise PermanentProviderError(
                        ErrorCode.PROVIDER_PERMANENT,
                        _RESPONSE_PREFIXES[ErrorCode.PROVIDER_PERMANENT] + details,
                        retry_after_seconds=(
                            _retry_after_seconds(exc.response.headers)
                            if status == 429 else None
                        ),
                    ) from None
            if self._azure_response_retries and status is not None:
                if _eligible_response(status, fields):
                    raise RetryableProviderError(
                        ErrorCode.PROVIDER_RETRYABLE,
                        _RESPONSE_PREFIXES[ErrorCode.PROVIDER_RETRYABLE] + details,
                        retry_after_seconds=_retry_after_seconds(
                            getattr(getattr(exc, "response", None), "headers", {}),
                            default=0.0 if status == 429 else None,
                        ),
                    ) from None
                raise PermanentProviderError(
                    ErrorCode.PROVIDER_PERMANENT,
                    _RESPONSE_PREFIXES[ErrorCode.PROVIDER_PERMANENT] + details,
                ) from None
            if status == 408 or (
                isinstance(status, int) and 500 <= status <= 599
            ):
                raise AmbiguousProviderError(
                    ErrorCode.PROVIDER_AMBIGUOUS,
                    "The provider request ended with an uncertain dispatch result." + details,
                ) from None
            if status in {409, 429}:
                raise RetryableProviderError(
                    ErrorCode.PROVIDER_RETRYABLE,
                    "The provider is temporarily unavailable." + details,
                    retry_after_seconds=(
                        _retry_after_seconds(getattr(getattr(exc, "response", None), "headers", {}))
                        if status == 429 else None
                    ),
                ) from None
            if isinstance(status, int) and 400 <= status < 500:
                raise PermanentProviderError(
                    ErrorCode.PROVIDER_PERMANENT,
                    "The provider rejected the request." + details,
                ) from None
            if _is_definitive_pre_dispatch_connect_failure(exc):
                raise RetryableProviderError(
                    ErrorCode.PROVIDER_RETRYABLE,
                    _NOT_SENT,
                ) from None
            if "timeout" in name or "connection" in name or "protocol" in name:
                raise AmbiguousProviderError(
                    ErrorCode.PROVIDER_AMBIGUOUS,
                    "The provider request ended with an uncertain dispatch result.",
                ) from None
            raise AmbiguousProviderError(
                ErrorCode.PROVIDER_AMBIGUOUS,
                "The provider request ended without a definitive response.",
            ) from None
