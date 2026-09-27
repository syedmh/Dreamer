from __future__ import annotations

import json
import logging
import math
import multiprocessing
import signal
import time
from contextlib import ExitStack
from dataclasses import dataclass, field
from multiprocessing.connection import Connection
from typing import Callable, Protocol

from .config import ProviderConfig, validate_authentication
from .domain import AppError, ErrorCode
from .redaction import redact_secret

SCOPE = "https://cognitiveservices.azure.com/.default"
AUTHORITY = "https://login.microsoftonline.com"
DEFAULT_TENANT = "organizations"
DEFAULT_CLIENT = "04b07795-8ddb-461a-bbee-02f9e1bf7b46"
TOKEN_ALLOWANCE_SECONDS = 35.0
MAX_TOKEN_LENGTH = 16_384
MAX_MESSAGE_BYTES = 20_480
STARTUP_TIMEOUT_SECONDS = 300.0
REFRESH_TIMEOUT_SECONDS = 60.0


def authentication_error(message: str = "Sign-in failed. Restart and sign in again.") -> AppError:
    return AppError(ErrorCode.AUTHENTICATION_FAILED, message)


@dataclass(frozen=True)
class AccessToken:
    token: str = field(repr=False)
    expires_on: int

    def require_valid(self, minimum_lifetime: float = 0) -> None:
        if (
            type(self.token) is not str
            or not 1 <= len(self.token) <= MAX_TOKEN_LENGTH
            or not self.token.isascii()
            or any(ord(char) <= 32 or ord(char) == 127 for char in self.token)
            or type(self.expires_on) is not int
            or not time.time() + minimum_lifetime < self.expires_on <= time.time() + 86_400
        ):
            raise authentication_error(
                "The sign-in token is missing, expired, or has insufficient lifetime. "
                "Restart and sign in again."
            )


class Deadline(Protocol):
    def current(self) -> float | None: ...


class AuthenticationSession(Protocol):
    @property
    def started(self) -> bool: ...

    def acquire(self, minimum_lifetime: float, deadline: Deadline) -> AccessToken: ...


def _send(connection: Connection, message: dict) -> None:
    payload = json.dumps(message, separators=(",", ":"), allow_nan=False).encode("ascii")
    if len(payload) > MAX_MESSAGE_BYTES:
        raise ValueError("invalid authentication message")
    connection.send_bytes(payload)


def _receive(connection: Connection) -> dict:
    value = json.loads(connection.recv_bytes(MAX_MESSAGE_BYTES))
    if type(value) is not dict:
        raise ValueError("invalid authentication message")
    return value


def _minimum(value: object) -> float:
    if type(value) not in (float, int) or not math.isfinite(value) or not 0 <= value <= 86_435:
        raise ValueError("invalid authentication lifetime")
    return float(value)


def _serve(connection: Connection, config: ProviderConfig, credential_factory) -> None:
    """Child-only credential lifetime; only bounded access tokens cross the pipe."""
    from azure.core.exceptions import ClientAuthenticationError
    from azure.identity import AuthenticationRequiredError

    credential = None
    try:
        validate_authentication(
            config.name, config.authentication, config.tenant_id,
            config.client_id, config.redirect_uri,
        )
        options = dict(
            authority=AUTHORITY,
            tenant_id=config.tenant_id or DEFAULT_TENANT,
            client_id=config.client_id or DEFAULT_CLIENT,
            disable_automatic_authentication=True,
            enable_support_logging=False,
            logging_enable=False,
            use_env_settings=False,
            timeout=STARTUP_TIMEOUT_SECONDS,
        )
        if config.redirect_uri is not None:
            options["redirect_uri"] = config.redirect_uri
        credential = credential_factory(**options)
        first = True
        while True:
            command = _receive(connection)
            if set(command) != {"op", "minimum_lifetime"} or command["op"] != (
                "start" if first else "token"
            ):
                raise ValueError("invalid authentication command")
            minimum = _minimum(command["minimum_lifetime"])
            if first:
                credential.authenticate(scopes=[SCOPE])
                first = False
            sdk_token = credential.get_token(SCOPE)
            token = AccessToken(sdk_token.token, sdk_token.expires_on)
            token.require_valid(minimum)
            with redact_secret(token.token):
                _send(connection, {
                    "status": "ok", "token": token.token, "expires_on": token.expires_on,
                })
            del sdk_token, token
    except AuthenticationRequiredError:
        _send(connection, {"status": "error", "category": "signin_required"})
    except (ClientAuthenticationError, OSError, ValueError, AppError):
        # SDK messages may include claims, account records, or credentials.
        try:
            _send(connection, {"status": "error", "category": "signin_failed"})
        except (OSError, EOFError):
            pass  # Parent cancelled and closed its private pipe.
    finally:
        try:
            if credential is not None:
                credential.close()
        finally:
            connection.close()


def _auth_child(connection: Connection, config: ProviderConfig) -> None:
    # A dedicated process lets us suppress even inherited DEBUG handlers without
    # changing application logging. No SDK exceptions escape to stderr.
    logging.disable(logging.CRITICAL)
    # The CLI owns console cancellation and enforces the shutdown deadline.
    signal.signal(signal.SIGINT, signal.SIG_IGN)
    if hasattr(signal, "SIGBREAK"):
        signal.signal(signal.SIGBREAK, signal.SIG_IGN)
    try:
        from azure.identity import InteractiveBrowserCredential

        _serve(connection, config, InteractiveBrowserCredential)
    except Exception:
        # Last-resort process boundary: never serialize/print an SDK exception.
        try:
            _send(connection, {"status": "error", "category": "signin_failed"})
        except (OSError, EOFError):
            pass
        finally:
            connection.close()


class AuthenticationBroker:
    """One killable, memory-only interactive credential per CLI invocation."""

    def __init__(
        self, config: ProviderConfig, shutdown_timeout: float, *, target=_auth_child,
        on_wait: Callable[[], None] | None = None,
    ):
        self.config = config
        self.shutdown_timeout = shutdown_timeout
        self._target = target
        self.on_wait = on_wait
        self._process = None
        self._connection = None
        self._secrets = ExitStack()
        self._started = False

    @property
    def started(self) -> bool:
        return self._started

    def start(
        self, minimum_lifetime: float, deadline: Deadline, *,
        timeout_seconds: float = STARTUP_TIMEOUT_SECONDS,
    ) -> None:
        if self._process is not None:
            raise authentication_error("The sign-in session has already started.")
        if self.config.authentication != "interactive":
            raise authentication_error("Interactive authentication must be explicitly configured.")
        validate_authentication(
            self.config.name, self.config.authentication, self.config.tenant_id,
            self.config.client_id, self.config.redirect_uri,
        )
        self._check_shutdown(deadline)
        context = multiprocessing.get_context("spawn")
        parent, child = context.Pipe(duplex=True)
        self._connection = parent
        self._process = context.Process(
            target=self._target, args=(child, self.config), name="tcfcomic-auth",
            daemon=False,
        )
        try:
            self._process.start()
            child.close()
            self._exchange("start", minimum_lifetime, min(timeout_seconds, STARTUP_TIMEOUT_SECONDS), deadline)
            self._started = True
        except (OSError, ValueError):
            child.close()
            self.close(deadline.current())
            raise authentication_error("The sign-in subprocess could not be started.") from None
        except BaseException:
            child.close()
            self.close(deadline.current())
            raise

    def _check_shutdown(self, deadline: Deadline) -> None:
        if deadline.current() is not None:
            raise AppError(ErrorCode.SHUTDOWN_INTERRUPTED, "Sign-in was interrupted.")

    def _exchange(
        self, op: str, minimum_lifetime: float, timeout: float, deadline: Deadline,
    ) -> AccessToken:
        try:
            self._check_shutdown(deadline)
            _send(self._connection, {"op": op, "minimum_lifetime": _minimum(minimum_lifetime)})
            expires = time.monotonic() + timeout
            while True:
                self._check_shutdown(deadline)
                if self._connection.poll(0.05):
                    response = _receive(self._connection)
                    break
                if time.monotonic() >= expires:
                    raise authentication_error(
                        "Sign-in timed out. Restart and sign in again in a browser."
                    )
                if not self._process.is_alive():
                    raise authentication_error()
                if self.on_wait is not None:
                    self.on_wait()
            self._check_shutdown(deadline)
            if response == {"status": "error", "category": "signin_required"}:
                raise authentication_error(
                    "Silent token refresh requires sign-in. Restart and sign in again."
                )
            if set(response) != {"status", "token", "expires_on"} or response["status"] != "ok":
                raise authentication_error()
            token = AccessToken(response["token"], response["expires_on"])
            token.require_valid(minimum_lifetime)
            self._secrets.close()
            self._secrets.enter_context(redact_secret(token.token))
            return token
        except (OSError, EOFError, ValueError, TypeError, RecursionError, OverflowError):
            self.close(deadline.current())
            raise authentication_error() from None
        except BaseException:
            self.close(deadline.current())
            raise

    def acquire(
        self, minimum_lifetime: float, deadline: Deadline, *,
        timeout_seconds: float = REFRESH_TIMEOUT_SECONDS,
    ) -> AccessToken:
        if not self.started:
            raise authentication_error("Start interactive sign-in before processing images.")
        return self._exchange("token", minimum_lifetime, min(timeout_seconds, REFRESH_TIMEOUT_SECONDS), deadline)

    def close(self, deadline: float | None = None) -> None:
        self._started = False
        if self._connection is not None:
            self._connection.close()
            self._connection = None
        process = self._process
        if process is not None:
            if process.pid is not None:
                stop_at = deadline if deadline is not None else time.monotonic() + self.shutdown_timeout
                if process.is_alive():
                    process.terminate()
                    process.join(timeout=min(0.1, max(0.0, stop_at - time.monotonic()) / 2))
                if process.is_alive():
                    process.kill()
                process.join(timeout=max(0.0, stop_at - time.monotonic()))
                if process.is_alive():
                    raise authentication_error("The sign-in subprocess could not be stopped.")
                process.close()
            self._process = None
        self._secrets.close()

    def __enter__(self) -> AuthenticationBroker:
        return self

    def __exit__(self, exc_type, exc, traceback) -> None:
        self.close()
