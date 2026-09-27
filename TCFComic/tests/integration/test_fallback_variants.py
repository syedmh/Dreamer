from __future__ import annotations

from dataclasses import replace

import logging
import sqlite3
from contextlib import closing

import pytest
import yaml

from conftest import write_config, write_image
from test_rate_pacing import ManualTime
from tcfcomic import cli
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode, JobStatus, PermanentProviderError, RetryableProviderError
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner

PROMPTS = {
    "tcf-school": "School portrait.",
    "pakistani-80s": "Photoreal eighties portrait.",
    "pakistani-80s-painted": "Painted eighties portrait.",
}
FALLBACKS = {"pakistani-80s": "pakistani-80s-painted"}


def fallback_config(root, *, prompts=PROMPTS, fallbacks=FALLBACKS, write_only=False, **kwargs):
    path, _ = write_config(root, **kwargs)
    data = yaml.safe_load(path.read_text())
    if prompts is not None:
        data["provider"]["prompts"] = dict(prompts)
    if fallbacks is not None:
        data["provider"]["fallbacks"] = fallbacks
    data["provider"]["requests_per_minute"] = 2
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    return path if write_only else (path, load_config(path))


def rewrite(path, *, fallbacks):
    data = yaml.safe_load(path.read_text())
    if fallbacks is None:
        data["provider"].pop("fallbacks", None)
    else:
        data["provider"]["fallbacks"] = fallbacks
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    return load_config(path)


class VariantProvider(FakeProvider):
    """Offline provider whose outcome depends on the prompt variant."""

    def __init__(self, failures=None):
        super().__init__()
        self.failures = dict(failures or {})

    def transform(self, request, output):
        failure = self.failures.get(request.variant)
        if failure is None:
            return super().transform(request, output)
        self.requests.append(request)
        if failure == "moderation":
            raise PermanentProviderError(ErrorCode.PROVIDER_PERMANENT, "moderation_blocked (offline).")
        if failure == "retryable":
            raise RetryableProviderError(ErrorCode.PROVIDER_RETRYABLE, "Temporarily unavailable (offline).")
        if failure == "auth":
            raise PermanentProviderError(ErrorCode.AUTHENTICATION_FAILED, "Offline auth failure.")
        if failure == "state":
            raise AppError(ErrorCode.STATE_FAILED, "Offline state failure.")
        raise AssertionError(failure)

    def variants(self):
        return [request.variant for request in self.requests]


class Records(logging.Handler):
    def __init__(self):
        super().__init__()
        self.records = []

    def emit(self, record):
        self.records.append(record)

    def events(self, name):
        return [r for r in self.records if getattr(r, "event", None) == name]


@pytest.fixture
def records(quiet_logger):
    handler = Records()
    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(handler)
    yield handler
    quiet_logger.removeHandler(handler)


def jobs(config):
    with closing(sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db")) as db:
        return db.execute(
            "SELECT variant, status, error_code FROM jobs ORDER BY created_at, variant"
        ).fetchall()


def by_variant(config):
    table = {}
    for variant, status, error_code in jobs(config):
        table.setdefault(variant, []).append((status, error_code))
    return table


def processor(config, logger, provider, timer):
    return Processor(config, logger, clock=timer.clock(), runner=InProcessAttemptRunner(provider))


def watch(config, logger, provider, timer, cycles=8):
    config = replace(config, watch=replace(config.watch, poll_interval_seconds=30))
    with processor(config, logger, provider, timer) as p:
        p.watch(max_cycles=cycles)


# --- configuration -----------------------------------------------------------------------


@pytest.mark.parametrize(
    ("fallbacks", "message"),
    [
        ({"pakistani-80s": "missing"}, "missing"),
        ({"missing": "pakistani-80s-painted"}, "missing"),
        ({"pakistani-80s": "pakistani-80s"}, "own fallback"),
        ({"pakistani-80s": "pakistani-80s-painted", "pakistani-80s-painted": "tcf-school"}, "chain"),
        ({"pakistani-80s": "pakistani-80s-painted", "tcf-school": "pakistani-80s-painted"}, "more than one"),
        ({"pakistani-80s": 3}, "provider.fallbacks"),
        (["pakistani-80s"], "provider.fallbacks"),
    ],
)
def test_invalid_fallback_config_is_rejected(tmp_path, fallbacks, message):
    path = fallback_config(tmp_path, fallbacks=fallbacks, write_only=True)
    with pytest.raises(AppError) as raised:
        load_config(path)
    assert raised.value.code == ErrorCode.CONFIG_INVALID
    assert "provider.fallbacks" in raised.value.safe_message
    assert message.lower() in raised.value.safe_message.lower()


def test_fallbacks_require_named_prompts(tmp_path):
    path = fallback_config(tmp_path, prompts=None, fallbacks={"a": "b"}, write_only=True)
    with pytest.raises(AppError) as raised:
        load_config(path)
    assert raised.value.code == ErrorCode.CONFIG_INVALID
    assert "provider.fallbacks" in raised.value.safe_message


def test_valid_and_empty_fallback_config(tmp_path):
    _, config = fallback_config(tmp_path)
    assert config.provider.fallbacks == FALLBACKS
    assert config.provider.fallback_variants == {"pakistani-80s-painted"}
    assert config.provider.primary_variants == ("tcf-school", "pakistani-80s")
    assert config.provider.fallback_for("pakistani-80s") == "pakistani-80s-painted"
    assert config.provider.primary_for("pakistani-80s-painted") == "pakistani-80s"
    empty = rewrite(tmp_path / "config.yaml", fallbacks={})
    assert empty.provider.primary_variants == tuple(PROMPTS)
    assert not empty.provider.fallback_variants


def test_validate_reports_fallbacks_offline(tmp_path, capsys):
    path, _ = fallback_config(tmp_path)
    assert cli.main(["validate", "--config", str(path)]) == 0
    output = capsys.readouterr()
    assert "pakistani-80s-painted" in output.out + output.err


# --- process ------------------------------------------------------------------------------


def test_process_runs_fallback_after_moderated_primary(tmp_path, quiet_logger, records):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "DSC_5476.JPG", "JPEG")
    provider = VariantProvider({"pakistani-80s": "moderation"})
    timer = ManualTime()
    with processor(config, quiet_logger, provider, timer) as p:
        results = p.process_variants(image)
    assert [r.variant for r in results] == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    assert [r.status for r in results] == [JobStatus.SUCCEEDED, JobStatus.FAILED, JobStatus.SUCCEEDED]
    assert provider.variants() == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    queued = records.events("fallback_queued")
    assert len(queued) == 1
    assert "Queued DSC_5476.JPG as fallback; primary pakistani-80s failed (PROVIDER_PERMANENT)" in queued[0].getMessage() \
        or "primary pakistani-80s failed (PROVIDER_PERMANENT)" in getattr(queued[0], "safe_message", "")

    # A second invocation neither retries the failed primary nor reruns the fallback.
    with processor(config, quiet_logger, provider, timer) as p:
        again = p.process_variants(image)
    assert [r.status for r in again] == [JobStatus.SUCCEEDED, JobStatus.FAILED, JobStatus.SUCCEEDED]
    assert len(provider.requests) == 3


def test_process_skips_fallback_when_primary_succeeds(tmp_path, quiet_logger, records):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider()
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        results = p.process_variants(image)
    assert [r.variant for r in results] == ["tcf-school", "pakistani-80s"]
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
    assert "pakistani-80s-painted" not in by_variant(config)
    assert len(records.events("fallback_not_needed")) == 1


def test_process_fallback_after_exhausted_retryable_primary(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path, max_attempts=2)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "retryable"})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        results = p.process_variants(image)
    table = by_variant(config)
    assert table["pakistani-80s"] == [("FAILED", "PROVIDER_RETRYABLE")]
    assert table["pakistani-80s-painted"] == [("SUCCEEDED", None)]
    assert provider.variants().count("pakistani-80s") == 2
    assert provider.variants().count("pakistani-80s-painted") == 1
    assert results[-1].status == JobStatus.SUCCEEDED


NON_TRIGGER_CODES = [
    "AUTHENTICATION_FAILED", "STATE_FAILED", "INVALID_IMAGE", "IMAGE_LIMIT_EXCEEDED",
    "OUTPUT_INVALID", "OUTPUT_LIMIT_EXCEEDED", "PUBLICATION_FAILED", "SHUTDOWN_INTERRUPTED",
    "PROVIDER_AMBIGUOUS", "SOURCE_CHANGED",
]


def _seed_failed_primary(tmp_path, quiet_logger, error_code):
    """Record a FAILED primary without fallbacks configured, then relabel its error code."""
    path, _ = fallback_config(tmp_path)
    config = rewrite(path, fallbacks=None)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "moderation", "pakistani-80s-painted": "moderation"})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        p.process_variants(image)
    with closing(sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db")) as db:
        db.execute("DELETE FROM jobs WHERE variant = 'pakistani-80s-painted'")
        db.execute("UPDATE jobs SET error_code = ? WHERE variant = 'pakistani-80s'", (error_code,))
        db.commit()
    return path, image


@pytest.mark.parametrize("error_code", NON_TRIGGER_CODES)
def test_non_provider_failures_never_trigger_fallback(tmp_path, quiet_logger, error_code):
    path, image = _seed_failed_primary(tmp_path, quiet_logger, error_code)
    config = rewrite(path, fallbacks=FALLBACKS)
    provider = VariantProvider()
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        p.process_variants(image)
    watch(config, quiet_logger, provider, ManualTime())
    assert provider.requests == []
    table = by_variant(config)
    assert table["pakistani-80s"] == [("FAILED", error_code)]
    assert "pakistani-80s-painted" not in table


def test_provider_boundary_exception_runs_fallback_at_most_once(tmp_path, quiet_logger):
    """A STATE_FAILED raised inside the provider boundary is still recorded, and never chains."""
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "state", "pakistani-80s-painted": "state"})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        p.process_variants(image)
    table = by_variant(config)
    assert table["pakistani-80s"][0][0] == "FAILED"
    # The fallback runs at most once per source version and never triggers a further fallback.
    assert provider.variants().count("pakistani-80s-painted") <= 1


def test_process_invalid_image_does_not_trigger_fallback(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    image = config.paths.source / "broken.png"
    image.write_bytes(b"not an image")
    provider = VariantProvider()
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        results = p.process_variants(image)
    assert {r.error_code for r in results} == {ErrorCode.INVALID_IMAGE}
    assert [r.variant for r in results] == ["tcf-school", "pakistani-80s"]
    assert provider.requests == []
    assert "pakistani-80s-painted" not in by_variant(config)


def test_process_authentication_failure_stops_without_fallback(tmp_path, monkeypatch, capsys):
    path, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "auth"})
    timer = ManualTime()

    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(provider), clock=timer.clock(), **kwargs)

    monkeypatch.setattr(cli, "Processor", Offline)
    assert cli.main(["process", "--config", str(path), str(image)]) == 3
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
    assert "pakistani-80s-painted" not in by_variant(config)


def test_cli_process_prints_fallback_output(tmp_path, monkeypatch, capsys):
    path, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "moderation"})
    timer = ManualTime()

    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(provider), clock=timer.clock(), **kwargs)

    monkeypatch.setattr(cli, "Processor", Offline)
    code = cli.main(["process", "--config", str(path), str(image)])
    lines = capsys.readouterr().out.splitlines()
    assert code != 0  # the primary failure remains visible in the exit code
    assert len(lines) == 2
    assert any("pakistani-80s-painted" in line for line in lines)
    assert len(provider.requests) == 3


# --- watch --------------------------------------------------------------------------------


def test_watch_detection_queues_only_non_fallback_variants(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider()
    watch(config, quiet_logger, provider, ManualTime())
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
    assert sorted(by_variant(config)) == ["pakistani-80s", "tcf-school"]


def test_watch_queues_and_processes_fallback_once_in_same_run(tmp_path, quiet_logger, records):
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "DSC_5476.JPG", "JPEG")
    provider = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, provider, ManualTime(), cycles=12)
    assert provider.variants() == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    table = by_variant(config)
    assert table["pakistani-80s"] == [("FAILED", "PROVIDER_PERMANENT")]
    assert table["pakistani-80s-painted"] == [("SUCCEEDED", None)]
    assert len(records.events("fallback_queued")) == 1
    outputs = sorted(p.name for p in config.paths.destination.glob("*.png"))
    assert len(outputs) == 2 and any(name.endswith("__pakistani-80s-painted.png") for name in outputs)


def test_watch_primary_success_never_dispatches_fallback(tmp_path, quiet_logger, records):
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider()
    timer = ManualTime()
    watch(config, quiet_logger, provider, timer)
    watch(config, quiet_logger, provider, timer)
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
    assert "pakistani-80s-painted" not in by_variant(config)
    # Logged once per observation of the succeeded primary, never per heartbeat/cycle.
    assert 1 <= len(records.events("fallback_not_needed")) <= 2


def test_watch_authentication_failure_stops_without_fallback(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider({"tcf-school": "auth"})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        with pytest.raises(AppError) as raised:
            p.watch(max_cycles=8)
    assert raised.value.code == ErrorCode.AUTHENTICATION_FAILED
    assert provider.variants() == ["tcf-school"]
    table = by_variant(config)
    assert "pakistani-80s-painted" not in table
    assert table["pakistani-80s"][0][0] == "READY"


def test_watch_restart_admits_fallback_for_already_failed_primary_once(tmp_path, quiet_logger, records):
    timer = ManualTime()
    # Fresh state for the true restart case: the primary failed while no fallback was configured
    # and the fallback prompt did not exist yet.
    rpath = fallback_config(tmp_path, prompts={"pakistani-cinematic": "Photoreal cinematic."},
                            fallbacks=None, write_only=True)
    rconfig = load_config(rpath)
    write_image(rconfig.paths.source / "DSC_5476.JPG", "JPEG")
    initial = VariantProvider({"pakistani-cinematic": "moderation"})
    watch(rconfig, quiet_logger, initial, timer)
    assert by_variant(rconfig) == {"pakistani-cinematic": [("FAILED", "PROVIDER_PERMANENT")]}

    data = yaml.safe_load(rpath.read_text())
    data["provider"]["prompts"]["pakistani-cinematic-painted"] = "Painted cinematic."
    data["provider"]["fallbacks"] = {"pakistani-cinematic": "pakistani-cinematic-painted"}
    rpath.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    rconfig = load_config(rpath)
    records.records.clear()
    restarted = VariantProvider()
    watch(rconfig, quiet_logger, restarted, timer, cycles=12)
    assert restarted.variants() == ["pakistani-cinematic-painted"]
    queued = records.events("fallback_queued")
    assert len(queued) == 1

    # Later restarts and rescans neither duplicate the fallback nor retry the failed primary.
    later = VariantProvider()
    watch(rconfig, quiet_logger, later, timer, cycles=12)
    assert later.requests == []
    assert by_variant(rconfig) == {
        "pakistani-cinematic": [("FAILED", "PROVIDER_PERMANENT")],
        "pakistani-cinematic-painted": [("SUCCEEDED", None)],
    }


@pytest.mark.parametrize("fallback_outcome", ["success", "moderation"])
def test_existing_fallback_job_is_never_duplicated(tmp_path, quiet_logger, fallback_outcome):
    path, _ = fallback_config(tmp_path)
    config = rewrite(path, fallbacks=None)
    image = write_image(config.paths.source / "a.png")
    timer = ManualTime()
    failures = {"pakistani-80s": "moderation"}
    if fallback_outcome == "moderation":
        failures["pakistani-80s-painted"] = "moderation"
    first = VariantProvider(failures)
    with processor(config, quiet_logger, first, timer) as p:
        p.process_variants(image)
    assert len(first.requests) == 3
    before = jobs(config)

    config = rewrite(path, fallbacks=FALLBACKS)
    again = VariantProvider()
    watch(config, quiet_logger, again, timer)
    with processor(config, quiet_logger, again, timer) as p:
        p.process_variants(image)
    assert again.requests == []
    assert jobs(config) == before


def test_no_fallbacks_config_queues_all_variants_on_detection(tmp_path, quiet_logger, records):
    path, _ = fallback_config(tmp_path)
    config = rewrite(path, fallbacks=None)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, provider, ManualTime(), cycles=12)
    assert provider.variants() == list(PROMPTS)
    assert not records.events("fallback_queued")
    assert not records.events("fallback_not_needed")


def test_fallback_not_admitted_for_changed_source(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    timer = ManualTime()
    with processor(config, quiet_logger, VariantProvider({"pakistani-80s": "moderation"}), timer) as p:
        p.process_variants(image)
    # A new source version resets the gate: its primary has not failed.
    write_image(image, color=(200, 10, 10))
    provider = VariantProvider()
    with processor(config, quiet_logger, provider, timer) as p:
        results = p.process_variants(image)
    assert [r.variant for r in results] == ["tcf-school", "pakistani-80s"]
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
