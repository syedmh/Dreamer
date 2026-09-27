"""Independent gap coverage for provider fallbacks (negative / regression edges)."""
from __future__ import annotations

import os
import sqlite3
from contextlib import closing
from dataclasses import replace

import pytest
import yaml

from conftest import write_image
from fresh_response import capture_response
from test_fallback_variants import (
    FALLBACKS, PROMPTS, Records, VariantProvider, by_variant, fallback_config, jobs, processor,
    rewrite, watch,
)
from test_rate_pacing import ManualTime
from test_retry_cda7_pacing import Clock, Scripted, config_for, processor_for
from tcfcomic import cli
from tcfcomic.domain import ErrorCode, JobStatus, PermanentProviderError
from tcfcomic.processor import Processor
from tcfcomic.providers.worker import InProcessAttemptRunner


@pytest.fixture
def records(quiet_logger):
    import logging
    handler = Records()
    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(handler)
    yield handler
    quiet_logger.removeHandler(handler)


class HookProvider(VariantProvider):
    """VariantProvider that runs a side effect before failing a given variant."""

    def __init__(self, failures, hooks, timer=None):
        super().__init__(failures)
        self.hooks = dict(hooks)
        self.timer = timer
        self.starts = []

    def transform(self, request, output):
        self.starts.append((request.variant, self.timer.elapsed if self.timer else None))
        hook = self.hooks.pop(request.variant, None)
        if hook is not None:
            hook()
        return super().transform(request, output)


def fallback_rows(config):
    with closing(sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db")) as db:
        return db.execute(
            "SELECT sha256, size, mtime_ns, status, error_code FROM jobs WHERE variant = ?",
            ("pakistani-80s-painted",),
        ).fetchall()


def primary_rows(config):
    with closing(sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db")) as db:
        return db.execute(
            "SELECT sha256, size, mtime_ns, status, error_code FROM jobs WHERE variant = ?",
            ("pakistani-80s",),
        ).fetchall()


def _modify(image):
    def hook():
        write_image(image, color=(200, 10, 10), image_format="PNG")
        info = image.stat()
        os.utime(image, ns=(info.st_atime_ns, info.st_mtime_ns + 5_000_000_000))
    return hook


# --- source changed / removed between primary failure and fallback admission ----------------


def test_process_source_modified_before_fallback_admission_never_orphans_fallback(tmp_path, quiet_logger):
    """A fallback job must only exist for a source version whose primary FAILED."""
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = HookProvider({"pakistani-80s": "moderation"}, {"pakistani-80s": _modify(image)})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        p.process_variants(image)
    failed_primary_versions = {
        (sha, size, mtime) for sha, size, mtime, status, code in primary_rows(config)
        if status == "FAILED" and code in {"PROVIDER_PERMANENT", "PROVIDER_RETRYABLE"}
    }
    for sha, size, mtime, status, code in fallback_rows(config):
        assert (sha, size, mtime) in failed_primary_versions, (
            "fallback job created for a source version whose primary never failed",
            fallback_rows(config), primary_rows(config),
        )
    assert "pakistani-80s-painted" not in provider.variants()


def test_watch_source_modified_before_fallback_admission_never_orphans_fallback(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = HookProvider({"pakistani-80s": "moderation"}, {"pakistani-80s": _modify(image)})
    watch(config, quiet_logger, provider, ManualTime(), cycles=14)
    failed_primary_versions = {
        (sha, size, mtime) for sha, size, mtime, status, code in primary_rows(config)
        if status == "FAILED" and code in {"PROVIDER_PERMANENT", "PROVIDER_RETRYABLE"}
    }
    for sha, size, mtime, status, code in fallback_rows(config):
        assert (sha, size, mtime) in failed_primary_versions, (fallback_rows(config), primary_rows(config))


def test_process_source_removed_before_fallback_admission_sends_no_fallback(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = HookProvider({"pakistani-80s": "moderation"}, {"pakistani-80s": image.unlink})
    with processor(config, quiet_logger, provider, ManualTime()) as p:
        results = p.process_variants(image)
    assert "pakistani-80s-painted" not in provider.variants()
    assert results[1].status == JobStatus.FAILED
    assert all(status != "SUCCEEDED" for *_, status, _code in fallback_rows(config))


def test_watch_source_removed_before_fallback_admission_sends_no_fallback(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = HookProvider({"pakistani-80s": "moderation"}, {"pakistani-80s": image.unlink})
    watch(config, quiet_logger, provider, ManualTime(), cycles=14)
    assert provider.variants() == ["tcf-school", "pakistani-80s"]
    assert by_variant(config)["pakistani-80s"] == [("FAILED", "PROVIDER_PERMANENT")]


# --- retryable exhaustion in watch / restart -----------------------------------------------


def test_watch_fallback_after_exhausted_retryable_primary(tmp_path, quiet_logger, records):
    _, config = fallback_config(tmp_path, max_attempts=2)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "retryable"})
    watch(config, quiet_logger, provider, ManualTime(), cycles=20)
    table = by_variant(config)
    assert table["pakistani-80s"] == [("FAILED", "PROVIDER_RETRYABLE")]
    assert table["pakistani-80s-painted"] == [("SUCCEEDED", None)]
    assert provider.variants().count("pakistani-80s") == 2
    assert provider.variants().count("pakistani-80s-painted") == 1
    # The fallback is dispatched only after the last primary attempt.
    last_primary = max(i for i, v in enumerate(provider.variants()) if v == "pakistani-80s")
    assert provider.variants().index("pakistani-80s-painted") > last_primary
    assert len(records.events("fallback_queued")) == 1


def test_watch_does_not_queue_fallback_while_primary_awaits_retry(tmp_path, quiet_logger):
    """READY_RETRY is not terminal: no fallback until the primary retry budget is spent."""
    _, config = fallback_config(tmp_path, max_attempts=3, initial_delay_seconds=600, max_delay_seconds=600)
    write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "retryable"})
    # Few cycles: the 600 s retry delay keeps the primary in READY_RETRY.
    watch(config, quiet_logger, provider, ManualTime(), cycles=4)
    table = by_variant(config)
    assert table["pakistani-80s"][0][0] in {"READY_RETRY", "READY"}
    assert "pakistani-80s-painted" not in table
    assert "pakistani-80s-painted" not in provider.variants()


def test_watch_restart_admits_fallback_for_retryable_exhausted_primary_once(tmp_path, quiet_logger):
    path, _ = fallback_config(tmp_path, max_attempts=1)
    config = rewrite(path, fallbacks=None)
    write_image(config.paths.source / "a.png")
    timer = ManualTime()
    first = VariantProvider({"pakistani-80s": "retryable", "pakistani-80s-painted": "retryable"})
    # Without fallbacks the painted prompt is a normal variant; remove its job to model the
    # operator adding the fallback afterwards.
    watch(config, quiet_logger, first, timer, cycles=12)
    with closing(sqlite3.connect(config.paths.destination / ".tcfcomic" / "state.db")) as db:
        db.execute("DELETE FROM attempts WHERE job_id IN (SELECT job_id FROM jobs WHERE variant = 'pakistani-80s-painted')")
        db.execute("DELETE FROM jobs WHERE variant = 'pakistani-80s-painted'")
        db.commit()
    assert by_variant(config)["pakistani-80s"] == [("FAILED", "PROVIDER_RETRYABLE")]
    config = rewrite(path, fallbacks=FALLBACKS)
    restarted = VariantProvider()
    watch(config, quiet_logger, restarted, timer, cycles=12)
    assert restarted.variants() == ["pakistani-80s-painted"]
    again = VariantProvider()
    watch(config, quiet_logger, again, timer, cycles=12)
    assert again.requests == []


# --- config changes after the fallback exists ----------------------------------------------


@pytest.mark.parametrize("mode", ["process", "watch"])
def test_fallbacks_removed_after_fallback_job_exists_no_duplicate(tmp_path, quiet_logger, mode):
    path, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    timer = ManualTime()
    first = VariantProvider({"pakistani-80s": "moderation"})
    with processor(config, quiet_logger, first, timer) as p:
        p.process_variants(image)
    assert first.variants() == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    before = jobs(config)

    config = rewrite(path, fallbacks=None)
    again = VariantProvider()
    if mode == "process":
        with processor(config, quiet_logger, again, timer) as p:
            results = p.process_variants(image)
        assert [r.variant for r in results] == list(PROMPTS)
        assert [r.status for r in results] == [JobStatus.SUCCEEDED, JobStatus.FAILED, JobStatus.SUCCEEDED]
    else:
        watch(config, quiet_logger, again, timer, cycles=12)
    assert again.requests == []
    assert jobs(config) == before


def test_fallbacks_removed_new_source_runs_all_prompts_as_primaries(tmp_path, quiet_logger):
    path, config = fallback_config(tmp_path)
    timer = ManualTime()
    write_image(config.paths.source / "a.png")
    watch(config, quiet_logger, VariantProvider({"pakistani-80s": "moderation"}), timer, cycles=12)
    config = rewrite(path, fallbacks=None)
    write_image(config.paths.source / "b.png", color=(1, 2, 3))
    provider = VariantProvider()
    watch(config, quiet_logger, provider, timer, cycles=12)
    assert sorted(provider.variants()) == sorted(PROMPTS)
    assert all(r.source.path.name == "b.png" for r in provider.requests)


# --- fallback itself fails -----------------------------------------------------------------


@pytest.mark.parametrize("fallback_failure", ["moderation", "retryable"])
def test_failed_fallback_never_chains_and_is_not_reattempted(tmp_path, quiet_logger, monkeypatch, capsys, fallback_failure):
    path, config = fallback_config(tmp_path, max_attempts=1)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "moderation", "pakistani-80s-painted": fallback_failure})
    timer = ManualTime()

    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(provider), clock=timer.clock(), **kwargs)

    monkeypatch.setattr(cli, "Processor", Offline)
    assert cli.main(["process", "--config", str(path), str(image)]) != 0
    assert provider.variants() == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    table = by_variant(config)
    assert table["pakistani-80s-painted"][0][0] == "FAILED"
    assert cli.main(["process", "--config", str(path), str(image)]) != 0
    watch(config, quiet_logger, provider, timer, cycles=12)
    assert len(provider.requests) == 3
    assert by_variant(config) == table


def test_fallback_authentication_failure_stops_invocation_exit_3(tmp_path, monkeypatch):
    path, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    provider = VariantProvider({"pakistani-80s": "moderation", "pakistani-80s-painted": "auth"})
    timer = ManualTime()

    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(provider), clock=timer.clock(), **kwargs)

    monkeypatch.setattr(cli, "Processor", Offline)
    assert cli.main(["process", "--config", str(path), str(image)]) == 3
    assert by_variant(config)["pakistani-80s-painted"] == [("FAILED", "AUTHENTICATION_FAILED")]


# --- multiple sources ----------------------------------------------------------------------


def test_watch_fallback_only_for_source_whose_primary_failed(tmp_path, quiet_logger):
    _, config = fallback_config(tmp_path)
    good = write_image(config.paths.source / "good.png", color=(1, 1, 1))
    bad = write_image(config.paths.source / "bad.png", color=(9, 9, 9))

    class PerSource(VariantProvider):
        def transform(self, request, output):
            if request.source.path.name == bad.name and request.variant == "pakistani-80s":
                self.requests.append(request)
                raise PermanentProviderError(ErrorCode.PROVIDER_PERMANENT, "moderation_blocked (offline).")
            return super().transform(request, output)

    provider = PerSource()
    watch(config, quiet_logger, provider, ManualTime(), cycles=20)
    fallback_sources = [r.source.path.name for r in provider.requests if r.variant == "pakistani-80s-painted"]
    assert fallback_sources == [bad.name]
    assert len(provider.requests) == 5
    assert good.exists() and bad.exists()


# --- rate pacing ---------------------------------------------------------------------------


@pytest.mark.parametrize("mode", ["process", "watch"])
def test_fallback_dispatch_respects_requests_per_minute(tmp_path, quiet_logger, mode):
    _, config = fallback_config(tmp_path)  # requests_per_minute: 2 -> 30 s spacing
    image = write_image(config.paths.source / "a.png")
    timer = ManualTime()
    provider = HookProvider({"pakistani-80s": "moderation"}, {}, timer=timer)
    if mode == "process":
        with processor(config, quiet_logger, provider, timer) as p:
            p.process_variants(image)
    else:
        watch(config, quiet_logger, provider, timer, cycles=20)
    starts = dict(provider.starts)
    assert [v for v, _ in provider.starts] == ["tcf-school", "pakistani-80s", "pakistani-80s-painted"]
    assert starts["pakistani-80s"] - starts["tcf-school"] >= 30 - 1e-6
    assert starts["pakistani-80s-painted"] - starts["pakistani-80s"] >= 30 - 1e-6


# --- --retry-failed-variants with fallbacks (Azure offline) --------------------------------


AZ_PROMPTS = {"one": "original one", "one-painted": "painted one"}
AZ_FALLBACKS = {"one": "one-painted"}


def _azure(tmp_path, attempts):
    config = config_for(tmp_path, attempts=attempts)
    return replace(config, provider=replace(config.provider, prompts=dict(AZ_PROMPTS), fallbacks=dict(AZ_FALLBACKS)))


def test_retry_failed_variants_moderated_primary_with_succeeded_fallback_sends_nothing(tmp_path, quiet_logger):
    config = _azure(tmp_path, attempts=4)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    moderation = PermanentProviderError(
        ErrorCode.PROVIDER_PERMANENT,
        "The provider rejected the request. HTTP 400. Provider code moderation_blocked. "
        "The provider blocked the input or generated output under its content policy.")
    provider = Scripted(timer, [moderation, None])
    with processor_for(config, quiet_logger, timer, provider) as p:
        first = p.process_variants(source)
        assert [r.variant for r in first] == ["one", "one-painted"]
        assert [r.status for r in first] == [JobStatus.FAILED, JobStatus.SUCCEEDED]
        fallback_job = p.state.get_job(first[1].job_id)
        again = p.process_variants(source, retry_failed_variants=True)
        # Moderation without captured response evidence is not reopened.
        assert again[0].variant == "one" and again[0].status == JobStatus.FAILED
        assert p.state.get_job(first[1].job_id) == fallback_job
        assert len([j for j in p.state.list_jobs() if j.variant == "one-painted"]) == 1
    assert [r.variant for r in provider.requests] == ["one", "one-painted"]


def test_retry_failed_variants_reopened_primary_never_duplicates_fallback(tmp_path, quiet_logger):
    config = _azure(tmp_path, attempts=1)
    source = write_image(config.paths.source / "x.png")
    timer = Clock()
    eligible = capture_response(source)
    provider = Scripted(timer, [eligible, None])
    with processor_for(config, quiet_logger, timer, provider) as p:
        first = p.process_variants(source)
        primary = p.state.get_job(first[0].job_id)
        assert primary.status == JobStatus.FAILED
    ran_fallback = [r.variant for r in provider.requests].count("one-painted")
    config = replace(config, retry=replace(config.retry, max_attempts=4))
    with processor_for(config, quiet_logger, timer, provider) as p:
        p.process_variants(source, retry_failed_variants=True)
        p.process_variants(source, retry_failed_variants=True)
        fallback_jobs = [j for j in p.state.list_jobs() if j.variant == "one-painted"]
    assert len(fallback_jobs) <= 1
    assert [r.variant for r in provider.requests].count("one-painted") == ran_fallback
    assert [r.variant for r in provider.requests].count("one") == 2


# --- review #1: transient fallback admission failure must not lose the fallback -------------


def _fail_fallback_admission_once(monkeypatch, code=ErrorCode.SOURCE_CHANGED):
    from tcfcomic.domain import AppError
    original = Processor._admit
    calls = {"failed": 0}

    def flaky(self, path, **kwargs):
        if kwargs.get("variant") == "pakistani-80s-painted" and not calls["failed"]:
            calls["failed"] += 1
            raise AppError(code, "The input file is temporarily locked.")
        return original(self, path, **kwargs)

    monkeypatch.setattr(Processor, "_admit", flaky)
    return calls


def test_watch_fallback_retried_after_one_transient_admission_failure(
    tmp_path, quiet_logger, monkeypatch,
):
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "a.png")
    calls = _fail_fallback_admission_once(monkeypatch)
    provider = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, provider, ManualTime(), cycles=40)
    assert calls["failed"] == 1
    assert provider.variants().count("pakistani-80s-painted") == 1
    assert by_variant(config)["pakistani-80s-painted"] == [("SUCCEEDED", None)]


def test_watch_pending_fallback_dropped_when_source_changes_after_admission_failure(
    tmp_path, quiet_logger, monkeypatch, records,
):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    from tcfcomic.domain import AppError
    original = Processor._admit
    state = {"failed": 0}

    def flaky(self, path, **kwargs):
        if kwargs.get("variant") == "pakistani-80s-painted" and not state["failed"]:
            state["failed"] += 1
            _modify(image)()
            raise AppError(ErrorCode.SOURCE_CHANGED, "The input file changed.")
        return original(self, path, **kwargs)

    monkeypatch.setattr(Processor, "_admit", flaky)
    # The rewritten version's primary succeeds, so no fallback may be sent for either version.
    provider = HookProvider({"pakistani-80s": "moderation"}, {})
    provider.failures = {"pakistani-80s": "moderation"}
    original_transform = provider.transform
    seen = {"primary": 0}

    def transform(request, output):
        if request.variant == "pakistani-80s":
            seen["primary"] += 1
            if seen["primary"] > 1:
                provider.failures = {}
        return original_transform(request, output)

    provider.transform = transform
    watch(config, quiet_logger, provider, ManualTime(), cycles=40)
    assert state["failed"] == 1
    assert "pakistani-80s-painted" not in provider.variants()
    assert len(records.events("fallback_pending_dropped")) == 1
    assert not fallback_rows(config)


# --- review #3: watch "existing" fallback is scoped to the latest primary's SHA-256 ---------


def test_watch_existing_fallback_for_old_content_does_not_bill_when_new_primary_succeeds(
    tmp_path, quiet_logger,
):
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    first = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, first, ManualTime(), cycles=20)
    assert by_variant(config)["pakistani-80s-painted"] == [("SUCCEEDED", None)]
    info = image.stat()
    write_image(image, color=(10, 200, 10), image_format="PNG")
    assert image.stat().st_size == info.st_size
    os.utime(image, ns=(info.st_atime_ns, info.st_mtime_ns))
    second = VariantProvider({})
    watch(config, quiet_logger, second, ManualTime(), cycles=20)
    assert "pakistani-80s" in second.variants()
    assert "pakistani-80s-painted" not in second.variants()
    assert len(fallback_rows(config)) == 1

# --- security: fallback source hashing refuses links / non-regular files and is bounded ------


def _stat_with(info, **changes):
    fields = {name: getattr(info, name) for name in dir(info) if name.startswith("st_")}
    fields.update(changes)
    return type("FakeStat", (), fields)()


def test_file_sha256_hashes_regular_file_of_expected_size(tmp_path):
    import hashlib
    from tcfcomic.processor import _file_sha256
    path = tmp_path / "a.bin"
    path.write_bytes(b"x" * 3_000_000)
    assert _file_sha256(path, 3_000_000) == hashlib.sha256(b"x" * 3_000_000).hexdigest()


@pytest.mark.parametrize("expected", [2_999_999, 3_000_001, 0])
def test_file_sha256_size_mismatch_is_no_match(tmp_path, expected):
    from tcfcomic.processor import _file_sha256
    path = tmp_path / "a.bin"
    path.write_bytes(b"x" * 3_000_000)
    assert _file_sha256(path, expected) is None


def test_file_sha256_bounds_read_when_file_grows_after_stat(tmp_path, monkeypatch):
    from tcfcomic import processor as module
    path = tmp_path / "a.bin"
    path.write_bytes(b"x" * 10)
    real_lstat, real_fstat = os.lstat, os.fstat
    small = lambda info: _stat_with(info, st_size=4)
    monkeypatch.setattr(module.os, "lstat", lambda p, *a, **k: small(real_lstat(p, *a, **k)))
    monkeypatch.setattr(module.os, "fstat", lambda fd: small(real_fstat(fd)))
    reads = []
    real_open = open

    class Spy:
        def __init__(self, stream):
            self.stream = stream

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            self.stream.close()

        def fileno(self):
            return self.stream.fileno()

        def read(self, size=-1):
            assert size >= 0, "unbounded read"
            data = self.stream.read(size)
            reads.append(len(data))
            return data

    monkeypatch.setattr(module, "open", lambda p, mode="r", *a, **k: Spy(real_open(p, mode, *a, **k)),
                        raising=False)
    assert module._file_sha256(path, 4) is None
    assert sum(reads) <= 5


def test_file_sha256_refuses_reparse_point_without_opening(tmp_path, monkeypatch):
    import stat as stat_module
    from tcfcomic import processor as module
    path = tmp_path / "a.bin"
    path.write_bytes(b"x" * 10)
    real_lstat = os.lstat
    flag = getattr(stat_module, "FILE_ATTRIBUTE_REPARSE_POINT", 0x400)
    monkeypatch.setattr(stat_module, "FILE_ATTRIBUTE_REPARSE_POINT", flag, raising=False)
    monkeypatch.setattr(module.os, "lstat", lambda p, *a, **k: _stat_with(
        real_lstat(p, *a, **k), st_file_attributes=flag))
    opened = []
    monkeypatch.setattr(module, "open", lambda *a, **k: opened.append(a) or (_ for _ in ()).throw(
        AssertionError("opened a reparse point")), raising=False)
    assert module._file_sha256(path, 10) is None
    assert not opened


def test_file_sha256_refuses_non_regular_file(tmp_path):
    from tcfcomic.processor import _file_sha256
    directory = tmp_path / "dir"
    directory.mkdir()
    assert _file_sha256(directory, os.lstat(directory).st_size) is None


def test_file_sha256_refuses_symlink(tmp_path):
    from tcfcomic.processor import _file_sha256
    target = tmp_path / "target.bin"
    target.write_bytes(b"x" * 10)
    link = tmp_path / "link.bin"
    try:
        link.symlink_to(target)
    except OSError:
        pytest.skip("symlink creation requires privilege")
    assert _file_sha256(link, 10) is None


def test_file_sha256_refuses_file_swapped_after_lstat(tmp_path, monkeypatch):
    from tcfcomic import processor as module
    path = tmp_path / "a.bin"
    path.write_bytes(b"x" * 10)
    real_fstat = os.fstat
    monkeypatch.setattr(module.os, "fstat", lambda fd: _stat_with(
        real_fstat(fd), st_ino=real_fstat(fd).st_ino + 1))
    assert module._file_sha256(path, 10) is None


def test_fallback_decision_duplicate_primaries_does_not_hash_oversized_source(
    tmp_path, quiet_logger, monkeypatch,
):
    # The duplicate-primary disambiguation hash is bounded by the candidate size.
    from tcfcomic import processor as module
    seen = []
    real = module._file_sha256
    monkeypatch.setattr(module, "_file_sha256", lambda path, size: seen.append(size) or real(path, size))
    _, config = fallback_config(tmp_path)
    image = write_image(config.paths.source / "a.png")
    first = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, first, ManualTime(), cycles=20)
    info = image.stat()
    write_image(image, color=(10, 200, 10), image_format="PNG")
    os.utime(image, ns=(info.st_atime_ns, info.st_mtime_ns))
    watch(config, quiet_logger, VariantProvider({"pakistani-80s": "moderation"}), ManualTime(), cycles=20)
    assert seen and all(size == image.stat().st_size for size in seen)

# --- review M1: a permanently locked pending fallback must not flood the log -------------------


def test_watch_pending_fallback_repeated_admission_failure_logs_bounded(
    tmp_path, quiet_logger, monkeypatch, records,
):
    from tcfcomic.domain import AppError
    _, config = fallback_config(tmp_path)
    write_image(config.paths.source / "a.png")
    original = Processor._admit
    calls = {"failed": 0}
    failures = 60

    def locked(self, path, **kwargs):
        if kwargs.get("variant") == "pakistani-80s-painted" and calls["failed"] < failures:
            calls["failed"] += 1
            raise AppError(ErrorCode.SOURCE_CHANGED, "The input file is temporarily locked.")
        return original(self, path, **kwargs)

    monkeypatch.setattr(Processor, "_admit", locked)
    provider = VariantProvider({"pakistani-80s": "moderation"})
    watch(config, quiet_logger, provider, ManualTime(), cycles=120)
    # Retried every cycle until the lock cleared ...
    assert calls["failed"] == failures
    assert provider.variants().count("pakistani-80s-painted") == 1
    assert by_variant(config)["pakistani-80s-painted"] == [("SUCCEEDED", None)]
    # ... but only the first failure and a power-of-two schedule were logged, not 60 errors.
    failed = records.events("watch_item_failed")
    assert 1 <= len(failed) <= 8
    assert len(records.events("fallback_pending_admitted")) == 1
