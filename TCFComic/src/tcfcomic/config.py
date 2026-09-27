from __future__ import annotations

import math
import os
import re
import uuid
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping

import yaml
from yaml.events import AliasEvent

from .azure_endpoint import canonical_azure_endpoint
from .domain import AppError, ErrorCode, valid_variant
from .path_safety import is_reparse_or_link, reject_reparse_components

DEFAULT_PROMPT = (
    "Transform this picture into a Studio Ghibli-inspired hand-painted "
    "Japanese animation aesthetic while retaining the recognizable subject "
    "and major composition."
)

MAX_CONFIG_BYTES = 65_536
MAX_YAML_ALIASES = 32
MAX_YAML_DEPTH = 32
MAX_YAML_NODES = 2_048


class _BoundedSafeLoader(yaml.SafeLoader):
    """Safe YAML loader with explicit complexity limits."""

    def __init__(self, stream: str) -> None:
        super().__init__(stream)
        self._alias_count = 0
        self._compose_depth = 0
        self._node_count = 0

    def compose_node(self, parent, index):
        if self.check_event(AliasEvent):
            self._alias_count += 1
            if self._alias_count > MAX_YAML_ALIASES:
                raise yaml.YAMLError("too many YAML aliases")
        self._compose_depth += 1
        self._node_count += 1
        try:
            if self._compose_depth > MAX_YAML_DEPTH:
                raise yaml.YAMLError("YAML nesting is too deep")
            if self._node_count > MAX_YAML_NODES:
                raise yaml.YAMLError("YAML contains too many nodes")
            return super().compose_node(parent, index)
        finally:
            self._compose_depth -= 1

    def construct_mapping(self, node, deep=False):
        self.flatten_mapping(node)
        keys = set()
        for key_node, _ in node.value:
            key = self.construct_object(key_node, deep=deep)
            if not isinstance(key, (str, int, float, bool, type(None))):
                raise yaml.YAMLError("invalid mapping key")
            if key in keys:
                raise yaml.YAMLError("duplicate mapping key")
            keys.add(key)
        return super().construct_mapping(node, deep=deep)


@dataclass(frozen=True)
class PathsConfig:
    source: Path
    destination: Path
    quarantine: Path


@dataclass(frozen=True)
class ProviderConfig:
    name: str
    model: str
    prompt: str
    request_timeout_seconds: float
    endpoint: str | None = None
    authentication: str = "api_key"
    tenant_id: str | None = None
    client_id: str | None = None
    redirect_uri: str | None = None
    requests_per_minute: int | None = None
    prompts: Mapping[str, str] | None = None
    fallbacks: Mapping[str, str] | None = None

    @property
    def variants(self) -> tuple[str, ...]:
        return tuple(self.prompts) if self.prompts is not None else ("",)

    @property
    def fallback_variants(self) -> frozenset[str]:
        return frozenset(self.fallbacks.values()) if self.fallbacks else frozenset()

    @property
    def primary_variants(self) -> tuple[str, ...]:
        fallbacks = self.fallback_variants
        return tuple(variant for variant in self.variants if variant not in fallbacks)

    def fallback_for(self, primary: str) -> str | None:
        return self.fallbacks.get(primary) if self.fallbacks else None

    def primary_for(self, fallback: str) -> str | None:
        if not self.fallbacks:
            return None
        for primary, candidate in self.fallbacks.items():
            if candidate == fallback:
                return primary
        return None

    def prompt_for(self, variant: str = "") -> str:
        if self.prompts is None and variant == "":
            return self.prompt
        if self.prompts is not None and variant in self.prompts:
            return self.prompts[variant]
        raise AppError(ErrorCode.STATE_FAILED, "The queued prompt variant is not configured.")


def validate_authentication(
    name: str, authentication: object, tenant_id: object,
    client_id: object, redirect_uri: object,
) -> tuple[str, str | None, str | None, str | None]:
    if type(authentication) is not str or authentication not in {"api_key", "interactive"}:
        raise _invalid("provider.authentication", "expected 'api_key' or 'interactive'")
    if authentication != "interactive":
        if any(value is not None for value in (tenant_id, client_id, redirect_uri)):
            raise _invalid("provider.authentication", "app settings require interactive authentication")
        return authentication, None, None, None
    if name != "azure_openai":
        raise _invalid("provider.authentication", "interactive authentication is Azure-only")
    values = []
    for field, value in (("tenant_id", tenant_id), ("client_id", client_id)):
        if value is not None:
            if type(value) is not str or not re.fullmatch(
                r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", value
            ):
                raise _invalid(f"provider.{field}", "expected a GUID")
            value = value.lower()
        values.append(value)
    if (client_id is None) != (redirect_uri is None):
        raise _invalid("provider.redirect_uri", "client_id and redirect_uri must be provided together")
    if redirect_uri is not None:
        match = (
            re.fullmatch(r"http://localhost:([0-9]{1,5})/?", redirect_uri)
            if type(redirect_uri) is str else None
        )
        if match is None or not 1024 <= int(match[1]) <= 65535:
            raise _invalid(
                "provider.redirect_uri",
                "expected http://localhost:PORT/ with port 1024-65535 and no other URL components",
            )
        redirect_uri = f"http://localhost:{int(match[1])}/"
    return authentication, values[0], values[1], redirect_uri


@dataclass(frozen=True)
class WatchConfig:
    poll_interval_seconds: float
    stable_seconds: float
    recursive: bool
    heartbeat_seconds: float = 15.0


@dataclass(frozen=True)
class RetryConfig:
    max_attempts: int
    initial_delay_seconds: float
    max_delay_seconds: float
    azure_response_retries: bool = False


@dataclass(frozen=True)
class ShutdownConfig:
    timeout_seconds: float


@dataclass(frozen=True)
class LimitsConfig:
    max_input_bytes: int
    max_output_bytes: int
    max_width: int
    max_height: int
    max_pixels: int


@dataclass(frozen=True)
class LoggingConfig:
    level: str
    rotate_bytes: int
    backup_count: int
    console_format: str = "json"


@dataclass(frozen=True)
class AppConfig:
    version: int
    paths: PathsConfig
    provider: ProviderConfig
    watch: WatchConfig
    retry: RetryConfig
    shutdown: ShutdownConfig
    limits: LimitsConfig
    logging: LoggingConfig


_TOP_KEYS = {
    "version",
    "paths",
    "provider",
    "watch",
    "retry",
    "shutdown",
    "limits",
    "logging",
}


def _invalid(field: str, message: str) -> AppError:
    return AppError(ErrorCode.CONFIG_INVALID, f"Invalid configuration field '{field}': {message}")


def _mapping(value: object, field: str) -> dict[str, Any]:
    if type(value) is not dict:
        raise _invalid(field, "expected a mapping")
    if any(type(key) is not str for key in value):
        raise _invalid(field, "mapping keys must be strings")
    return value


def _keys(data: Mapping[str, Any], allowed: set[str], field: str) -> None:
    if any(type(key) is not str for key in data):
        raise _invalid(field, "mapping keys must be strings")
    unknown = set(data) - allowed
    if unknown:
        raise _invalid(field, f"unknown key(s): {', '.join(sorted(unknown))}")


def _string(data: Mapping[str, Any], key: str, field: str, default: str | None = None) -> str:
    value = data.get(key, default)
    if type(value) is not str or not value.strip():
        raise _invalid(f"{field}.{key}", "expected a non-empty string")
    return value.strip()


def _integer(
    data: Mapping[str, Any],
    key: str,
    field: str,
    default: int,
    *,
    minimum: int = 0,
    maximum: int | None = None,
) -> int:
    value = data.get(key, default)
    if type(value) is not int or value < minimum or (
        maximum is not None and value > maximum
    ):
        bound = f" and <= {maximum}" if maximum is not None else ""
        raise _invalid(
            f"{field}.{key}", f"expected an integer >= {minimum}{bound}"
        )
    return value


def _number(
    data: Mapping[str, Any],
    key: str,
    field: str,
    default: float,
    *,
    minimum: float = 0.0,
    allow_zero: bool = False,
    maximum: float | None = None,
) -> float:
    value = data.get(key, default)
    if type(value) not in (int, float):
        raise _invalid(f"{field}.{key}", "expected a number")
    result = float(value)
    if (
        not math.isfinite(result)
        or result < minimum
        or (not allow_zero and result == 0)
        or (maximum is not None and result > maximum)
    ):
        comparison = ">=" if allow_zero else ">"
        bound = f" and <= {maximum}" if maximum is not None else ""
        raise _invalid(
            f"{field}.{key}",
            f"expected a finite number {comparison} {minimum}{bound}",
        )
    return result


def _boolean(data: Mapping[str, Any], key: str, field: str, default: bool) -> bool:
    value = data.get(key, default)
    if type(value) is not bool:
        raise _invalid(f"{field}.{key}", "expected a boolean")
    return value


def _is_unc_path(path: Path) -> bool:
    return str(path).startswith("\\\\")


def _is_relative_to(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def _validate_root(path: Path, field: str, *, writable: bool) -> Path:
    if not path.is_absolute():
        raise _invalid(field, "path must be absolute")
    if _is_unc_path(path):
        raise _invalid(field, "UNC paths are not supported")
    try:
        reject_reparse_components(path)
        resolved = path.resolve(strict=True)
    except (OSError, AppError):
        raise _invalid(field, "directory does not exist or cannot be resolved") from None
    if not resolved.is_dir():
        raise _invalid(field, "expected an existing directory")
    if is_reparse_or_link(resolved):
        raise _invalid(field, "reparse points and symbolic links are not supported")
    if not os.access(resolved, os.R_OK):
        raise _invalid(field, "directory is not readable")
    if writable:
        probe = resolved / f".tcfcomic-access-{uuid.uuid4().hex}.tmp"
        probe_failed = False
        try:
            with probe.open("x", encoding="utf-8") as stream:
                stream.write("")
        except OSError:
            probe_failed = True
        finally:
            try:
                probe.unlink()
            except FileNotFoundError:
                pass
            except OSError:
                probe_failed = True
        if probe_failed:
            raise _invalid(field, "directory is not writable")
    return resolved


def _validate_quarantine(path: Path, destination: Path, *, prepare: bool = True) -> Path:
    field = "paths.quarantine"
    if not path.is_absolute():
        raise _invalid(field, "path must be absolute")
    if _is_unc_path(path):
        raise _invalid(field, "UNC paths are not supported")
    try:
        reject_reparse_components(path)
        candidate = path.resolve(strict=False)
    except (OSError, AppError):
        raise _invalid(field, "directory cannot be resolved safely") from None
    destination_key = Path(os.path.normcase(str(destination)))
    candidate_key = Path(os.path.normcase(str(candidate)))
    if not _is_relative_to(candidate_key, destination_key):
        raise _invalid(field, "must be beneath paths.destination")
    if not candidate.exists():
        if not prepare:
            return candidate
        try:
            candidate.mkdir(parents=True, exist_ok=True)
        except OSError:
            raise _invalid(field, "directory does not exist and cannot be created") from None
    resolved = _validate_root(candidate, field, writable=prepare)
    resolved_key = Path(os.path.normcase(str(resolved)))
    if not _is_relative_to(resolved_key, destination_key):
        raise _invalid(field, "must be beneath paths.destination")
    return resolved


def load_config(path: Path, *, prepare_paths: bool = True) -> AppConfig:
    try:
        with path.open("rb") as stream:
            encoded = stream.read(MAX_CONFIG_BYTES + 1)
        if len(encoded) > MAX_CONFIG_BYTES:
            raise AppError(
                ErrorCode.CONFIG_INVALID,
                f"Configuration exceeds the {MAX_CONFIG_BYTES}-byte limit: {path}",
            )
        text = encoded.decode("utf-8")
        raw = yaml.load(text, Loader=_BoundedSafeLoader)
    except AppError:
        raise
    except (OSError, UnicodeDecodeError, yaml.YAMLError) as exc:
        kind = (
            "cannot be read"
            if isinstance(exc, OSError)
            else "contains malformed YAML or unsafe complexity"
        )
        raise AppError(ErrorCode.CONFIG_INVALID, f"Configuration {kind}: {path}") from None

    data = _mapping(raw, "root")
    _keys(data, _TOP_KEYS, "root")

    version = data.get("version")
    if type(version) is not int or version != 1:
        raise _invalid("version", "expected integer 1")

    paths = _mapping(data.get("paths"), "paths")
    _keys(paths, {"source", "destination", "quarantine"}, "paths")
    source_text = _string(paths, "source", "paths")
    destination_text = _string(paths, "destination", "paths")
    source = _validate_root(Path(source_text), "paths.source", writable=False)
    destination = _validate_root(Path(destination_text), "paths.destination", writable=prepare_paths)
    source_key = Path(os.path.normcase(str(source)))
    destination_key = Path(os.path.normcase(str(destination)))
    if source_key == destination_key:
        raise _invalid("paths.destination", "source and destination must be different")
    if _is_relative_to(destination_key, source_key) or _is_relative_to(source_key, destination_key):
        raise _invalid("paths.destination", "source and destination trees must not overlap")

    quarantine_text = paths.get("quarantine")
    if quarantine_text is None:
        quarantine_path = destination / ".tcfcomic" / "quarantine"
    else:
        if type(quarantine_text) is not str or not quarantine_text.strip():
            raise _invalid("paths.quarantine", "expected a non-empty string")
        quarantine_path = Path(quarantine_text)
    quarantine = _validate_quarantine(quarantine_path, destination, prepare=prepare_paths)

    provider = _mapping(data.get("provider"), "provider")
    _keys(provider, {
        "name", "model", "prompt", "prompts", "request_timeout_seconds", "endpoint",
        "authentication", "tenant_id", "client_id", "redirect_uri",
        "requests_per_minute", "fallbacks",
    }, "provider")
    prompts = None
    if "prompts" in provider:
        if "prompt" in provider:
            raise _invalid("provider", "prompt and prompts are mutually exclusive")
        prompts = _mapping(provider["prompts"], "provider.prompts")
        if not prompts or any(not valid_variant(key) for key in prompts):
            raise _invalid("provider.prompts", "expected safe lowercase ASCII names of at most 32 characters")
        if any(type(value) is not str or not value.strip() for value in prompts.values()):
            raise _invalid("provider.prompts", "each prompt must be a non-empty string")
    fallbacks = _parse_fallbacks(provider, prompts)
    provider_name =  _string(provider, "name", "provider").lower()
    if provider_name not in {"openai", "fake", "azure_openai"}:
        raise _invalid("provider.name", "expected 'openai', 'fake', or 'azure_openai'")
    endpoint = None
    if provider_name == "azure_openai":
        try:
            endpoint = canonical_azure_endpoint(provider.get("endpoint"))
        except ValueError as exc:
            raise _invalid("provider.endpoint", str(exc)) from None
    elif "endpoint" in provider:
        raise _invalid("provider.endpoint", "only supported for 'azure_openai'")
    for field in ("tenant_id", "client_id", "redirect_uri"):
        if field in provider and provider[field] is None:
            raise _invalid(f"provider.{field}", "omit optional settings rather than using null")
    authentication, tenant_id, client_id, redirect_uri = validate_authentication(
        provider_name, provider.get("authentication", "api_key"),
        provider.get("tenant_id"), provider.get("client_id"), provider.get("redirect_uri"),
    )

    watch = _mapping(data.get("watch", {}), "watch")
    _keys(watch, {"poll_interval_seconds", "stable_seconds", "recursive", "heartbeat_seconds"}, "watch")
    recursive = _boolean(watch, "recursive", "watch", False)
    if recursive:
        raise _invalid("watch.recursive", "recursive watching is not supported in the MVP")

    retry = _mapping(data.get("retry", {}), "retry")
    _keys(retry, {"max_attempts", "initial_delay_seconds", "max_delay_seconds", "azure_response_retries"}, "retry")
    azure_response_retries = _boolean(retry, "azure_response_retries", "retry", False)
    if azure_response_retries and provider_name != "azure_openai":
        raise _invalid("retry.azure_response_retries", "requires the Azure OpenAI provider")
    retry_initial = _number(
        retry,
        "initial_delay_seconds",
        "retry",
        1.0,
        allow_zero=True,
        maximum=86_400,
    )
    retry_max = _number(
        retry,
        "max_delay_seconds",
        "retry",
        8.0,
        allow_zero=True,
        maximum=86_400,
    )
    if retry_max < retry_initial:
        raise _invalid("retry.max_delay_seconds", "must be >= retry.initial_delay_seconds")

    shutdown = _mapping(data.get("shutdown", {}), "shutdown")
    _keys(shutdown, {"timeout_seconds"}, "shutdown")
    limits = _mapping(data.get("limits", {}), "limits")
    _keys(
        limits,
        {"max_input_bytes", "max_output_bytes", "max_width", "max_height", "max_pixels"},
        "limits",
    )
    logging_data = _mapping(data.get("logging", {}), "logging")
    _keys(logging_data, {"level", "rotate_bytes", "backup_count", "console_format"}, "logging")
    level = _string(logging_data, "level", "logging", "INFO").upper()
    if level not in {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}:
        raise _invalid("logging.level", "unsupported logging level")
    console_format = logging_data.get("console_format", "json")
    if type(console_format) is not str or console_format not in {"json", "text"}:
        raise _invalid("logging.console_format", "expected 'json' or 'text'")

    return AppConfig(
        version=version,
        paths=PathsConfig(source=source, destination=destination, quarantine=quarantine),
        provider=ProviderConfig(
            name=provider_name,
            model=_string(
                provider, "model", "provider",
                None if provider_name == "azure_openai" else "gpt-image-2",
            ),
            prompt=_string(provider, "prompt", "provider", DEFAULT_PROMPT),
            prompts=prompts,
            fallbacks=fallbacks,
            request_timeout_seconds=_number(
                provider,
                "request_timeout_seconds",
                "provider",
                120.0,
                maximum=86_400,
            ),
            endpoint=endpoint,
            authentication=authentication,
            tenant_id=tenant_id,
            client_id=client_id,
            redirect_uri=redirect_uri,
            requests_per_minute=(
                _integer(provider, "requests_per_minute", "provider", 2,
                         minimum=1, maximum=60_000)
                if "requests_per_minute" in provider else None
            ),
        ),
        watch=WatchConfig(
            poll_interval_seconds=_number(
                watch,
                "poll_interval_seconds",
                "watch",
                1.0,
                maximum=3_600,
            ),
            stable_seconds=_number(
                watch,
                "stable_seconds",
                "watch",
                3.0,
                allow_zero=True,
                maximum=86_400,
            ),
            recursive=recursive,
            heartbeat_seconds=_number(
                watch, "heartbeat_seconds", "watch", 15.0,
                minimum=1, maximum=3600,
            ),
        ),
        retry=RetryConfig(
            max_attempts=_integer(
                retry, "max_attempts", "retry", 3, minimum=1,
                maximum=4 if azure_response_retries else 100,
            ),
            initial_delay_seconds=retry_initial,
            max_delay_seconds=retry_max,
            azure_response_retries=azure_response_retries,
        ),
        shutdown=ShutdownConfig(
            timeout_seconds=_number(
                shutdown, "timeout_seconds", "shutdown", 15.0, maximum=3_600
            )
        ),
        limits=LimitsConfig(
            max_input_bytes=_integer(
                limits,
                "max_input_bytes",
                "limits",
                52_428_800,
                minimum=1,
                maximum=1_073_741_824,
            ),
            max_output_bytes=_integer(
                limits,
                "max_output_bytes",
                "limits",
                52_428_800,
                minimum=1,
                maximum=1_073_741_824,
            ),
            max_width=_integer(
                limits, "max_width", "limits", 7680, minimum=1, maximum=100_000
            ),
            max_height=_integer(
                limits, "max_height", "limits", 7680, minimum=1, maximum=100_000
            ),
            max_pixels=_integer(
                limits,
                "max_pixels",
                "limits",
                40_000_000,
                minimum=1,
                maximum=1_000_000_000,
            ),
        ),
        logging=LoggingConfig(
            level=level,
            console_format=console_format,
            rotate_bytes=_integer(
                logging_data,
                "rotate_bytes",
                "logging",
                10_485_760,
                minimum=1,
                maximum=1_073_741_824,
            ),
            backup_count=_integer(
                logging_data,
                "backup_count",
                "logging",
                5,
                minimum=1,
                maximum=100,
            ),
        ),
    )


def _parse_fallbacks(
    provider: Mapping[str, Any], prompts: Mapping[str, str] | None,
) -> Mapping[str, str] | None:
    field = "provider.fallbacks"
    if "fallbacks" not in provider or provider["fallbacks"] is None:
        return None
    raw = _mapping(provider["fallbacks"], field)
    if not raw:
        return None
    if prompts is None:
        raise _invalid(field, "requires named provider.prompts")
    for primary, fallback in raw.items():
        if type(fallback) is not str or not fallback:
            raise _invalid(field, f"fallback for '{primary}' must be a prompt name")
        if primary not in prompts:
            raise _invalid(field, f"primary '{primary}' is not a configured prompt")
        if fallback not in prompts:
            raise _invalid(field, f"fallback '{fallback}' is not a configured prompt")
        if primary == fallback:
            raise _invalid(field, f"'{primary}' cannot be its own fallback")
    targets = list(raw.values())
    duplicates = sorted({name for name in targets if targets.count(name) > 1})
    if duplicates:
        raise _invalid(field, f"fallback '{duplicates[0]}' is assigned to more than one primary")
    chained = sorted(set(targets) & set(raw))
    if chained:
        raise _invalid(field, f"'{chained[0]}' cannot be both a fallback and a primary (no chains)")
    return dict(raw)


def sanitized_config_summary(config: AppConfig) -> Mapping[str, object]:
    return {
        "source": str(config.paths.source),
        "destination": str(config.paths.destination),
        "quarantine": str(config.paths.quarantine),
        "provider": config.provider.name,
        "model": config.provider.model,
        "max_attempts": config.retry.max_attempts,
        "azure_response_retries": config.retry.azure_response_retries,
        **(
            {"requests_per_minute": config.provider.requests_per_minute}
            if config.provider.requests_per_minute is not None else {}
        ),
        **(
            {"authentication": "interactive"}
            if config.provider.authentication == "interactive" else {}
        ),
        **({"endpoint": config.provider.endpoint} if config.provider.endpoint else {}),
        **(
            {"fallbacks": dict(config.provider.fallbacks)}
            if config.provider.fallbacks else {}
        ),
        "watch_mode": "non-recursive",
        "poll_interval_seconds": config.watch.poll_interval_seconds,
        "stable_seconds": config.watch.stable_seconds,
    }


def require_failed_variant_retry_policy(config: AppConfig) -> None:
    if (
        config.provider.name != "azure_openai"
        or not config.retry.azure_response_retries
        or not 1 <= config.retry.max_attempts <= 4
    ):
        raise _invalid(
            "--retry-failed-variants",
            "requires Azure retry.azure_response_retries: true and at most four lifetime attempts",
        )


def require_provider_credentials(config: AppConfig) -> None:
    if config.provider.authentication == "interactive":
        return
    if config.provider.name == "fake":
        return
    if config.provider.name == "openai":
        variable, label = "OPENAI_API_KEY", "OpenAI"
    elif config.provider.name == "azure_openai":
        variable, label = "AZURE_OPENAI_API_KEY", "Azure OpenAI"
    else:
        raise _invalid("provider.name", "unsupported provider")
    if not os.environ.get(variable, "").strip():
        raise AppError(
            ErrorCode.CREDENTIAL_MISSING,
            f"The {variable} environment variable is required for the {label} provider.",
        )
