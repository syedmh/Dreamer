from __future__ import annotations

import json
import os
import signal
import sqlite3
import subprocess
import sys
import threading
import time
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.domain import StableCandidate
from tcfcomic.processor import ProcessResult, Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner


def run_cli(project: Path, *args: str, env=None) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-m", "tcfcomic", *args],
        cwd=project,
        env=env,
        text=True,
        encoding="utf-8",
        capture_output=True,
        timeout=30,
        check=False,
    )


def test_exact_validate_and_process_commands(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(
        tmp_path, source_name="Incoming Images", destination_name="Output Ünicode"
    )
    source = write_image(config.paths.source / "Mixed Case.JPEG", "JPEG")
    validated = run_cli(project, "validate", "--config", str(config_path))
    assert validated.returncode == 0, validated.stderr
    summary = json.loads(validated.stdout)
    assert summary["provider"] == "fake"

    processed = run_cli(
        project, "process", "--config", str(config_path), str(source)
    )
    assert processed.returncode == 0, processed.stderr
    output = Path(processed.stdout.strip())
    assert output.exists()
    Image.open(output).verify()


def test_cli_invalid_input_and_missing_openai_key(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    fake_path, fake_config = write_config(tmp_path / "fake")
    unsupported = fake_config.paths.source / "notes.txt"
    unsupported.write_text("hello", encoding="utf-8")
    result = run_cli(
        project, "process", "--config", str(fake_path), str(unsupported)
    )
    assert result.returncode == 4
    assert "UNSUPPORTED_FILE" in result.stderr

    openai_path, openai_config = write_config(tmp_path / "openai", provider="openai")
    source = write_image(openai_config.paths.source / "photo.png")
    env = os.environ.copy()
    env.pop("OPENAI_API_KEY", None)
    result = run_cli(
        project,
        "process",
        "--config",
        str(openai_path),
        str(source),
        env=env,
    )
    assert result.returncode == 3
    assert "OPENAI_API_KEY" in result.stderr


def test_cli_non_string_yaml_key_and_non_directory_destination_exit_two(
    tmp_path: Path,
) -> None:
    project = Path(__file__).parents[2]
    non_string = tmp_path / "non-string.yaml"
    non_string.write_text("1: value\n", encoding="utf-8")
    result = run_cli(project, "validate", "--config", str(non_string))
    assert result.returncode == 2
    assert result.stderr.startswith("CONFIG_INVALID:")
    assert "mapping keys must be strings" in result.stderr

    config_path, config = write_config(tmp_path / "destination")
    destination_file = tmp_path / "destination" / "occupied.file"
    destination_file.write_text("occupied", encoding="utf-8")
    text = config_path.read_text(encoding="utf-8")
    old = str(config.paths.destination).replace("\\", "\\\\")
    new = str(destination_file).replace("\\", "\\\\")
    config_path.write_text(text.replace(old, new), encoding="utf-8")
    result = run_cli(project, "validate", "--config", str(config_path))
    assert result.returncode == 2
    assert result.stderr.startswith("CONFIG_INVALID:")
    assert "paths.destination" in result.stderr


def test_validate_does_not_require_openai_key(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    config_path, _ = write_config(tmp_path, provider="openai")
    result = run_cli(project, "validate", "--config", str(config_path))
    assert result.returncode == 0
    assert "openai" in result.stdout


def test_validate_never_attempts_writable_probe_or_cleanup(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    import tcfcomic.cli as cli_module

    config_path, config = write_config(tmp_path)
    real_open = Path.open
    real_unlink = Path.unlink
    probes = []
    before = sorted(str(path.relative_to(tmp_path)) for path in tmp_path.rglob("*"))

    def fail_writable_probe(path: Path, *args, **kwargs):
        if path.name.startswith(".tcfcomic-access-"):
            probes.append("open")
            raise PermissionError("synthetic access denial")
        return real_open(path, *args, **kwargs)

    def fail_probe_cleanup(path: Path, *args, **kwargs):
        if path.name.startswith(".tcfcomic-access-"):
            probes.append("unlink")
            raise PermissionError("synthetic cleanup denial")
        return real_unlink(path, *args, **kwargs)

    monkeypatch.setattr(Path, "open", fail_writable_probe)
    monkeypatch.setattr(Path, "unlink", fail_probe_cleanup)
    assert cli_module.main(["validate", "--config", str(config_path)]) == 0
    captured = capsys.readouterr()
    assert json.loads(captured.out)["destination"] == str(config.paths.destination)
    assert captured.err == ""
    assert probes == []
    assert sorted(str(path.relative_to(tmp_path)) for path in tmp_path.rglob("*")) == before


@pytest.mark.parametrize(
    "command_args",
    [
        ("validate",),
        ("process", "ignored.png"),
        ("watch",),
    ],
)
def test_quarantine_file_fails_before_runtime_construction(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
    command_args: tuple[str, ...],
) -> None:
    import tcfcomic.cli as cli_module

    config_path, config = write_config(tmp_path)
    quarantine_file = config.paths.destination / "occupied.quarantine"
    quarantine_file.write_text("occupied", encoding="utf-8")
    escaped = str(quarantine_file).replace("\\", "\\\\")
    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "provider:",
            f'  quarantine: "{escaped}"\nprovider:',
        ),
        encoding="utf-8",
    )
    calls: list[str] = []

    def unexpected_credentials(*args, **kwargs) -> None:
        del args, kwargs
        calls.append("credentials")

    def unexpected_logging(*args, **kwargs):
        del args, kwargs
        calls.append("logging")
        raise AssertionError("logging must not be configured")

    class UnexpectedProcessor:
        def __init__(self, *args, **kwargs) -> None:
            del args, kwargs
            calls.append("processor")
            raise AssertionError("Processor must not be constructed")

    monkeypatch.setattr(
        cli_module,
        "require_provider_credentials",
        unexpected_credentials,
    )
    monkeypatch.setattr(cli_module, "configure_logging", unexpected_logging)
    monkeypatch.setattr(cli_module, "Processor", UnexpectedProcessor)
    argv = [
        command_args[0],
        "--config",
        str(config_path),
        *command_args[1:],
    ]
    assert cli_module.main(argv) == 2
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err.startswith("CONFIG_INVALID:")
    assert "paths.quarantine" in captured.err
    assert calls == []


@pytest.mark.parametrize(
    "command_args",
    [
        ("validate",),
        ("process", "ignored.png"),
        ("watch",),
    ],
)
def test_validate_is_readonly_and_runtime_lock_failure_stops_later_phases(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
    command_args: tuple[str, ...],
) -> None:
    import tcfcomic.cli as cli_module

    config_path, config = write_config(tmp_path)
    calls: list[str] = []
    probes: list[str] = []
    original_unlink = Path.unlink
    original_open = Path.open
    before = sorted(str(path.relative_to(tmp_path)) for path in tmp_path.rglob("*"))

    def deny_quarantine_probe_cleanup(
        path: Path,
        missing_ok: bool = False,
    ) -> None:
        if (
            path.parent == config.paths.quarantine
            and path.name.startswith(".tcfcomic-access-")
        ):
            probes.append("unlink")
            raise PermissionError("synthetic cleanup denial")
        original_unlink(path, missing_ok=missing_ok)

    def deny_probe_creation(path: Path, *args, **kwargs):
        if path.name.startswith(".tcfcomic-access-"):
            probes.append("open")
            raise PermissionError("synthetic access denial")
        return original_open(path, *args, **kwargs)

    def deny_runtime_lock(self):
        calls.append("lock")
        raise AppError(ErrorCode.STATE_FAILED, "Synthetic runtime lock unavailable.")

    def unexpected_credentials(*args, **kwargs) -> None:
        del args, kwargs
        calls.append("credentials")

    def unexpected_logging(*args, **kwargs):
        del args, kwargs
        calls.append("logging")
        raise AssertionError("logging must not be configured")

    class UnexpectedProcessor:
        def __init__(self, *args, **kwargs) -> None:
            del args, kwargs
            calls.append("processor")
            raise AssertionError("Processor must not be constructed")

    monkeypatch.setattr(Path, "unlink", deny_quarantine_probe_cleanup)
    monkeypatch.setattr(Path, "open", deny_probe_creation)
    monkeypatch.setattr(cli_module.DestinationSession, "lock_internal", deny_runtime_lock)
    monkeypatch.setattr(
        cli_module,
        "require_provider_credentials",
        unexpected_credentials,
    )
    monkeypatch.setattr(cli_module, "configure_logging", unexpected_logging)
    monkeypatch.setattr(cli_module, "Processor", UnexpectedProcessor)
    argv = [
        command_args[0],
        "--config",
        str(config_path),
        *command_args[1:],
    ]

    assert cli_module.main(argv) == (0 if command_args[0] == "validate" else 6)
    captured = capsys.readouterr()
    assert probes == []
    if command_args[0] == "validate":
        assert json.loads(captured.out)["quarantine"] == str(config.paths.quarantine)
        assert captured.err == ""
        assert calls == []
        assert sorted(str(path.relative_to(tmp_path)) for path in tmp_path.rglob("*")) == before
    else:
        assert captured.out == ""
        assert captured.err == "STATE_FAILED: Synthetic runtime lock unavailable.\n"
        assert calls == ["credentials", "lock"]


@pytest.mark.parametrize(
    ("error_code", "status", "attempts", "expected_exit"),
    [
        (ErrorCode.INVALID_IMAGE, JobStatus.FAILED, 0, 4),
        (ErrorCode.IMAGE_LIMIT_EXCEEDED, JobStatus.FAILED, 0, 4),
        (ErrorCode.PROVIDER_PERMANENT, JobStatus.FAILED, 1, 5),
        (ErrorCode.PROVIDER_AMBIGUOUS, JobStatus.AMBIGUOUS, 1, 5),
        (ErrorCode.OUTPUT_LIMIT_EXCEEDED, JobStatus.FAILED, 1, 5),
        (ErrorCode.OUTPUT_INVALID, JobStatus.FAILED, 1, 5),
        (ErrorCode.PUBLICATION_FAILED, JobStatus.FAILED, 1, 6),
        (ErrorCode.STATE_FAILED, JobStatus.FAILED, 1, 6),
    ],
)
def test_cli_terminal_results_use_shared_error_mapping(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    error_code: ErrorCode,
    status: JobStatus,
    attempts: int,
    expected_exit: int,
) -> None:
    import tcfcomic.cli as cli_module

    config_path, config = write_config(tmp_path)
    source = write_image(config.paths.source / "fault.png")

    class StubProcessor:
        def __init__(self, *args, **kwargs) -> None:
            del args, kwargs

        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc, traceback) -> None:
            del exc_type, exc, traceback

        def process_path(self, path, *, shutdown_event):
            del path, shutdown_event
            return ProcessResult(
                "a" * 32,
                status,
                None,
                attempts,
                error_code,
            )

    monkeypatch.setattr(cli_module, "Processor", StubProcessor)
    assert (
        cli_module.main(
            ["process", "--config", str(config_path), str(source)]
        )
        == expected_exit
    )


def test_cli_request_identity_drift_exits_six_without_provider_attempt(
    tmp_path: Path,
    quiet_logger,
) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(tmp_path)
    source = write_image(config.paths.source / "identity-drift.png")
    event = threading.Event()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as processor:
        info = source.stat()
        admitted = processor._admit(
            source,
            candidate=StableCandidate(
                source,
                info.st_size,
                info.st_mtime_ns,
            ),
            shutdown_event=event,
        )
        original = processor.state.get_job(admitted.job_id)

    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "  model: gpt-image-2",
            "  model: changed-model",
        ),
        encoding="utf-8",
    )
    completed = run_cli(
        project,
        "process",
        "--config",
        str(config_path),
        str(source),
    )

    assert completed.returncode == 6, (completed.stdout, completed.stderr)
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        row = connection.execute(
            """
            SELECT status, error_code, provider, model, prompt_hash
              FROM jobs
             WHERE job_id = ?
            """,
            (admitted.job_id,),
        ).fetchone()
        attempts = connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?",
            (admitted.job_id,),
        ).fetchone()[0]
    finally:
        connection.close()
    assert row == (
        JobStatus.FAILED.value,
        ErrorCode.STATE_FAILED.value,
        original.provider,
        original.model,
        original.prompt_hash,
    )
    assert attempts == 0
    record = json.loads(
        next(config.paths.quarantine.glob("*.json")).read_text(
            encoding="utf-8"
        )
    )
    assert record["stage"] == "state"


def test_cli_reduced_limit_restart_maps_durable_input_failure_to_exit_four(
    tmp_path: Path,
    quiet_logger,
) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / "target-reduced-limit.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / "unrelated-ready.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as first:
        target = first._admit(
            target_source,
            candidate=StableCandidate(
                target_source,
                target_source.stat().st_size,
                target_source.stat().st_mtime_ns,
            ),
            shutdown_event=event,
        )
        unrelated = first._admit(
            unrelated_source,
            candidate=StableCandidate(
                unrelated_source,
                unrelated_source.stat().st_size,
                unrelated_source.stat().st_mtime_ns,
            ),
            shutdown_event=event,
        )
        target_stage = Path(first.state.get_job(target.job_id).staged_path)
        unrelated_stage = Path(first.state.get_job(unrelated.job_id).staged_path)
        durable_size = target_stage.stat().st_size

    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "  max_input_bytes: 1048576",
            f"  max_input_bytes: {durable_size - 1}",
        ),
        encoding="utf-8",
    )

    completed = run_cli(
        project,
        "process",
        "--config",
        str(config_path),
        str(target_source),
    )

    assert completed.returncode == 4, (completed.stdout, completed.stderr)
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        target_row = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (target.job_id,),
        ).fetchone()
        unrelated_row = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (unrelated.job_id,),
        ).fetchone()
        target_attempts = connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?",
            (target.job_id,),
        ).fetchone()[0]
        unrelated_attempts = connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?",
            (unrelated.job_id,),
        ).fetchone()[0]
    finally:
        connection.close()

    assert target_row == (
        JobStatus.FAILED.value,
        ErrorCode.IMAGE_LIMIT_EXCEEDED.value,
    )
    assert unrelated_row == (JobStatus.READY.value, None)
    assert target_attempts == 0
    assert unrelated_attempts == 0
    assert not target_stage.exists()
    assert unrelated_stage.exists()
    assert list(config.paths.destination.glob("*.png")) == []


def test_cli_request_identity_precedes_reduced_input_limit(
    tmp_path: Path,
    quiet_logger,
) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(tmp_path)
    target_source = write_image(
        config.paths.source / "target-identity-and-limit.png",
        color=(1, 2, 3),
    )
    unrelated_source = write_image(
        config.paths.source / "unrelated-identity-and-limit.png",
        color=(4, 5, 6),
    )
    event = threading.Event()
    with Processor(
        config,
        quiet_logger,
        runner=InProcessAttemptRunner(FakeProvider()),
    ) as first:
        target = first._admit(
            target_source,
            candidate=StableCandidate(
                target_source,
                target_source.stat().st_size,
                target_source.stat().st_mtime_ns,
            ),
            shutdown_event=event,
        )
        unrelated = first._admit(
            unrelated_source,
            candidate=StableCandidate(
                unrelated_source,
                unrelated_source.stat().st_size,
                unrelated_source.stat().st_mtime_ns,
            ),
            shutdown_event=event,
        )
        original = first.state.get_job(target.job_id)
        target_stage = Path(original.staged_path)
        unrelated_stage = Path(first.state.get_job(unrelated.job_id).staged_path)
        durable_size = target_stage.stat().st_size

    config_path.write_text(
        config_path.read_text(encoding="utf-8")
        .replace(
            "  model: gpt-image-2",
            "  model: changed-model",
        )
        .replace(
            "  max_input_bytes: 1048576",
            f"  max_input_bytes: {durable_size - 1}",
        ),
        encoding="utf-8",
    )

    completed = run_cli(
        project,
        "process",
        "--config",
        str(config_path),
        str(target_source),
    )

    assert completed.returncode == 6, (completed.stdout, completed.stderr)
    connection = sqlite3.connect(
        config.paths.destination / ".tcfcomic" / "state.db"
    )
    try:
        target_row = connection.execute(
            """
            SELECT status, error_code, provider, model, prompt_hash
              FROM jobs
             WHERE job_id = ?
            """,
            (target.job_id,),
        ).fetchone()
        unrelated_row = connection.execute(
            "SELECT status, error_code FROM jobs WHERE job_id = ?",
            (unrelated.job_id,),
        ).fetchone()
        target_attempts = connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?",
            (target.job_id,),
        ).fetchone()[0]
        unrelated_attempts = connection.execute(
            "SELECT COUNT(*) FROM attempts WHERE job_id = ?",
            (unrelated.job_id,),
        ).fetchone()[0]
    finally:
        connection.close()

    assert target_row == (
        JobStatus.FAILED.value,
        ErrorCode.STATE_FAILED.value,
        original.provider,
        original.model,
        original.prompt_hash,
    )
    assert unrelated_row == (JobStatus.READY.value, None)
    assert target_attempts == 0
    assert unrelated_attempts == 0
    assert not target_stage.exists()
    assert unrelated_stage.exists()
    assert list(config.paths.destination.glob("*.png")) == []


def test_cli_fake_provider_output_limit_exits_five_without_final_output(
    tmp_path: Path,
) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(tmp_path, max_output_bytes=16)
    source = write_image(config.paths.source / "valid.png")

    result = run_cli(
        project,
        "process",
        "--config",
        str(config_path),
        str(source),
    )

    assert result.returncode == 5, (result.stdout, result.stderr)
    assert list(config.paths.destination.glob("*.png")) == []


def test_cli_unexpected_internal_failure_exits_six(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
    capsys: pytest.CaptureFixture[str],
) -> None:
    import tcfcomic.cli as cli_module

    config_path, config = write_config(tmp_path)
    source = write_image(config.paths.source / "internal.png")

    class FailingProcessor:
        def __init__(self, *args, **kwargs) -> None:
            del args, kwargs
            raise RuntimeError("synthetic internal failure")

    monkeypatch.setattr(cli_module, "Processor", FailingProcessor)
    assert (
        cli_module.main(
            ["process", "--config", str(config_path), str(source)]
        )
        == 6
    )
    captured = capsys.readouterr()
    assert captured.out == ""
    assert captured.err.startswith("STATE_FAILED:")
    assert "synthetic internal failure" not in captured.err


def test_redirected_stdout_round_trips_unicode_output_path(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(
        tmp_path,
        source_name="输入 Images",
        destination_name="Résultats 漫画",
    )
    source = write_image(config.paths.source / "人物 Ünicode.PNG")
    completed = subprocess.run(
        [
            sys.executable,
            "-m",
            "tcfcomic",
            "process",
            "--config",
            str(config_path),
            str(source),
        ],
        cwd=project,
        capture_output=True,
        timeout=30,
        check=False,
    )
    assert completed.returncode == 0, completed.stderr.decode("utf-8")
    output_text = completed.stdout.decode("utf-8").strip()
    assert "Résultats 漫画" in output_text
    output = Path(output_text)
    assert output.is_file()
    Image.open(output).verify()


def _wait_for_path(path: Path, process: subprocess.Popen[str]) -> None:
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        if path.exists():
            return
        if process.poll() is not None:
            stdout, stderr = process.communicate()
            raise AssertionError(
                f"CLI exited before startup handshake: {process.returncode}\n"
                f"stdout={stdout}\nstderr={stderr}"
            )
        time.sleep(0.02)
    raise AssertionError(f"CLI startup handshake was not created: {path}")


@pytest.mark.skipif(os.name != "nt", reason="Windows console signal contract")
def test_process_console_signal_during_stability_wait_exits_130(
    tmp_path: Path,
) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(
        tmp_path, stable_seconds=60, poll_interval_seconds=0.05
    )
    source = write_image(config.paths.source / "waiting.png")
    process = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "tcfcomic",
            "process",
            "--config",
            str(config_path),
            str(source),
        ],
        cwd=project,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
    )
    _wait_for_path(
        config.paths.destination / ".tcfcomic" / "runtime.lock", process
    )
    process.send_signal(signal.CTRL_BREAK_EVENT)
    stdout, stderr = process.communicate(timeout=15)
    assert process.returncode == 130, (stdout, stderr)


@pytest.mark.skipif(os.name != "nt", reason="Windows console signal contract")
def test_watch_console_signal_exits_zero(tmp_path: Path) -> None:
    project = Path(__file__).parents[2]
    config_path, config = write_config(
        tmp_path, poll_interval_seconds=0.05
    )
    process = subprocess.Popen(
        [
            sys.executable,
            "-m",
            "tcfcomic",
            "watch",
            "--config",
            str(config_path),
        ],
        cwd=project,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=subprocess.CREATE_NEW_PROCESS_GROUP,
    )
    _wait_for_path(
        config.paths.destination / ".tcfcomic" / "runtime.lock", process
    )
    process.send_signal(signal.CTRL_BREAK_EVENT)
    stdout, stderr = process.communicate(timeout=15)
    assert process.returncode == 0, (stdout, stderr)
