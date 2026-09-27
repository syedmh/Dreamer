from __future__ import annotations

import warnings
from pathlib import Path

from PIL import Image, UnidentifiedImageError

from .config import LimitsConfig
from .domain import AppError, ErrorCode, ImageInfo

_INPUT_FORMATS = {"JPEG", "PNG", "WEBP"}


def validate_dimensions(
    width: int, height: int, limits: LimitsConfig, *, output: bool = False,
) -> None:
    if (
        width > limits.max_width or height > limits.max_height
        or width * height > limits.max_pixels
    ):
        raise AppError(
            ErrorCode.OUTPUT_LIMIT_EXCEEDED if output else ErrorCode.IMAGE_LIMIT_EXCEEDED,
            "The image dimensions exceed the configured limits.",
        )


def _validate(
    path: Path, limits: LimitsConfig, *, output: bool, source_name: str | None = None,
) -> ImageInfo:
    byte_limit = limits.max_output_bytes if output else limits.max_input_bytes
    limit_error = (
        ErrorCode.OUTPUT_LIMIT_EXCEEDED
        if output
        else ErrorCode.IMAGE_LIMIT_EXCEEDED
    )
    try:
        size = path.stat().st_size
    except OSError:
        raise AppError(
            ErrorCode.OUTPUT_INVALID if output else ErrorCode.INVALID_IMAGE,
            "The image file cannot be read.",
        ) from None
    if size > byte_limit:
        raise AppError(
            limit_error,
            "The image exceeds the configured byte limit.",
        )
    try:
        with warnings.catch_warnings():
            warnings.simplefilter("error", Image.DecompressionBombWarning)
            with Image.open(path) as image:
                image_format = image.format
                width, height = image.size
                validate_dimensions(width, height, limits, output=output)
                frames = getattr(image, "n_frames", 1)
                expected = {"PNG"} if output else _INPUT_FORMATS
                primary_mpo = (
                    not output and image_format == "MPO"
                    and Path(source_name if source_name is not None else path.name).suffix.lower()
                    in {".jpg", ".jpeg"}
                )
                if not primary_mpo and (image_format not in expected or frames != 1):
                    raise AppError(
                        ErrorCode.OUTPUT_INVALID if output else ErrorCode.INVALID_IMAGE,
                        "The output is not a single-frame PNG image."
                        if output
                        else "The input image format or frame count is not supported.",
                    )
                if primary_mpo:
                    image.seek(0)
                    if image.getexif().get(274) in {5, 6, 7, 8}:
                        validate_dimensions(height, width, limits)
                image.verify()
            with Image.open(path) as image:
                image.seek(0)
                width, height = image.size
                validate_dimensions(width, height, limits, output=output)
                image.load()
    except AppError:
        raise
    except (Image.DecompressionBombWarning, Image.DecompressionBombError):
        raise AppError(
            limit_error,
            "The image dimensions exceed the configured limits.",
        ) from None
    except (OSError, ValueError, SyntaxError, UnidentifiedImageError):
        raise AppError(
            ErrorCode.OUTPUT_INVALID if output else ErrorCode.INVALID_IMAGE,
            "The output is not a valid PNG image."
            if output
            else "The input does not contain a valid supported image.",
        ) from None
    return ImageInfo(image_format, width, height, size)


def validate_input(
    path: Path, limits: LimitsConfig, source_name: str | None = None,
) -> ImageInfo:
    return _validate(path, limits, output=False, source_name=source_name)


def validate_output_png(path: Path, limits: LimitsConfig) -> ImageInfo:
    return _validate(path, limits, output=True)
