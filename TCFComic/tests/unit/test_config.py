from __future__ import annotations

import os
from pathlib import Path

import pytest

from conftest import write_config
from tcfcomic.config import (
    DEFAULT_PROMPT,
    MAX_CONFIG_BYTES,
    load_config,
    require_provider_credentials,
    sanitized_config_summary,
)
from tcfcomic.domain import AppError, ErrorCode


def test_validate_valid_config_and_defaults(tmp_path: Path) -> None:
    config_path, config = write_config(tmp_path)
    loaded = load_config(config_path)
    summary = sanitized_config_summary(loaded)
    assert loaded.provider.model == "gpt-image-2"
    assert "Studio Ghibli-inspired" in DEFAULT_PROMPT
    assert "affiliat" not in DEFAULT_PROMPT.lower()
    assert summary["provider"] == "fake"
    assert summary["watch_mode"] == "non-recursive"
    assert "prompt" not in summary


@pytest.mark.parametrize(
    "replacement, field",
    [
        ("version: true", "version"),
        ("  name: mystery", "provider.name"),
        ("  recursive: true", "watch.recursive"),
        ("  max_attempts: true", "retry.max_attempts"),
        ("provider:\n  name: fake\n  api_key: secret", "provider"),
    ],
)
def test_strict_schema_and_exact_types(
    tmp_path: Path, replacement: str, field: str
) -> None:
    config_path, _ = write_config(tmp_path)
    text = config_path.read_text(encoding="utf-8")
    if replacement.startswith("provider:"):
        start = text.index("provider:")
        end = text.index("watch:")
        text = text[:start] + replacement + "\n" + text[end:]
    elif replacement.startswith("version:"):
        text = text.replace("version: 1", replacement)
    elif "name:" in replacement:
        text = text.replace("  name: fake", replacement)
    elif "recursive:" in replacement:
        text = text.replace("  recursive: false", replacement)
    else:
        text = text.replace("  max_attempts: 3", replacement)
    config_path.write_text(text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert field in caught.value.safe_message


def test_malformed_and_missing_paths_are_actionable(tmp_path: Path) -> None:
    malformed = tmp_path / "bad.yaml"
    malformed.write_text("version: [", encoding="utf-8")
    with pytest.raises(AppError, match="malformed YAML"):
        load_config(malformed)

    config_path, config = write_config(tmp_path / "valid")
    config.paths.source.rmdir()
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert "paths.source" in caught.value.safe_message


def test_same_or_overlapping_paths_rejected(tmp_path: Path) -> None:
    config_path, config = write_config(tmp_path)
    text = config_path.read_text(encoding="utf-8")
    source = str(config.paths.source).replace("\\", "\\\\")
    destination = str(config.paths.destination).replace("\\", "\\\\")
    config_path.write_text(text.replace(destination, source), encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert "paths.destination" in caught.value.safe_message


def test_quarantine_must_be_beneath_destination(tmp_path: Path) -> None:
    config_path, _ = write_config(tmp_path)
    escaped = str((tmp_path / "elsewhere").resolve()).replace("\\", "\\\\")
    text = config_path.read_text(encoding="utf-8").replace(
        "provider:", f'  quarantine: "{escaped}"\nprovider:'
    )
    config_path.write_text(text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert "paths.quarantine" in caught.value.safe_message


def test_quarantine_must_be_directory_and_missing_path_is_created(
    tmp_path: Path,
) -> None:
    config_path, config = write_config(tmp_path)
    quarantine_file = config.paths.destination / "occupied.quarantine"
    quarantine_file.write_text("occupied", encoding="utf-8")
    escaped_file = str(quarantine_file).replace("\\", "\\\\")
    text = config_path.read_text(encoding="utf-8").replace(
        "provider:", f'  quarantine: "{escaped_file}"\nprovider:'
    )
    config_path.write_text(text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "paths.quarantine" in caught.value.safe_message
    assert "existing directory" in caught.value.safe_message

    missing = config.paths.destination / "custom" / "quarantine"
    escaped_missing = str(missing).replace("\\", "\\\\")
    config_path.write_text(
        text.replace(escaped_file, escaped_missing),
        encoding="utf-8",
    )
    assert not missing.exists()
    loaded = load_config(config_path)
    assert loaded.paths.quarantine == missing.resolve(strict=True)
    assert missing.is_dir()


def test_quarantine_creation_accepts_directory_created_concurrently(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config_path, config = write_config(tmp_path)
    missing = config.paths.destination / "concurrent" / "quarantine"
    escaped_missing = str(missing).replace("\\", "\\\\")
    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "provider:",
            f'  quarantine: "{escaped_missing}"\nprovider:',
        ),
        encoding="utf-8",
    )
    original_mkdir = Path.mkdir
    created_concurrently = False

    def mkdir_with_concurrent_creation(
        path: Path,
        mode: int = 0o777,
        parents: bool = False,
        exist_ok: bool = False,
    ) -> None:
        nonlocal created_concurrently
        if path == missing and not created_concurrently:
            created_concurrently = True
            original_mkdir(path, mode=mode, parents=parents)
        original_mkdir(path, mode=mode, parents=parents, exist_ok=exist_ok)

    monkeypatch.setattr(Path, "mkdir", mkdir_with_concurrent_creation)

    loaded = load_config(config_path)

    assert created_concurrently
    assert loaded.paths.quarantine == missing.resolve(strict=True)
    assert missing.is_dir()


def test_quarantine_probe_cleanup_failure_is_config_invalid(
    tmp_path: Path,
    monkeypatch: pytest.MonkeyPatch,
) -> None:
    config_path, config = write_config(tmp_path)
    original_unlink = Path.unlink

    def deny_quarantine_probe_cleanup(
        path: Path,
        missing_ok: bool = False,
    ) -> None:
        if (
            path.parent == config.paths.quarantine
            and path.name.startswith(".tcfcomic-access-")
        ):
            raise PermissionError("synthetic cleanup denial")
        original_unlink(path, missing_ok=missing_ok)

    monkeypatch.setattr(Path, "unlink", deny_quarantine_probe_cleanup)

    with pytest.raises(AppError) as caught:
        load_config(config_path)

    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "paths.quarantine" in caught.value.safe_message
    assert "directory is not writable" in caught.value.safe_message


def test_logging_backup_count_must_be_positive(tmp_path: Path) -> None:
    config_path, _ = write_config(tmp_path)
    config_path.write_text(
        config_path.read_text(encoding="utf-8").replace(
            "  backup_count: 1",
            "  backup_count: 0",
        ),
        encoding="utf-8",
    )
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "logging.backup_count" in caught.value.safe_message


def test_openai_credential_preflight_only_uses_environment(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path, provider="openai")
    with pytest.raises(AppError) as caught:
        require_provider_credentials(config)
    assert caught.value.code == ErrorCode.CREDENTIAL_MISSING
    assert "OPENAI_API_KEY" in caught.value.safe_message
    monkeypatch.setenv("OPENAI_API_KEY", "test-value")
    require_provider_credentials(config)


@pytest.mark.parametrize("yaml_value", [".nan", ".inf", "-.inf"])
def test_non_finite_numbers_are_rejected(tmp_path: Path, yaml_value: str) -> None:
    config_path, _ = write_config(tmp_path)
    text = config_path.read_text(encoding="utf-8").replace(
        "  request_timeout_seconds: 2",
        f"  request_timeout_seconds: {yaml_value}",
    )
    config_path.write_text(text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert "provider.request_timeout_seconds" in caught.value.safe_message
    assert "finite" in caught.value.safe_message


def test_numeric_upper_bounds_are_rejected(tmp_path: Path) -> None:
    config_path, _ = write_config(tmp_path)
    text = config_path.read_text(encoding="utf-8").replace(
        "  max_attempts: 3", "  max_attempts: 1000000"
    )
    config_path.write_text(text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert "retry.max_attempts" in caught.value.safe_message


@pytest.mark.parametrize(
    "yaml_text",
    [
        "1: value\n",
        "version: 1\npaths:\n  2: value\n",
    ],
)
def test_non_string_yaml_keys_are_actionable_config_errors(
    tmp_path: Path, yaml_text: str
) -> None:
    config_path = tmp_path / "non-string.yaml"
    config_path.write_text(yaml_text, encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "mapping keys must be strings" in caught.value.safe_message


def test_yaml_size_alias_and_depth_limits_apply_before_schema(
    tmp_path: Path,
) -> None:
    oversized = tmp_path / "oversized.yaml"
    oversized.write_bytes(b"#" * (MAX_CONFIG_BYTES + 1))
    with pytest.raises(AppError) as caught:
        load_config(oversized)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "byte limit" in caught.value.safe_message

    aliases = tmp_path / "aliases.yaml"
    aliases.write_text(
        "version: &value 1\naliases: ["
        + ",".join("*value" for _ in range(33))
        + "]\n",
        encoding="utf-8",
    )
    with pytest.raises(AppError) as caught:
        load_config(aliases)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "unsafe complexity" in caught.value.safe_message

    deep = tmp_path / "deep.yaml"
    deep.write_text(
        "version: 1\nextra: " + ("[" * 40) + "0" + ("]" * 40) + "\n",
        encoding="utf-8",
    )
    with pytest.raises(AppError) as caught:
        load_config(deep)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "unsafe complexity" in caught.value.safe_message


def test_destination_must_be_directory(tmp_path: Path) -> None:
    config_path, config = write_config(tmp_path)
    destination_file = tmp_path / "not-a-directory"
    destination_file.write_text("occupied", encoding="utf-8")
    text = config_path.read_text(encoding="utf-8")
    old = str(config.paths.destination).replace("\\", "\\\\")
    new = str(destination_file).replace("\\", "\\\\")
    config_path.write_text(text.replace(old, new), encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(config_path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID
    assert "paths.destination" in caught.value.safe_message
    assert "existing directory" in caught.value.safe_message
