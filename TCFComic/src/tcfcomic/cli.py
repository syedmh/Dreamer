from __future__ import annotations

import argparse
import json
import signal
import sys
import threading
import time
from contextlib import ExitStack
from pathlib import Path
from typing import Sequence

from .config import (
    load_config,
    require_provider_credentials,
    require_failed_variant_retry_policy,
    sanitized_config_summary,
)
from .domain import AppError, ErrorCode, JobStatus
from .logging_setup import configure_logging
from .processor import Processor
from .authentication import AuthenticationBroker, TOKEN_ALLOWANCE_SECONDS
from .providers.worker import ShutdownDeadline
from .runtime import DestinationSession
from .auth_probe import PROBE_TIMEOUT_SECONDS, check_auth, validate_probe


def _configure_redirected_stdio() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            redirected = not stream.isatty()
        except (AttributeError, OSError):
            redirected = False
        reconfigure = getattr(stream, "reconfigure", None)
        if redirected and callable(reconfigure):
            reconfigure(encoding="utf-8", errors="strict")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="tcfcomic")
    subcommands = parser.add_subparsers(dest="command", required=True)

    validate = subcommands.add_parser("validate", help="validate configuration")
    validate.add_argument("--config", required=True, type=Path)

    check = subcommands.add_parser(
        "check-auth",
        help="make one read-only Azure catalog GET; no images or queue access",
        description=(
            "Check Azure catalog access once, without retries or queue access. "
            "Success does not prove image-edit permission or deployment availability."
        ),
    )
    check.add_argument("--config", required=True, type=Path)

    process = subcommands.add_parser("process", help="process one source image")
    process.add_argument("--config", required=True, type=Path)
    process.add_argument(
        "--retry-input-rejection", action="store_true",
        help=(
            "retry an eligible JPEG-named MPO previously rejected at input with zero "
            "historical attempts, using current request settings; not force reprocessing"
        ),
    )
    process.add_argument("image", type=Path)

    watch = subcommands.add_parser("watch", help="watch the configured source folder")
    watch.add_argument("--config", required=True, type=Path)
    watch.add_argument(
        "--reset-state", action="store_true",
        help="archive all internal history and reprocess all incoming images, causing additional billable requests; never deletes prior outputs",
    )
    watch.add_argument(
        "--retry-input-rejection", action="store_true",
        help="reconsider eligible never-sent JPEG-named MPO input rejections using current settings",
    )
    for command in (process, watch):
        command.add_argument(
            "--retry-failed-variants", action="store_true",
            help=(
                "explicitly retry eligible failed Azure responses using CURRENT prompts; "
                "retains lifetime attempts and skips successes; may incur additional charges"
            ),
        )
    return parser


def _exit_code_for_error_code(code: ErrorCode) -> int:
    if code in {ErrorCode.CREDENTIAL_MISSING, ErrorCode.AUTHENTICATION_FAILED}:
        return 3
    if code in {
        ErrorCode.SOURCE_OUTSIDE_ROOT,
        ErrorCode.SOURCE_CHANGED,
        ErrorCode.UNSUPPORTED_FILE,
        ErrorCode.INVALID_IMAGE,
        ErrorCode.IMAGE_LIMIT_EXCEEDED,
    }:
        return 4
    if code in {
        ErrorCode.PROVIDER_RETRYABLE,
        ErrorCode.PROVIDER_PERMANENT,
        ErrorCode.PROVIDER_AMBIGUOUS,
        ErrorCode.OUTPUT_LIMIT_EXCEEDED,
        ErrorCode.OUTPUT_INVALID,
    }:
        return 5
    if code == ErrorCode.CONFIG_INVALID:
        return 2
    return 6


def _exit_code(error: AppError) -> int:
    return _exit_code_for_error_code(error.code)


def main(argv: Sequence[str] | None = None) -> int:
    _configure_redirected_stdio()
    args = build_parser().parse_args(argv)
    try:
        config = load_config(args.config, prepare_paths=False)
        if args.command == "validate":
            print(json.dumps(sanitized_config_summary(config), sort_keys=True))
            return 0

        if args.command == "check-auth":
            validate_probe(config)
        elif args.retry_failed_variants:
            require_failed_variant_retry_policy(config)
            if args.command == "watch" and args.reset_state:
                raise AppError(
                    ErrorCode.CONFIG_INVALID,
                    "--retry-failed-variants cannot be combined with --reset-state; recovery retains history.",
                )
        require_provider_credentials(config)
        shutdown_event = threading.Event()
        signal_received = threading.Event()
        previous_handlers: dict[int, object] = {}

        def request_shutdown(signum, frame) -> None:
            del signum, frame
            signal_received.set()
            shutdown_event.set()

        handled_signals = [signal.SIGINT, signal.SIGTERM]
        if hasattr(signal, "SIGBREAK"):
            handled_signals.append(signal.SIGBREAK)
        for item in handled_signals:
            previous_handlers[item] = signal.getsignal(item)
            signal.signal(item, request_shutdown)
        try:
            with ExitStack() as stack:
                if args.command == "check-auth":
                    timeout = min(PROBE_TIMEOUT_SECONDS, config.provider.request_timeout_seconds)
                    expires = time.monotonic() + timeout
                    token = None
                    if config.provider.authentication == "interactive":
                        auth = stack.enter_context(AuthenticationBroker(
                            config.provider, min(config.shutdown.timeout_seconds, timeout),
                        ))
                        print("Browser sign-in is starting. Complete Microsoft sign-in in your browser.", file=sys.stderr)
                        deadline = ShutdownDeadline(shutdown_event.is_set, min(config.shutdown.timeout_seconds, timeout))
                        auth.start(timeout + TOKEN_ALLOWANCE_SECONDS, deadline, timeout_seconds=timeout)
                        remaining = expires - time.monotonic()
                        if remaining <= 0:
                            raise AppError(ErrorCode.AUTHENTICATION_FAILED, "The bounded catalog sign-in check timed out.")
                        token = auth.acquire(
                            remaining + TOKEN_ALLOWANCE_SECONDS, deadline, timeout_seconds=remaining,
                        )
                    summary = check_auth(
                        config, shutdown_event, access_token=token,
                        timeout=expires - time.monotonic(),
                    )
                    if signal_received.is_set():
                        return 130
                    print(summary)
                    return 0
                runtime = stack.enter_context(DestinationSession(config.paths.destination))
                if args.command == "watch" and args.reset_state:
                    runtime.reset(config, shutdown_event, report=lambda message: print(message, file=sys.stderr))
                runtime.preflight(named=config.provider.prompts is not None)
                runtime.lock_internal()
                logger = configure_logging(config.logging, config.paths.destination)
                def close_logging() -> None:
                    for handler in tuple(logger.handlers):
                        logger.removeHandler(handler)
                        handler.close()
                stack.callback(close_logging)
                processor_options = {"runtime": runtime}
                if config.provider.authentication == "interactive":
                    auth = stack.enter_context(AuthenticationBroker(
                        config.provider, config.shutdown.timeout_seconds,
                    ))
                    print("Browser sign-in is starting. Complete Microsoft sign-in in your browser.", file=sys.stderr)
                    auth.start(
                        config.provider.request_timeout_seconds + TOKEN_ALLOWANCE_SECONDS,
                        ShutdownDeadline(shutdown_event.is_set, config.shutdown.timeout_seconds),
                    )
                    processor_options["auth_session"] = auth
                processor = stack.enter_context(Processor(config, logger, **processor_options))
                if args.command == "process":
                    try:
                        retry_options = {"retry_input_rejection": True} if args.retry_input_rejection else {}
                        if args.retry_failed_variants:
                            retry_options["retry_failed_variants"] = True
                        if config.provider.prompts is not None:
                            results = processor.process_variants(
                                args.image, shutdown_event=shutdown_event,
                                on_result=lambda result: print(str(result.output_path))
                                if result.status == JobStatus.SUCCEEDED else None,
                                **retry_options,
                            )
                        else:
                            results = (processor.process_path(
                                args.image, shutdown_event=shutdown_event, **retry_options,
                            ),)
                    except AppError as exc:
                        if (
                            signal_received.is_set()
                            and exc.code == ErrorCode.SHUTDOWN_INTERRUPTED
                        ):
                            return 130
                        raise
                    if signal_received.is_set():
                        return 130
                    failure_code = 0
                    for result in results:
                        if result.status == JobStatus.SUCCEEDED:
                            if config.provider.prompts is None:
                                print(str(result.output_path))
                        elif not failure_code:
                            failure_code = (
                                _exit_code_for_error_code(result.error_code)
                                if result.error_code is not None else
                                4 if result.status == JobStatus.FAILED and result.attempts == 0 else 5
                            )
                    return failure_code
                watch_options = {}
                if args.retry_failed_variants:
                    watch_options["retry_failed_variants"] = True
                if args.retry_input_rejection:
                    watch_options["retry_input_rejection"] = True
                processor.watch(shutdown_event=shutdown_event, **watch_options)
                return 0
        except AppError as exc:
            if exc.code == ErrorCode.SHUTDOWN_INTERRUPTED and signal_received.is_set():
                return 0 if args.command == "watch" else 130
            raise
        except KeyboardInterrupt:
            return 0 if args.command == "watch" else 130
        finally:
            for item, handler in previous_handlers.items():
                signal.signal(item, handler)
    except KeyboardInterrupt:
        return 130
    except AppError as exc:
        print(f"{exc.code.value}: {exc.safe_message}", file=sys.stderr)
        return _exit_code(exc)
    except Exception:
        print(
            f"{ErrorCode.STATE_FAILED.value}: TCFComic stopped because of an internal failure.",
            file=sys.stderr,
        )
        return 6
