from __future__ import annotations

import json
import os

from .domain import ErrorCode, PermanentProviderError

MAX_ERROR_BYTES = 65_536
ACCESS_CODE_HINTS = {
    "AuthenticationTypeDisabled": "The resource reported that this authentication type is disabled. Contact the resource administrator or use approved interactive authentication.",
    "KeyBasedAuthenticationNotPermitted": "The resource reported that key-based authentication is not permitted. Contact the resource administrator or use approved interactive authentication.",
    "NetworkAccessDenied": "The resource reported a network access restriction. Review approved network access with the resource administrator.",
    "ForbiddenByFirewall": "The resource reported a network access restriction. Review approved network access with the resource administrator.",
}


def require_supported_environment(*, azure: bool) -> None:
    if any(
        (name.upper().startswith("OPENAI_") and name.upper() != "OPENAI_API_KEY")
        or (azure and name.upper().startswith("AZURE_OPENAI_")
            and name.upper() != "AZURE_OPENAI_API_KEY")
        for name in os.environ
    ):
        raise PermanentProviderError(
            ErrorCode.PROVIDER_PERMANENT,
            "Unsupported ambient Azure OpenAI configuration is present; only API key environment variables are allowed."
            if azure else
            "Unsupported ambient OpenAI configuration is present; only OPENAI_API_KEY is allowed.",
        )


def access_hint(status: int, authentication: str, body: bytes = b"") -> str:
    """Recognize only bounded literal resource diagnostics; never return wire text."""
    if len(body) <= MAX_ERROR_BYTES:
        text = body.decode("utf-8", errors="replace")
        try:
            payload = json.loads(text)
        except (ValueError, RecursionError):
            payload = None
        nodes = [payload] if type(payload) is dict else []
        if nodes and type(payload.get("error")) is dict:
            nodes.append(payload["error"])
        for node in nodes:
            for field in ("code", "type"):
                code = node.get(field)
                if type(code) is str and code in ACCESS_CODE_HINTS:
                    return ACCESS_CODE_HINTS[code]
        messages = [node.get("message") for node in nodes]
        if payload is None:
            messages.append(text)
        for message in messages:
            if type(message) is not str:
                continue
            if "key based authentication is disabled for this resource." in message.casefold():
                return (
                    "The resource reported that key-based authentication is disabled. "
                    "Contact the resource administrator or use approved interactive authentication."
                )
            if "access denied due to virtual network/firewall rules." in message.casefold():
                return ACCESS_CODE_HINTS["NetworkAccessDenied"]
    if status == 401:
        return "The credential was not accepted. Check that the credential is valid for the configured resource; the exact cause is unknown."
    hint = "The exact access restriction is unknown. Check resource permissions and network access rules, and authentication policy."
    if authentication == "api_key":
        hint += " Disabled key-based authentication is one possibility, not a confirmed cause."
    return hint
