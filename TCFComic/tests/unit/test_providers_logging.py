from __future__ import annotations

import base64
import dataclasses
import json
import logging
import multiprocessing
import sys
import types
from pathlib import Path

import pytest
import httpx
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import (
    AmbiguousProviderError,
    AppError,
    ErrorCode,
    PermanentProviderError,
    RetryableProviderError,
    SourceSnapshot,
    TransformRequest,
)
from tcfcomic.logging_setup import configure_logging, log_event
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.openai import OpenAIProvider
from tcfcomic.providers.worker import (
    InProcessAttemptRunner,
    SubprocessAttemptRunner,
    _execute,
)


def request_for(path: Path) -> TransformRequest:
    info = path.stat()
    snapshot = SourceSnapshot(
        path=path,
        normalized_path=str(path).lower(),
        size=info.st_size,
        mtime_ns=info.st_mtime_ns,
        sha256="12" * 32,
        staged_path=path,
    )
    return TransformRequest("job", snapshot, "safe prompt", "gpt-image-2")


def test_fake_provider_is_deterministic_and_captures_request(tmp_path: Path) -> None:
    source = write_image(tmp_path / "source.png")
    request = request_for(source)
    provider = FakeProvider()
    first = tmp_path / "first.png"
    second = tmp_path / "second.png"
    with first.open("wb") as output:
        provider.transform(request, output)
    with second.open("wb") as output:
        provider.transform(request, output)
    assert first.read_bytes() == second.read_bytes()
    Image.open(first).verify()
    assert provider.requests == [request, request]


def test_fake_provider_scripted_permanent_failure(tmp_path: Path) -> None:
    source = write_image(tmp_path / "source.png")
    with pytest.raises(PermanentProviderError) as caught:
        with (tmp_path / "out").open("wb") as output:
            FakeProvider(["permanent"]).transform(request_for(source), output)
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT


def test_openai_exact_mocked_contract(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source = write_image(tmp_path / "source.png")
    png = tmp_path / "payload.png"
    write_image(png)
    encoded = base64.b64encode(png.read_bytes()).decode("ascii")
    captured: dict[str, object] = {}

    class Images:
        def edit(self, **kwargs):
            captured["edit"] = kwargs
            return types.SimpleNamespace(
                id="a" * 32,
                data=[types.SimpleNamespace(b64_json=encoded)],
            )

    class Client:
        def __init__(self, **kwargs):
            captured["client"] = kwargs
            self.images = Images()

    class HttpClient:
        def __init__(self, **kwargs):
            captured["http_client"] = kwargs

    monkeypatch.setitem(
        sys.modules,
        "openai",
        types.SimpleNamespace(OpenAI=Client, DefaultHttpxClient=HttpClient),
    )
    monkeypatch.setenv("OPENAI_API_KEY", "only-from-env")
    provider = OpenAIProvider(17)
    http_client = captured["client"]["http_client"]
    output = tmp_path / "out.png"
    with output.open("wb") as stream:
        result = provider.transform(request_for(source), stream)
    assert captured["client"] == {
        "api_key": "only-from-env",
        "base_url": "https://api.openai.com/v1",
        "max_retries": 0,
        "timeout": 17,
        "http_client": http_client,
    }
    assert captured["http_client"] == {"trust_env": False, "follow_redirects": False}
    edit = captured["edit"]
    assert set(edit) == {"model", "image", "prompt", "output_format"}
    assert edit["model"] == "gpt-image-2"
    assert edit["output_format"] == "png"
    assert "input_fidelity" not in edit
    assert output.read_bytes() == png.read_bytes()
    assert result.provider_request_id == "a" * 32


def test_logging_redacts_secret_and_payload(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    secret = "sk-test-secret-value"
    monkeypatch.setenv("OPENAI_API_KEY", secret)
    logger = configure_logging(config.logging, config.paths.destination)
    payload = base64.b64encode(b"x" * 100).decode("ascii")
    log_event(
        logger,
        logging.ERROR,
        "safe_test",
        message=f"credential={secret} payload={payload}",
    )
    for handler in logger.handlers:
        handler.flush()
    text = (
        config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl"
    ).read_text(encoding="utf-8")
    assert secret not in text
    assert payload not in text
    decoded = json.loads(text.strip())
    assert decoded["event"] == "safe_test"
    assert "[REDACTED]" in decoded["message"]


def test_logging_rejects_existing_reparse_log_leaf(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    log_path = (
        config.paths.destination / ".tcfcomic" / "logs" / "tcfcomic.jsonl"
    )
    log_path.parent.mkdir(parents=True)
    log_path.write_text("operator-owned", encoding="utf-8")
    import tcfcomic.path_safety as path_safety

    real_is_reparse = path_safety.is_reparse_or_link

    def synthetic_leaf_reparse(path: Path) -> bool:
        return path == log_path or real_is_reparse(path)

    monkeypatch.setattr(
        path_safety,
        "is_reparse_or_link",
        synthetic_leaf_reparse,
    )
    with pytest.raises(AppError) as caught:
        configure_logging(config.logging, config.paths.destination)
    assert caught.value.code == ErrorCode.STATE_FAILED
    assert log_path.read_text(encoding="utf-8") == "operator-owned"


def test_logging_rotation_is_bounded_by_backup_count(tmp_path: Path) -> None:
    _, config = write_config(tmp_path)
    logging_config = dataclasses.replace(
        config.logging,
        rotate_bytes=128,
        backup_count=2,
    )
    logger = configure_logging(logging_config, config.paths.destination)
    try:
        for index in range(20):
            log_event(
                logger,
                logging.INFO,
                "rotation_test",
                message=f"{index:02d}-" + ("x" * 200),
            )
        for handler in logger.handlers:
            handler.flush()
        log_dir = config.paths.destination / ".tcfcomic" / "logs"
        files = list(log_dir.glob("tcfcomic.jsonl*"))
        assert {path.name for path in files} == {
            "tcfcomic.jsonl",
            "tcfcomic.jsonl.1",
            "tcfcomic.jsonl.2",
        }
        assert len(files) == 3
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_logging_reconfiguration_prunes_stale_backups_high_to_one(
    tmp_path: Path,
) -> None:
    _, config = write_config(tmp_path)
    high = dataclasses.replace(config.logging, backup_count=5)
    logger = configure_logging(high, config.paths.destination)
    old_file_handlers = [
        handler
        for handler in logger.handlers
        if isinstance(handler, logging.FileHandler)
    ]
    log_dir = config.paths.destination / ".tcfcomic" / "logs"
    for index in range(1, 8):
        (log_dir / f"tcfcomic.jsonl.{index}").write_text(
            str(index), encoding="utf-8"
        )

    low = dataclasses.replace(config.logging, backup_count=1)
    logger = configure_logging(low, config.paths.destination)
    try:
        assert all(handler.stream is None for handler in old_file_handlers)
        assert {path.name for path in log_dir.iterdir()} == {
            "tcfcomic.jsonl",
            "tcfcomic.jsonl.1",
        }
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_logging_prunes_only_safe_canonical_stale_backups(
    tmp_path: Path,
) -> None:
    _, config = write_config(tmp_path)
    logging_config = dataclasses.replace(config.logging, backup_count=2)
    log_dir = config.paths.destination / ".tcfcomic" / "logs"
    log_dir.mkdir(parents=True)
    preserved = {
        "tcfcomic.jsonl.1",
        "tcfcomic.jsonl.2",
        "tcfcomic.jsonl.0",
        "tcfcomic.jsonl.01",
        "tcfcomic.jsonl.x",
        "TCFCOMIC.jsonl.5",
        "operator-tcfcomic.jsonl.5",
        "tcfcomic.jsonl.6.tmp",
    }
    for name in preserved:
        (log_dir / name).write_text("operator", encoding="utf-8")
    (log_dir / "tcfcomic.jsonl.3").mkdir()
    stale = log_dir / "tcfcomic.jsonl.4"
    stale.write_text("stale", encoding="utf-8")

    logger = configure_logging(logging_config, config.paths.destination)
    try:
        assert not stale.exists()
        assert (log_dir / "tcfcomic.jsonl.3").is_dir()
        assert preserved <= {path.name for path in log_dir.iterdir()}
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_logging_skips_stale_symlink_backup(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    logging_config = dataclasses.replace(config.logging, backup_count=1)
    log_dir = config.paths.destination / ".tcfcomic" / "logs"
    log_dir.mkdir(parents=True)
    link = log_dir / "tcfcomic.jsonl.2"
    link.write_text("operator", encoding="utf-8")
    import tcfcomic.logging_setup as logging_setup_module

    actual = link.stat()

    class SymlinkEntry:
        name = link.name
        path = str(link)

        @staticmethod
        def stat(*, follow_symlinks: bool):
            assert follow_symlinks is False
            return actual

        @staticmethod
        def is_symlink():
            return True

    monkeypatch.setattr(
        logging_setup_module.os, "scandir", lambda path: [SymlinkEntry()]
    )

    logger = configure_logging(logging_config, config.paths.destination)
    try:
        assert link.read_text(encoding="utf-8") == "operator"
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_logging_skips_stale_reparse_backup(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    logging_config = dataclasses.replace(config.logging, backup_count=1)
    log_dir = config.paths.destination / ".tcfcomic" / "logs"
    log_dir.mkdir(parents=True)
    backup = log_dir / "tcfcomic.jsonl.2"
    backup.write_text("operator", encoding="utf-8")
    import tcfcomic.logging_setup as logging_setup_module

    actual = backup.stat()
    reparse_flag = 1024
    monkeypatch.setattr(
        logging_setup_module.stat,
        "FILE_ATTRIBUTE_REPARSE_POINT",
        reparse_flag,
        raising=False,
    )

    class ReparseEntry:
        name = backup.name
        path = str(backup)

        @staticmethod
        def stat(*, follow_symlinks: bool):
            assert follow_symlinks is False
            return types.SimpleNamespace(
                st_mode=actual.st_mode,
                st_file_attributes=reparse_flag,
            )

        @staticmethod
        def is_symlink():
            return False

    monkeypatch.setattr(
        logging_setup_module.os, "scandir", lambda path: [ReparseEntry()]
    )
    logger = configure_logging(logging_config, config.paths.destination)
    try:
        assert backup.read_text(encoding="utf-8") == "operator"
    finally:
        for handler in tuple(logger.handlers):
            logger.removeHandler(handler)
            handler.close()


def test_logging_fails_state_when_stale_backup_cannot_be_removed(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    logging_config = dataclasses.replace(config.logging, backup_count=1)
    log_dir = config.paths.destination / ".tcfcomic" / "logs"
    log_dir.mkdir(parents=True)
    stale = log_dir / "tcfcomic.jsonl.2"
    stale.write_text("stale", encoding="utf-8")
    real_unlink = Path.unlink

    def fail_stale_unlink(path: Path, *args, **kwargs):
        if path == stale:
            raise PermissionError("operator detail")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "unlink", fail_stale_unlink)
    with pytest.raises(AppError) as caught:
        configure_logging(logging_config, config.paths.destination)
    assert caught.value.code == ErrorCode.STATE_FAILED
    assert "operator detail" not in caught.value.safe_message
    assert stale.read_text(encoding="utf-8") == "stale"


def _mock_openai_module(monkeypatch: pytest.MonkeyPatch, edit):
    from openai import APIStatusError, APIConnectionError

    constructed: list[dict[str, object]] = []

    class Images:
        def edit(self, **kwargs):
            try:
                return edit(**kwargs)
            except _StatusError as exc:
                response = httpx.Response(
                    exc.status_code, json={},
                    request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
                )
                raise APIStatusError("synthetic response", response=response, body={}) from exc.__cause__
            except _APIConnectionError as exc:
                raise APIConnectionError(
                    request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
                ) from exc.__cause__

    class Client:
        def __init__(self, **kwargs):
            constructed.append(kwargs)
            self.images = Images()

    class HttpClient:
        def __init__(self, **kwargs):
            self.kwargs = kwargs

    monkeypatch.setitem(
        sys.modules,
        "openai",
        types.SimpleNamespace(
            OpenAI=Client, DefaultHttpxClient=HttpClient,
            APIStatusError=APIStatusError, APIConnectionError=APIConnectionError,
        ),
    )
    return constructed


def test_openai_rejects_hostile_ambient_environment_before_sdk_construction(
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    constructed = _mock_openai_module(
        monkeypatch, lambda **_: (_ for _ in ()).throw(AssertionError("no request"))
    )
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    monkeypatch.setenv("OPENAI_BASE_URL", "http://127.0.0.1:9999/v1")
    monkeypatch.setenv("OPENAI_DEFAULT_HEADERS", '{"X-Evil":"1"}')
    with pytest.raises(PermanentProviderError) as caught:
        OpenAIProvider(5)
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert constructed == []
    assert "127.0.0.1" not in caught.value.safe_message


class _StatusError(Exception):
    def __init__(self, status_code: int) -> None:
        self.status_code = status_code


class _APITimeoutError(Exception):
    pass


class _APIConnectionError(Exception):
    pass


@pytest.mark.parametrize(
    ("failure", "expected"),
    [
        (_APITimeoutError(), AmbiguousProviderError),
        (_APIConnectionError(), AmbiguousProviderError),
        (_StatusError(429), RetryableProviderError),
        (_StatusError(500), AmbiguousProviderError),
        (_StatusError(503), AmbiguousProviderError),
        (_StatusError(400), PermanentProviderError),
        (_StatusError(404), PermanentProviderError),
        (RuntimeError("unknown post-dispatch failure"), AmbiguousProviderError),
    ],
)
def test_openai_failure_classification_is_conservative(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    failure: Exception,
    expected: type[Exception],
) -> None:
    source = write_image(tmp_path / "source.png")

    def fail(**kwargs):
        del kwargs
        raise failure

    _mock_openai_module(monkeypatch, fail)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    with pytest.raises(expected):
        with (tmp_path / "out").open("wb") as output:
            provider.transform(request_for(source), output)


@pytest.mark.parametrize("status_code", [409, 429])
def test_openai_retries_transient_http_4xx_statuses(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    status_code: int,
) -> None:
    source = write_image(tmp_path / "source.png")

    def fail(**kwargs):
        del kwargs
        raise _StatusError(status_code)

    _mock_openai_module(monkeypatch, fail)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    with pytest.raises(RetryableProviderError) as caught:
        with (tmp_path / "out").open("wb") as output:
            provider.transform(request_for(source), output)
    assert caught.value.code == ErrorCode.PROVIDER_RETRYABLE


@pytest.mark.parametrize("status_code", [408, *range(500, 600)])
def test_openai_treats_paid_or_uncertain_http_failures_as_ambiguous(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    status_code: int,
) -> None:
    source = write_image(tmp_path / "source.png")

    def fail(**kwargs):
        del kwargs
        raise _StatusError(status_code)

    _mock_openai_module(monkeypatch, fail)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    with pytest.raises(AmbiguousProviderError) as caught:
        with (tmp_path / "out").open("wb") as output:
            provider.transform(request_for(source), output)
    assert caught.value.code == ErrorCode.PROVIDER_AMBIGUOUS


@pytest.mark.parametrize("status_code", [408, 500, 599])
def test_openai_http_ambiguity_precedes_nested_connect_failure(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    status_code: int,
) -> None:
    source = write_image(tmp_path / "source.png")
    failure = _StatusError(status_code)
    failure.__cause__ = httpx.ConnectError(
        "synthetic pre-dispatch-looking cause",
        request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
    )

    def fail(**kwargs):
        del kwargs
        raise failure

    _mock_openai_module(monkeypatch, fail)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    with pytest.raises(AmbiguousProviderError) as caught:
        with (tmp_path / "out").open("wb") as output:
            provider.transform(request_for(source), output)
    assert caught.value.code == ErrorCode.PROVIDER_AMBIGUOUS


@pytest.mark.parametrize(
    ("cause", "expected"),
    [
        (
            httpx.ConnectError(
                "synthetic secret connect failure",
                request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
            ),
            RetryableProviderError,
        ),
        (
            httpx.ConnectTimeout(
                "synthetic secret connect timeout",
                request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
            ),
            RetryableProviderError,
        ),
        (
            httpx.ReadTimeout(
                "synthetic secret read timeout",
                request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
            ),
            AmbiguousProviderError,
        ),
        (
            httpx.WriteError(
                "synthetic secret write failure",
                request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
            ),
            AmbiguousProviderError,
        ),
        (
            httpx.RemoteProtocolError(
                "synthetic secret protocol failure",
                request=httpx.Request("POST", "https://api.openai.com/v1/images/edits"),
            ),
            AmbiguousProviderError,
        ),
    ],
)
def test_openai_distinguishes_pre_dispatch_connect_from_uncertain_failures(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    cause: Exception,
    expected: type[Exception],
) -> None:
    source = write_image(tmp_path / "source.png")
    failure = _APIConnectionError("outer secret must not escape")
    failure.__cause__ = cause

    def fail(**kwargs):
        del kwargs
        raise failure

    _mock_openai_module(monkeypatch, fail)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    with pytest.raises(expected) as caught:
        with (tmp_path / "out").open("wb") as output:
            provider.transform(request_for(source), output)
    assert "secret" not in caught.value.safe_message


def test_openai_malformed_and_encoded_or_decoded_oversize_are_without_partial(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    source = write_image(tmp_path / "source.png")
    responses = iter(
        [
            "%%%",
            base64.b64encode(b"x" * 128).decode("ascii"),
            base64.b64encode(b"x" * 5).decode("ascii"),
        ]
    )

    def respond(**kwargs):
        del kwargs
        return types.SimpleNamespace(
            id="request",
            data=[types.SimpleNamespace(b64_json=next(responses))],
        )

    _mock_openai_module(monkeypatch, respond)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    malformed = tmp_path / "malformed"
    with pytest.raises(PermanentProviderError):
        with malformed.open("wb") as output:
            provider.transform(request_for(source), output)
    assert malformed.read_bytes() == b""

    oversized = tmp_path / "oversized"
    request = dataclasses.replace(request_for(source), max_output_bytes=16)
    with pytest.raises(PermanentProviderError) as caught:
        with oversized.open("wb") as output:
            provider.transform(request, output)
    assert caught.value.code == ErrorCode.OUTPUT_LIMIT_EXCEEDED
    assert oversized.read_bytes() == b""

    decoded_oversized = tmp_path / "decoded-oversized"
    request = dataclasses.replace(request_for(source), max_output_bytes=4)
    with pytest.raises(PermanentProviderError) as caught:
        with decoded_oversized.open("wb") as output:
            provider.transform(request, output)
    assert caught.value.code == ErrorCode.OUTPUT_LIMIT_EXCEEDED
    assert decoded_oversized.read_bytes() == b""


@pytest.mark.parametrize(
    "data",
    [
        None,
        (),
        [],
        [types.SimpleNamespace(b64_json=None)],
        [types.SimpleNamespace(b64_json=b"bytes")],
        [types.SimpleNamespace(b64_json=123)],
    ],
)
def test_openai_malformed_response_types_are_permanent(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, data
) -> None:
    source = write_image(tmp_path / "source.png")

    def respond(**kwargs):
        del kwargs
        return types.SimpleNamespace(id="request", data=data)

    _mock_openai_module(monkeypatch, respond)
    monkeypatch.setenv("OPENAI_API_KEY", "key")
    provider = OpenAIProvider(5)
    target = tmp_path / "malformed"
    with pytest.raises(PermanentProviderError) as caught:
        with target.open("wb") as output:
            provider.transform(request_for(source), output)
    assert caught.value.code == ErrorCode.PROVIDER_PERMANENT
    assert target.read_bytes() == b""


class _TypeErrorProvider:
    def transform(self, request, output):
        del request, output
        raise TypeError("malformed provider response")


def _malformed_response_child(provider, request, temp_path, result_queue):
    del provider
    result_queue.put(_execute(_TypeErrorProvider(), request, temp_path))


def test_subprocess_runner_sanitizes_malformed_provider_response(
    tmp_path: Path,
) -> None:
    source = write_image(tmp_path / "source.png")
    _, config = write_config(tmp_path / "config")
    result = SubprocessAttemptRunner(target=_malformed_response_child).run(
        config.provider,
        request_for(source),
        tmp_path / "attempt.tmp",
        None,
    )
    assert result.outcome == "permanent"
    assert result.error_code == ErrorCode.PROVIDER_PERMANENT
    assert result.safe_message == "The provider worker failed safely."
    assert not (tmp_path / "attempt.tmp").exists()
    assert multiprocessing.active_children() == []


def test_subprocess_runner_captures_provider_constructor_error(
    tmp_path: Path,
) -> None:
    source = write_image(tmp_path / "source.png")
    _, config = write_config(tmp_path / "config", provider="openai")
    result = SubprocessAttemptRunner().run(
        config.provider,
        request_for(source),
        tmp_path / "attempt.tmp",
        None,
    )
    assert result.outcome == "permanent"
    assert result.error_code == ErrorCode.CREDENTIAL_MISSING
    assert "OPENAI_API_KEY" in result.safe_message
    assert not (tmp_path / "attempt.tmp").exists()
    assert multiprocessing.active_children() == []


def test_worker_enforces_provider_output_bound_and_removes_temp(
    tmp_path: Path,
) -> None:
    class OversizedProvider:
        def transform(self, request, output):
            output.write(b"x" * 32)
            raise AssertionError("bounded writer should reject first")

    source = write_image(tmp_path / "source.png")
    request = dataclasses.replace(request_for(source), max_output_bytes=8)
    temp = tmp_path / "attempt.tmp"
    result = InProcessAttemptRunner(OversizedProvider()).run(
        types.SimpleNamespace(), request, temp, None
    )
    assert result.outcome == "permanent"
    assert result.error_code == ErrorCode.OUTPUT_LIMIT_EXCEEDED
    assert not temp.exists()
