"""Fresh offline evidence, minted by the real SDK/adapter, never by a formatter."""
import hashlib
import io
import os
from unittest.mock import patch

import httpx
import openai

from tcfcomic.domain import AppError, SourceSnapshot, TransformRequest
from tcfcomic.providers.openai import OpenAIProvider


def capture_response(source, status=400, *, body=None, enabled=False,
                     request_id=None, headers=None):
    calls = []
    response_headers = dict(headers or {})
    if request_id is not None:
        response_headers["x-request-id"] = request_id

    def dispatch(request):
        request.read()
        calls.append(request)
        return httpx.Response(status, json={} if body is None else body,
                              headers=response_headers)

    real_client = openai.DefaultHttpxClient
    clients = []

    def client(**kwargs):
        result = real_client(transport=httpx.MockTransport(dispatch), **kwargs)
        clients.append(result)
        return result

    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("OPENAI_", "AZURE_OPENAI_"))}
    env["AZURE_OPENAI_API_KEY"] = "synthetic-key-not-a-credential"
    info = source.stat()
    request = TransformRequest(
        "a" * 32,
        SourceSnapshot(source, str(source), info.st_size, info.st_mtime_ns,
                       hashlib.sha256(source.read_bytes()).hexdigest(), source),
        "synthetic fixture prompt", "synthetic-deployment",
        azure_response_retries=enabled,
    )
    try:
        with patch.dict(os.environ, env, clear=True), patch.object(openai, "DefaultHttpxClient", client):
            provider = OpenAIProvider(
                2, azure_endpoint="https://offline.openai.azure.com",
                azure_response_retries=enabled,
            )
            try:
                provider.transform(request, io.BytesIO())
            except AppError as error:
                assert len(calls) == 1, "Fixture must execute exactly one intercepted SDK request"
                return error
            raise AssertionError("Expected an actual adapter HTTP error")
    finally:
        for client in clients:
            client.close()
