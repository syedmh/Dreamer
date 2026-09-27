from __future__ import annotations

from collections.abc import Iterable
from io import BytesIO
from typing import BinaryIO

from PIL import Image

from ..domain import (
    AmbiguousProviderError,
    ErrorCode,
    PermanentProviderError,
    ProviderResult,
    RetryableProviderError,
    TransformRequest,
)


class FakeProvider:
    """Deterministic offline provider with optional scripted outcomes."""

    def __init__(self, outcomes: Iterable[str] | None = None) -> None:
        self._outcomes = list(outcomes or ())
        self.requests: list[TransformRequest] = []

    def transform(self, request: TransformRequest, output: BinaryIO) -> ProviderResult:
        self.requests.append(request)
        outcome = self._outcomes.pop(0) if self._outcomes else "success"
        if outcome == "retryable":
            raise RetryableProviderError(
                ErrorCode.PROVIDER_RETRYABLE, "The provider is temporarily unavailable."
            )
        if outcome == "permanent":
            raise PermanentProviderError(
                ErrorCode.PROVIDER_PERMANENT, "The provider rejected the request."
            )
        if outcome == "ambiguous":
            raise AmbiguousProviderError(
                ErrorCode.PROVIDER_AMBIGUOUS, "The provider result is uncertain."
            )
        if outcome == "corrupt":
            output.write(b"not-a-png")
            return ProviderResult("fake-corrupt", 9, "image/png")

        color = tuple(bytes.fromhex(request.source.sha256[:6]))
        image = Image.new("RGB", (32, 32), color)
        buffer = BytesIO()
        image.save(buffer, format="PNG")
        payload = buffer.getvalue()
        output.write(payload)
        return ProviderResult(
            provider_request_id=f"fake-{request.source.sha256[:12]}",
            output_size=len(payload),
            media_type="image/png",
        )

