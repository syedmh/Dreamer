from __future__ import annotations

import asyncio
import json
import logging
import multiprocessing
import os
import signal
import threading
import time
from multiprocessing.connection import Connection

import httpx

from .authentication import AccessToken
from .azure_access import MAX_ERROR_BYTES, access_hint, require_supported_environment
from .azure_endpoint import canonical_azure_endpoint
from .config import AppConfig, require_provider_credentials
from .domain import AppError, ErrorCode
from .redaction import redact_secret, validated_request_id
from .providers.worker import SubprocessAttemptRunner

PROBE_TIMEOUT_SECONDS = 30.0
MAX_CATALOG_BYTES = 1_048_576
MODELS_API_VERSION = "2024-10-21"
MAX_RESULT_BYTES = 4096


def validate_probe(config: AppConfig) -> str:
    if config.provider.name != "azure_openai":
        raise AppError(ErrorCode.CONFIG_INVALID, "check-auth supports only the Azure OpenAI provider.")
    try:
        endpoint = canonical_azure_endpoint(config.provider.endpoint)
    except ValueError:
        raise AppError(ErrorCode.CONFIG_INVALID, "check-auth requires a valid HTTPS Azure resource root endpoint.") from None
    require_provider_credentials(config)
    require_supported_environment(azure=True)
    return endpoint


def _inconclusive(message: str) -> AppError:
    return AppError(ErrorCode.PROVIDER_PERMANENT, "Azure catalog probe inconclusive. " + message)


async def _read_bounded(response: httpx.Response, limit: int) -> bytes | None:
    # Reject encodings rather than decompressing an unbounded response.
    if response.headers.get("content-encoding", "identity").lower() != "identity":
        return None
    if response.is_stream_consumed:
        return response.content if len(response.content) <= limit else None
    chunks = bytearray()
    async for chunk in response.aiter_raw(chunk_size=8192):
        if len(chunks) + len(chunk) > limit:
            return None
        chunks.extend(chunk)
    return bytes(chunks)


async def _get_catalog(
    endpoint: str, authentication: str, secret: str, timeout: float, on_denial=None,
) -> str:
    headers = {"Accept-Encoding": "identity"}
    headers.update(
        {"api-key": secret} if authentication == "api_key"
        else {"Authorization": "Bearer " + secret}
    )
    async with httpx.AsyncClient(
        trust_env=False, follow_redirects=False, verify=True, timeout=timeout,
        transport=httpx.AsyncHTTPTransport(retries=0, trust_env=False, verify=True),
    ) as client:
        async with client.stream(
            "GET", endpoint + "/openai/models",
            params={"api-version": MODELS_API_VERSION}, headers=headers,
        ) as response:
            status = response.status_code
            details = f"Authentication {authentication}. HTTP {status}."
            for name in ("x-request-id", "apim-request-id", "x-ms-request-id"):
                request_id = validated_request_id(response.headers.get(name))
                if request_id is not None:
                    details += f" Request ID {request_id}."
                    break
            if status in {401, 403}:
                if on_denial is not None:
                    on_denial(
                        "Azure denied catalog access. " + details + " "
                        + access_hint(status, authentication),
                    )
                # A received denial remains definitive even if its body stalls.
                try:
                    body = await _read_bounded(response, MAX_ERROR_BYTES)
                except (httpx.HTTPError, asyncio.CancelledError):
                    body = None
                raise AppError(
                    ErrorCode.AUTHENTICATION_FAILED,
                    "Azure denied catalog access. " + details + " "
                    + access_hint(status, authentication, body or b""),
                )
            if status != 200:
                hint = {
                    404: "The catalog probe route is unavailable; this does not establish an invalid key.",
                    429: "The resource rate-limited the probe. Wait before an operator-initiated check.",
                }.get(status, "A transient service failure occurred." if 500 <= status <= 599
                      else "The catalog probe returned an unexpected status.")
                raise _inconclusive(details + " " + hint + " No retry was made.")
            body = await _read_bounded(response, MAX_CATALOG_BYTES)
            try:
                payload = json.loads(body) if body is not None else None
            except (ValueError, RecursionError):
                payload = None
            if type(payload) is not dict or type(payload.get("data")) is not list:
                raise _inconclusive(details + " The catalog response was invalid, encoded, or exceeded the body limit.")
            return (
                "Azure catalog request accepted. " + details
                + " This proves only catalog access, not image-edit permission, deployment availability, or generation success."
            )


async def _run_probe(endpoint, authentication, secret, timeout, shutdown_event, on_denial=None) -> str:
    if shutdown_event.is_set():
        raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Azure catalog probe interrupted.")
    request = asyncio.create_task(_get_catalog(endpoint, authentication, secret, timeout, on_denial))
    try:
        expires = asyncio.get_running_loop().time() + timeout
        while not request.done():
            if shutdown_event.is_set():
                raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Azure catalog probe interrupted.")
            remaining = expires - asyncio.get_running_loop().time()
            if remaining <= 0:
                request.cancel()
                # Preserve an already received 401/403 when only its body timed out.
                try:
                    await request
                except asyncio.CancelledError:
                    raise _inconclusive("The bounded request timed out. No retry was made.") from None
                break
            await asyncio.wait({request}, timeout=min(0.05, remaining))
        return await request
    finally:
        if not request.done():
            request.cancel()
        await asyncio.gather(request, return_exceptions=True)


def _probe_child(
    connection: Connection, endpoint: str, authentication: str, secret: str, timeout: float,
) -> None:
    logging.disable(logging.CRITICAL)
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, signal.SIG_IGN)
    try:
        def report_denial(message: str) -> None:
            connection.send_bytes(json.dumps({"denial": message}).encode("ascii"))

        try:
            with redact_secret(secret), asyncio.Runner() as runner:
                summary = runner.run(_run_probe(
                    endpoint, authentication, secret, timeout, threading.Event(), report_denial,
                ))
            result = {"summary": summary}
        except AppError as exc:
            result = {"error": exc.code.value, "message": exc.safe_message}
        except httpx.HTTPError:
            result = {"error": ErrorCode.PROVIDER_PERMANENT.value,
                      "message": "Azure catalog probe inconclusive. A transport failure occurred. No retry was made."}
        except Exception:
            # Process boundary: never print or serialize a library exception.
            result = {"error": ErrorCode.PROVIDER_PERMANENT.value,
                      "message": "Azure catalog probe inconclusive. The request worker failed safely."}
        payload = json.dumps(result, ensure_ascii=True).encode("ascii")
        if len(payload) > MAX_RESULT_BYTES:
            raise ValueError("invalid diagnostic result")
        connection.send_bytes(payload)
    except (OSError, ValueError):
        pass  # Parent cancelled or timed out and closed its private pipe.
    finally:
        connection.close()


def _run_in_child(
    endpoint: str, authentication: str, secret: str, timeout: float,
    shutdown_event: threading.Event, shutdown_timeout: float,
) -> str:
    if shutdown_event.is_set():
        raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Azure catalog probe interrupted.")
    context = multiprocessing.get_context("spawn")
    parent, child = context.Pipe(duplex=False)
    process = context.Process(
        target=_probe_child, args=(child, endpoint, authentication, secret, timeout),
        name="tcfcomic-auth-probe", daemon=False,
    )
    expires = time.monotonic() + timeout
    denial = None
    try:
        process.start()
        child.close()
        while True:
            if shutdown_event.is_set():
                raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Azure catalog probe interrupted.")
            remaining = expires - time.monotonic()
            terminal_error = None
            if remaining <= 0:
                terminal_error = _inconclusive("The bounded request worker timed out. No retry was made.")
            ready = parent.poll(0 if terminal_error is not None else min(0.05, remaining))
            if not ready and terminal_error is None and not process.is_alive():
                terminal_error = _inconclusive("The request worker stopped without a result. No retry was made.")
                ready = parent.poll(0)
            if shutdown_event.is_set():
                raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Azure catalog probe interrupted.")
            if ready:
                result = json.loads(parent.recv_bytes(MAX_RESULT_BYTES))
                if type(result) is not dict:
                    raise ValueError("invalid diagnostic result")
                message = (
                    result.get("summary") if set(result) == {"summary"} else
                    result.get("denial") if set(result) == {"denial"} else result.get("message")
                )
                if (
                    type(message) is not str or not 1 <= len(message) <= 2048
                    or not message.isascii() or any(ord(char) < 32 or ord(char) == 127 for char in message)
                ):
                    raise ValueError("invalid diagnostic result")
                if set(result) == {"denial"} and denial is None:
                    denial = AppError(ErrorCode.AUTHENTICATION_FAILED, message)
                    # Consume one ready message at a terminal boundary, never drain or wait.
                    if terminal_error is not None:
                        raise denial
                    continue
                if set(result) == {"summary"} and denial is None:
                    return message
                if set(result) == {"error", "message"} and result["error"] in {
                    ErrorCode.AUTHENTICATION_FAILED.value, ErrorCode.PROVIDER_PERMANENT.value,
                }:
                    if denial is not None and result["error"] != ErrorCode.AUTHENTICATION_FAILED.value:
                        raise denial
                    raise AppError(ErrorCode(result["error"]), message)
                raise ValueError("invalid diagnostic result")
            if terminal_error is not None:
                raise denial or terminal_error
    except (OSError, EOFError, ValueError, TypeError, RecursionError):
        raise denial or _inconclusive("The request worker could not return a valid result. No retry was made.") from None
    finally:
        parent.close()
        child.close()
        if process.pid is not None:
            SubprocessAttemptRunner._stop_process(process, time.monotonic() + shutdown_timeout)
            process.join(timeout=0)
            if process.is_alive():
                raise _inconclusive("The request worker could not be stopped.")
        process.close()


def check_auth(
    config: AppConfig, shutdown_event: threading.Event, *,
    access_token: AccessToken | None = None, timeout: float | None = None,
) -> str:
    endpoint = validate_probe(config)
    timeout = min(PROBE_TIMEOUT_SECONDS, config.provider.request_timeout_seconds,
                  timeout if timeout is not None else PROBE_TIMEOUT_SECONDS)
    if timeout <= 0:
        raise _inconclusive("The bounded check timed out before the request.")
    if config.provider.authentication == "interactive":
        if access_token is None:
            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "The catalog probe requires the configured interactive sign-in.")
        access_token.require_valid(timeout)
        secret = access_token.token
    else:
        if access_token is not None:
            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "The catalog probe authentication mode does not match.")
        secret = os.environ.get("AZURE_OPENAI_API_KEY", "").strip()
    # Even inherited HTTP DEBUG handlers must not emit wire headers or bodies.
    previous_logging = logging.root.manager.disable
    try:
        logging.disable(logging.CRITICAL)
        with redact_secret(secret):
            return _run_in_child(
                endpoint, config.provider.authentication, secret, timeout, shutdown_event,
                min(config.shutdown.timeout_seconds, PROBE_TIMEOUT_SECONDS),
            )
    except httpx.HTTPError:
        raise _inconclusive("A transport failure occurred. No retry was made.") from None
    finally:
        logging.disable(previous_logging)
