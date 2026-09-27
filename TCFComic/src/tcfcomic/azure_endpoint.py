from __future__ import annotations

import re

AZURE_API_VERSION = "preview"

_ENDPOINT = re.compile(
    r"https://[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.openai\.azure\.com/?",
    re.IGNORECASE | re.ASCII,
)


def canonical_azure_endpoint(value: object) -> str:
    # Match the raw URL before any parser can discard or normalize hostile input.
    if type(value) is not str or _ENDPOINT.fullmatch(value) is None:
        raise ValueError(
            "expected an HTTPS Azure resource root endpoint "
            "(https://RESOURCE.openai.azure.com)"
        )
    return value.lower().rstrip("/")
