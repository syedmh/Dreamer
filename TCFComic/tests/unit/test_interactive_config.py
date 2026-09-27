from dataclasses import replace
import hashlib
import json
import threading

import pytest
import yaml

from conftest import write_config, write_image
from tcfcomic.authentication import AccessToken, DEFAULT_CLIENT, DEFAULT_TENANT
from tcfcomic.cli import main
from tcfcomic.config import load_config, require_provider_credentials
from tcfcomic.domain import AppError, ErrorCode, JobStatus
from tcfcomic.processor import Processor
from tcfcomic.providers.worker import _provider_from_config
from tcfcomic.redaction import redact_secret, sanitize_text, _secrets

ENDPOINT = "https://sweepertestai.openai.azure.com"
GUID = "12345678-1234-1234-1234-123456789abc"


def interactive_config(root, **settings):
    path, _ = write_config(root, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text())
    data["provider"].update(authentication="interactive", **settings)
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    return path, load_config(path)


@pytest.mark.parametrize("field,value", [
    ("authentication", None), ("authentication", True), ("authentication", "default"),
    ("authentication", "Interactive"), ("authentication", ["interactive"]),
    ("tenant_id", None), ("tenant_id", 1), ("tenant_id", "common"),
    ("tenant_id", "../tenant"), ("tenant_id", GUID + " "), ("tenant_id", GUID.replace("-", "")),
    ("client_id", "secret"), ("client_id", True), ("client_id", GUID),
    ("redirect_uri", "http://localhost:8400/"),
    ("authority", "https://evil.example"), ("scope", "other"),
    ("token", "opaque-secret"), ("client_secret", "secret"),
    ("cache_persistence_options", {}),
])
def test_auth_schema_rejects_invalid_settings(tmp_path, field, value):
    path, _ = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    data = yaml.safe_load(path.read_text())
    data["provider"].update(authentication="interactive")
    data["provider"][field] = value
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(AppError) as caught:
        load_config(path)
    assert caught.value.code == ErrorCode.CONFIG_INVALID


@pytest.mark.parametrize("uri", [
    "https://localhost:8400/", "http://localhost/", "http://localhost:80/",
    "http://localhost:0/", "http://localhost:65536/", "http://localhost:-1/",
    "http://127.0.0.1:8400/", "http://[::1]:8400/", "http://evil.example:8400/",
    "http://localhost.evil.example:8400/", "http://user@localhost:8400/",
    "http://localhost:8400/path", "http://localhost:8400//",
    "http://localhost:8400/?", "http://localhost:8400/#", "http://localhost:8400/%2e",
    "http://localhost:8400/\\", "http://localhost:8400/\n",
    " http://localhost:8400/", "http://LOCALHOST:8400/", "http://loc\u0430lhost:8400/",
    "http://localhost:8400/?token=secret", True, [], 8400,
])
def test_redirect_attack_matrix(tmp_path, uri):
    with pytest.raises(AppError):
        interactive_config(tmp_path, client_id=GUID, redirect_uri=uri)


@pytest.mark.parametrize("provider", ["fake", "openai"])
def test_interactive_is_azure_only(tmp_path, provider):
    path, _ = write_config(tmp_path, provider=provider)
    data = yaml.safe_load(path.read_text())
    data["provider"]["authentication"] = "interactive"
    path.write_text(yaml.safe_dump(data), encoding="utf-8")
    with pytest.raises(AppError):
        load_config(path)


def test_auth_config_canonicalization_and_no_key_fallback(tmp_path, monkeypatch):
    _, config = interactive_config(
        tmp_path, client_id=GUID.upper(), tenant_id=GUID.upper(),
        redirect_uri="http://localhost:08400",
    )
    assert config.provider.tenant_id == GUID
    assert config.provider.client_id == GUID
    assert config.provider.redirect_uri == "http://localhost:8400/"
    monkeypatch.setenv("AZURE_CLIENT_ID", "ambient-is-not-used")
    monkeypatch.setenv("AZURE_TENANT_ID", "ambient-is-not-used")
    require_provider_credentials(config)


def test_missing_interactive_token_never_falls_back_to_keys(tmp_path, monkeypatch):
    _, config = interactive_config(tmp_path)
    monkeypatch.setenv("OPENAI_API_KEY", "public-key-not-a-fallback")
    monkeypatch.setenv("AZURE_OPENAI_API_KEY", "azure-key-not-a-fallback")
    with pytest.raises(AppError) as caught:
        _provider_from_config(config.provider)
    assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED


def test_help_validate_and_direct_processor_do_not_authenticate(tmp_path, monkeypatch, quiet_logger):
    import azure.identity
    import tcfcomic.cli as cli

    path, config = interactive_config(tmp_path)
    def forbidden(*args, **kwargs):
        pytest.fail("Validation/direct construction must not start auth")
    monkeypatch.setattr(azure.identity, "InteractiveBrowserCredential", forbidden)
    monkeypatch.setattr(cli, "AuthenticationBroker", forbidden)
    assert main(["validate", "--config", str(path)]) == 0
    with pytest.raises(SystemExit) as caught:
        main(["--help"])
    assert caught.value.code == 0
    with pytest.raises(AppError) as caught:
        Processor(config, quiet_logger)
    assert caught.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert not (config.paths.destination / ".tcfcomic" / "state.db").exists()


@pytest.mark.parametrize("token", [
    "opaque-short-sensitive",
    "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJzeW50aGV0aWMifQ.signature-with_underscores",
])
def test_exact_redaction_scoped_bounded_and_repr(token):
    before = dict(_secrets)
    value = AccessToken(token, 1)
    assert token not in repr(value)
    for _ in range(100):
        with redact_secret(token):
            with redact_secret(token):
                assert token not in sanitize_text(f"prefix {token} suffix")
                assert len(_secrets) <= len(before) + 1
    assert _secrets == before


@pytest.mark.parametrize("name", ["fake", "openai", "azure_openai"])
def test_legacy_identity_exact_bytes_unchanged(tmp_path, quiet_logger, name):
    _, config = write_config(tmp_path, provider=name, endpoint=ENDPOINT if name == "azure_openai" else None)
    prompt = config.provider.prompt
    if name == "azure_openai":
        prompt = json.dumps(
            ["azure_openai.identity.v1", ENDPOINT, "preview", prompt],
            ensure_ascii=False, separators=(",", ":"),
        )
    with Processor(config, quiet_logger) as processor:
        assert processor._request_identity().prompt_hash == hashlib.sha256(prompt.encode("utf-8")).hexdigest()


class SignedIn:
    started = True
    def acquire(self, minimum_lifetime, deadline):
        import time
        return AccessToken("offline-only-access-token", int(time.time()) + 3600)


@pytest.mark.parametrize("setting,value", [
    ("tenant_id", GUID), ("client_id", GUID),
    ("redirect_uri", "http://localhost:8400/"), ("authentication", "api_key"),
])
def test_interactive_identity_changes(tmp_path, quiet_logger, setting, value):
    _, config = interactive_config(tmp_path)
    with Processor(config, quiet_logger, auth_session=SignedIn()) as processor:
        original = processor._request_identity()
        expected = json.dumps([
            "azure_openai.identity.v2", ENDPOINT, "preview", config.provider.prompt,
            "interactive", DEFAULT_TENANT, DEFAULT_CLIENT, "dynamic-loopback",
        ], ensure_ascii=False, separators=(",", ":"))
        assert original.prompt_hash == hashlib.sha256(expected.encode()).hexdigest()
    changed = replace(config, provider=replace(config.provider, **{setting: value}))
    with Processor(changed, quiet_logger, auth_session=SignedIn()) as processor:
        assert processor._request_identity() != original


@pytest.mark.parametrize("retry", [False, True])
def test_auth_mode_drift_rejects_queued_work_without_image_requests(tmp_path, quiet_logger, retry):
    from datetime import UTC, datetime

    _, config = write_config(tmp_path, provider="azure_openai", endpoint=ENDPOINT)
    event = threading.Event()
    with Processor(config, quiet_logger) as processor:
        admitted = processor._admit(
            write_image(config.paths.source / "pending.png"), candidate=None, shutdown_event=event,
        )
        if retry:
            processor.state.claim_ready(admitted.job_id, datetime.now(UTC))
            attempt = processor.state.begin_attempt(admitted.job_id)
            processor.state.mark_retryable(
                admitted.job_id, attempt.attempt_no, 0, "2000-01-01T00:00:00+00:00",
                ErrorCode.PROVIDER_RETRYABLE, "offline",
            )
    config = replace(config, provider=replace(config.provider, authentication="interactive"))
    class NoImageRequests:
        def run_authenticated(self, *args):
            pytest.fail("Identity drift must not dispatch")
    with Processor(config, quiet_logger, auth_session=SignedIn(), runner=NoImageRequests()) as processor:
        assert processor._dispatch_one(event)
        assert processor.state.get_job(admitted.job_id).status == JobStatus.FAILED
        assert processor.state.attempt_count(admitted.job_id) == int(retry)
