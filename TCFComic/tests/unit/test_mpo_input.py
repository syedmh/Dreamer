from __future__ import annotations

import base64
import hashlib
import io
from dataclasses import replace
from email import policy
from email.parser import BytesParser

import httpx
import openai
import pytest
from PIL import Image, ImageOps

from conftest import write_config, write_mpo
from tcfcomic.domain import AppError, ErrorCode, PermanentProviderError, SourceSnapshot, TransformRequest
from tcfcomic.image_io import validate_input, validate_output_png
from tcfcomic.providers.openai import OpenAIProvider


def request_for(path, staged):
    data = path.read_bytes()
    staged.write_bytes(data)
    info = path.stat()
    return TransformRequest(
        "a" * 32,
        SourceSnapshot(path, str(path), len(data), info.st_mtime_ns, hashlib.sha256(data).hexdigest(), staged),
        "private prompt", "offline-model",
    )


@pytest.fixture
def sdk(monkeypatch):
    captured = []
    clients = []
    with io.BytesIO() as buffer:
        with Image.new("RGB", (2, 2)) as image:
            image.save(buffer, format="PNG")
        encoded = base64.b64encode(buffer.getvalue()).decode()

    def dispatch(request):
        request.read()
        captured.append(request)
        return httpx.Response(200, json={"data": [{"b64_json": encoded}]})

    def client(**kwargs):
        result = httpx.Client(transport=httpx.MockTransport(dispatch), **kwargs)
        clients.append(result)
        return result

    monkeypatch.setattr(openai, "DefaultHttpxClient", client)
    monkeypatch.setenv("OPENAI_API_KEY", "synthetic-key")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "synthetic-key")
    yield captured
    for client in clients:
        client.close()


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("suffix", [".JPG", ".jPeG"])
def test_mpo_primary_sdk_multipart_pixels_orientation_and_metadata(tmp_path, sdk, azure, suffix):
    _, config = write_config(tmp_path)
    source = write_mpo(config.paths.source / ("synthetic" + suffix))
    staged = tmp_path / "staged.input"
    request = request_for(source, staged)
    original = source.read_bytes()
    assert validate_input(staged, config.limits, source.name).format == "MPO"
    with Image.open(source) as image:
        assert image.format == "MPO" and image.n_frames == 3
        assert image.getexif().get(34853) is not None
        image.seek(0)
        with ImageOps.exif_transpose(image) as oriented, oriented.convert("RGB") as expected:
            expected_pixels = expected.tobytes()
            expected_size = expected.size
    provider = OpenAIProvider(2, azure_endpoint="https://offline.openai.azure.com" if azure else None)
    with io.BytesIO() as output:
        provider.transform(request, output)
    message = BytesParser(policy=policy.default).parsebytes(
        f"Content-Type: {sdk[0].headers['content-type']}\r\n\r\n".encode() + sdk[0].content
    )
    parts = {part.get_param("name", header="content-disposition"): part for part in message.iter_parts()}
    part = parts["image"]
    assert part.get_filename() == "input.png"
    assert part.get_content_type() == "image/png"
    payload = part.get_payload(decode=True)
    with io.BytesIO(payload) as buffer, Image.open(buffer) as image:
        assert image.format == "PNG" and getattr(image, "n_frames", 1) == 1
        assert image.mode == "RGB" and image.size == expected_size == (18, 24)
        assert image.tobytes() == expected_pixels
        assert image.info == {} and not image.getexif()
    assert b"private" not in payload
    assert source.read_bytes() == staged.read_bytes() == original


@pytest.mark.parametrize("source_name", [None, "renamed.png", "renamed.webp", "renamed.mpo"])
def test_mpo_requires_original_jpeg_identity(tmp_path, source_name):
    _, config = write_config(tmp_path)
    path = write_mpo(tmp_path / "staged.input")
    with pytest.raises(AppError) as caught:
        validate_input(path, config.limits, source_name)
    assert caught.value.code == ErrorCode.INVALID_IMAGE
    with pytest.raises(AppError):
        validate_output_png(path, config.limits)


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("limit", ["bytes", "width", "height", "pixels", "oriented_height", "encoded_bytes"])
def test_mpo_limits_before_http(tmp_path, sdk, azure, limit):
    source = write_mpo(tmp_path / "synthetic.JPG", noisy=limit == "encoded_bytes")
    request = request_for(source, tmp_path / "staged.input")
    changes = {
        "bytes": {"max_input_bytes": source.stat().st_size - 1},
        "width": {"max_width": 23},
        "height": {"max_height": 17},
        "pixels": {"max_pixels": 431},
        "oriented_height": {"max_width": 24, "max_height": 18},
        "encoded_bytes": {"max_input_bytes": source.stat().st_size},
    }[limit]
    provider = OpenAIProvider(2, azure_endpoint="https://offline.openai.azure.com" if azure else None)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(replace(request, **changes), output)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
    assert sdk == []


@pytest.mark.parametrize("azure", [False, True])
@pytest.mark.parametrize("kind", ["corrupt", "wrong_identity", "PNG", "WEBP", "GIF"])
def test_unsupported_or_corrupt_input_before_http(tmp_path, sdk, azure, kind):
    source = tmp_path / "synthetic.JPG"
    if kind in {"corrupt", "wrong_identity"}:
        write_mpo(source)
        if kind == "corrupt":
            data = source.read_bytes()
            source.write_bytes(data[:data.index(b"\xff\xda") + 12])
    else:
        with Image.new("RGB", (20, 20), "red") as first, Image.new("RGB", (20, 20), "blue") as second:
            first.save(source, format=kind, save_all=True, append_images=[second], duration=100, loop=0)
    request = request_for(source, tmp_path / "staged.input")
    if kind == "wrong_identity":
        request = replace(request, source=replace(request.source, path=tmp_path / "pretend.png"))
    provider = OpenAIProvider(2, azure_endpoint="https://offline.openai.azure.com" if azure else None)
    with io.BytesIO() as output, pytest.raises(PermanentProviderError) as caught:
        provider.transform(request, output)
    assert caught.value.code == ErrorCode.INVALID_IMAGE
    assert sdk == []


def test_mpo_decompression_limit_before_decode(tmp_path, monkeypatch):
    _, config = write_config(tmp_path)
    source = write_mpo(tmp_path / "synthetic.JPG")
    monkeypatch.setattr(Image, "MAX_IMAGE_PIXELS", 100)
    with pytest.raises(AppError) as caught:
        validate_input(source, config.limits)
    assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
