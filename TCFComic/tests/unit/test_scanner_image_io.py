from __future__ import annotations

import hashlib
import os
import threading
from pathlib import Path

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import (
    AppError,
    ErrorCode,
    ShutdownToken,
)
from tcfcomic.image_io import validate_input, validate_output_png
from tcfcomic.scanner import (
    FolderScanner,
    StableCandidate,
    stage_candidate,
    wait_until_stable,
)


def test_scanner_stability_supported_case_and_nonrecursive(tmp_path: Path) -> None:
    source = tmp_path / "source"
    source.mkdir()
    image = write_image(source / "Picture.JpEg", "JPEG")
    (source / "ignored.txt").write_text("x", encoding="utf-8")
    write_image(source / "child" / "nested.png")
    scanner = FolderScanner(source, stable_seconds=3)
    assert scanner.observe(0) == ()
    image.write_bytes(image.read_bytes() + b"\0")
    assert scanner.observe(2) == ()
    assert scanner.observe(4) == ()
    candidates = scanner.observe(7)
    assert [item.path.name for item in candidates] == ["Picture.JpEg"]
    scanner.acknowledge(candidates[0])
    assert scanner.observe(10) == ()


@pytest.mark.parametrize(
    ("suffix", "image_format"),
    [(".jpg", "JPEG"), (".PNG", "PNG"), (".webp", "WEBP")],
)
def test_jpeg_png_webp_validate_and_stage(
    tmp_path: Path, suffix: str, image_format: str
) -> None:
    _, config = write_config(tmp_path / "case")
    source = write_image(config.paths.source / f"photo{suffix}", image_format)
    before = hashlib.sha256(source.read_bytes()).hexdigest()
    info = source.stat()
    snapshot = stage_candidate(
        StableCandidate(source, info.st_size, info.st_mtime_ns),
        config.paths.destination / ".tcfcomic" / "staging" / "job.input",
        config.limits.max_input_bytes,
        ShutdownToken(lambda: False),
    )
    decoded = validate_input(snapshot.staged_path, config.limits)
    assert decoded.format == image_format
    assert snapshot.path == Path(os.path.abspath(source))
    assert snapshot.normalized_path == os.path.normcase(
        str(Path(os.path.abspath(source)))
    )
    assert snapshot.sha256 == before
    assert hashlib.sha256(source.read_bytes()).hexdigest() == before


def test_stage_candidate_honors_shutdown_after_final_source_stat(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path / "case")
    source = write_image(config.paths.source / "photo.png")
    info = source.stat()
    staged_path = config.paths.destination / ".tcfcomic" / "staging" / "job.input"
    shutdown_event = threading.Event()
    original_stat = Path.stat

    def patched_stat(self: Path, *args, **kwargs):
        follow_symlinks = kwargs.get("follow_symlinks", True)
        result = original_stat(self, *args, **kwargs)
        if self == source and follow_symlinks:
            shutdown_event.set()
        return result

    monkeypatch.setattr(Path, "stat", patched_stat)
    with pytest.raises(AppError) as caught:
        stage_candidate(
            StableCandidate(source, info.st_size, info.st_mtime_ns),
            staged_path,
            config.limits.max_input_bytes,
            ShutdownToken(shutdown_event.is_set),
        )
    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert shutdown_event.is_set()
    assert not staged_path.exists()


def test_corrupt_and_wrong_output_rejected(tmp_path: Path) -> None:
    _, config = write_config(tmp_path)
    corrupt = config.paths.source / "bad.png"
    corrupt.write_bytes(b"not an image")
    with pytest.raises(AppError) as caught:
        validate_input(corrupt, config.limits)
    assert caught.value.code == ErrorCode.INVALID_IMAGE

    jpeg = write_image(config.paths.destination / "wrong.tmp", "JPEG")
    with pytest.raises(AppError) as caught:
        validate_output_png(jpeg, config.limits)
    assert caught.value.code == ErrorCode.OUTPUT_INVALID


def test_dimension_and_byte_limits(tmp_path: Path) -> None:
    _, config = write_config(tmp_path)
    input_image = write_image(config.paths.source / "large.png")
    input_limited = type(config.limits)(
        max_input_bytes=10,
        max_output_bytes=1_000_000,
        max_width=1_000,
        max_height=1_000,
        max_pixels=1_000_000,
    )
    with pytest.raises(AppError) as caught:
        validate_input(input_image, input_limited)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED

    output_image = write_image(config.paths.destination / "large.png")
    output_limited = type(config.limits)(
        max_input_bytes=1_000_000,
        max_output_bytes=10,
        max_width=1_000,
        max_height=1_000,
        max_pixels=1_000_000,
    )
    with pytest.raises(AppError) as caught:
        validate_output_png(output_image, output_limited)
    assert caught.value.code == ErrorCode.OUTPUT_LIMIT_EXCEEDED

    dimension_limited = type(config.limits)(
        max_input_bytes=1_000_000,
        max_output_bytes=1_000_000,
        max_width=10,
        max_height=10,
        max_pixels=100,
    )
    with pytest.raises(AppError) as caught:
        validate_input(input_image, dimension_limited)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
    with pytest.raises(AppError) as caught:
        validate_output_png(output_image, dimension_limited)
    assert caught.value.code == ErrorCode.OUTPUT_LIMIT_EXCEEDED

    pixel_limited = type(config.limits)(
        max_input_bytes=1_000_000,
        max_output_bytes=1_000_000,
        max_width=1_000,
        max_height=1_000,
        max_pixels=100,
    )
    with pytest.raises(AppError) as caught:
        validate_input(input_image, pixel_limited)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
    with pytest.raises(AppError) as caught:
        validate_output_png(output_image, pixel_limited)
    assert caught.value.code == ErrorCode.OUTPUT_LIMIT_EXCEEDED


def test_wait_until_stable_honors_shutdown(tmp_path: Path) -> None:
    image = write_image(tmp_path / "image.png")
    event = threading.Event()
    event.set()
    with pytest.raises(AppError) as caught:
        wait_until_stable(image, 3, 1, ShutdownToken(event.is_set))
    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED


def test_wait_until_stable_interrupts_during_wait(tmp_path: Path) -> None:
    image = write_image(tmp_path / "image.png")
    event = threading.Event()
    waits: list[float] = []

    def interrupt(timeout: float) -> bool:
        waits.append(timeout)
        event.set()
        return True

    with pytest.raises(AppError) as caught:
        wait_until_stable(
            image,
            stable_seconds=3,
            poll_interval_seconds=3_600,
            shutdown=ShutdownToken(event.is_set, interrupt),
        )

    assert caught.value.code == ErrorCode.SHUTDOWN_INTERRUPTED
    assert waits == [3]


def test_dimension_limit_is_checked_before_full_load(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch
) -> None:
    _, config = write_config(tmp_path)
    source = write_image(config.paths.source / "large.png")
    limited = type(config.limits)(
        max_input_bytes=1_000_000,
        max_output_bytes=1_000_000,
        max_width=10,
        max_height=10,
        max_pixels=100,
    )

    def forbidden_load(self):
        raise AssertionError("full decode should not occur after header limit failure")

    monkeypatch.setattr(Image.Image, "load", forbidden_load)
    with pytest.raises(AppError) as caught:
        validate_input(source, limited)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED


@pytest.mark.parametrize("output", [False, True])
def test_decompression_bomb_error_is_safely_classified(
    tmp_path: Path, monkeypatch: pytest.MonkeyPatch, output: bool
) -> None:
    _, config = write_config(tmp_path)
    path = write_image(
        (config.paths.destination if output else config.paths.source) / "bomb.png"
    )

    def bomb(*args, **kwargs):
        raise Image.DecompressionBombError("hostile dimensions")

    monkeypatch.setattr(Image, "open", bomb)
    with pytest.raises(AppError) as caught:
        (validate_output_png if output else validate_input)(path, config.limits)
    assert caught.value.code == (
        ErrorCode.OUTPUT_LIMIT_EXCEEDED
        if output
        else ErrorCode.IMAGE_LIMIT_EXCEEDED
    )
