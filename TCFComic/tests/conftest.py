from __future__ import annotations

import logging
import os
import socket
from pathlib import Path

import pytest
from PIL import Image

from tcfcomic.config import load_config

@pytest.fixture(autouse=True)
def no_openai_key(monkeypatch: pytest.MonkeyPatch) -> None:
    for name in tuple(os.environ):
        if name.upper().startswith(("OPENAI_", "AZURE_OPENAI_")):
            monkeypatch.delenv(name, raising=False)


@pytest.fixture(autouse=True)
def deny_external_network(monkeypatch: pytest.MonkeyPatch) -> None:
    original_connect = socket.socket.connect
    original_connect_ex = socket.socket.connect_ex
    original_getaddrinfo = socket.getaddrinfo

    def is_local(address) -> bool:
        if isinstance(address, str):
            return True
        if not isinstance(address, tuple) or not address:
            return False
        host = str(address[0]).strip("[]").lower()
        return host in {"localhost", "::1"} or host.startswith("127.")

    def guarded_connect(sock, address):
        if is_local(address):
            return original_connect(sock, address)
        raise AssertionError(f"external network access is forbidden: {address!r}")

    def guarded_connect_ex(sock, address):
        if is_local(address):
            return original_connect_ex(sock, address)
        raise AssertionError(f"external network access is forbidden: {address!r}")

    def guarded_getaddrinfo(host, port, *args, **kwargs):
        if is_local((host, port)):
            return original_getaddrinfo(host, port, *args, **kwargs)
        raise AssertionError("external DNS resolution is forbidden")

    monkeypatch.setattr(socket.socket, "connect", guarded_connect)
    monkeypatch.setattr(socket.socket, "connect_ex", guarded_connect_ex)
    monkeypatch.setattr(socket, "getaddrinfo", guarded_getaddrinfo)


@pytest.fixture
def quiet_logger() -> logging.Logger:
    logger = logging.getLogger(f"tcfcomic-test-{os.urandom(4).hex()}")
    logger.handlers[:] = [logging.NullHandler()]
    logger.propagate = False
    return logger


def write_image(path: Path, image_format: str = "PNG", color=(20, 40, 60)) -> Path:
    path.parent.mkdir(parents=True, exist_ok=True)
    Image.new("RGB", (24, 18), color).save(path, format=image_format)
    return path


def write_mpo(path: Path, *, noisy: bool = False) -> Path:
    import random

    primary = (
        Image.frombytes("RGB", (256, 192), random.Random(0).randbytes(256 * 192 * 3))
        if noisy else Image.new("RGB", (24, 18), "red")
    )
    frames = [primary, Image.new("RGB", primary.size, "blue"), Image.new("RGB", primary.size, "green")]
    try:
        exif = Image.Exif()
        exif[274] = 6
        exif[270] = "private metadata"
        exif[34853] = {1: "N", 2: (1.0, 2.0, 3.0)}
        primary.save(
            path, format="MPO", save_all=True, append_images=frames[1:], exif=exif,
            quality=30, comment=b"private comment", xmp=b"private xmp",
        )
    finally:
        for frame in frames:
            frame.close()
    return path


def write_config(
    root: Path,
    *,
    provider: str = "fake",
    model: str = "gpt-image-2",
    endpoint: str | None = None,
    source_name: str = "Incoming",
    destination_name: str = "Output",
    stable_seconds: float = 0,
    poll_interval_seconds: float = 0.01,
    max_attempts: int = 3,
    initial_delay_seconds: float = 0,
    max_delay_seconds: float = 0,
    shutdown_timeout_seconds: float = 1,
    max_output_bytes: int = 1_048_576,
) -> tuple[Path, object]:
    source = root / source_name
    destination = root / destination_name
    source.mkdir(parents=True)
    destination.mkdir(parents=True)
    config_path = root / "config.yaml"
    config_path.write_text(
        "\n".join(
            [
                "version: 1",
                "paths:",
                f'  source: "{str(source).replace(chr(92), chr(92) * 2)}"',
                f'  destination: "{str(destination).replace(chr(92), chr(92) * 2)}"',
                "provider:",
                f"  name: {provider}",
                f"  model: {model}",
                *([f"  endpoint: {endpoint}"] if endpoint is not None else []),
                "  request_timeout_seconds: 2",
                "watch:",
                f"  poll_interval_seconds: {poll_interval_seconds}",
                f"  stable_seconds: {stable_seconds}",
                "  recursive: false",
                "retry:",
                f"  max_attempts: {max_attempts}",
                f"  initial_delay_seconds: {initial_delay_seconds}",
                f"  max_delay_seconds: {max_delay_seconds}",
                "shutdown:",
                f"  timeout_seconds: {shutdown_timeout_seconds}",
                "limits:",
                "  max_input_bytes: 1048576",
                f"  max_output_bytes: {max_output_bytes}",
                "  max_width: 1000",
                "  max_height: 1000",
                "  max_pixels: 1000000",
                "logging:",
                "  level: INFO",
                "  rotate_bytes: 100000",
                "  backup_count: 1",
                "",
            ]
        ),
        encoding="utf-8",
    )
    return config_path, load_config(config_path)
