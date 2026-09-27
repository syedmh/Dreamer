from __future__ import annotations

import hashlib
import io
import json
import threading
from contextlib import ExitStack
from dataclasses import replace
from pathlib import Path

import pytest
import yaml
from PIL import Image

from conftest import write_config, write_image, write_mpo
from tcfcomic import cli
from tcfcomic.cli import build_parser
from tcfcomic.config import load_config
from tcfcomic.domain import AppError, ErrorCode, JobStatus, PermanentProviderError, StableCandidate
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.openai import _prepare_upload
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.runtime import DestinationSession
from tcfcomic.state import StateStore
from test_rate_pacing import ManualTime, TimedProvider


def named_config(root):
    path, _ = write_config(root)
    data = yaml.safe_load(path.read_text())
    data["provider"]["prompts"] = {"tcf-school": "First portrait.", "pakistani-80s": "Second portrait."}
    data["provider"]["requests_per_minute"] = 2
    path.write_text(yaml.safe_dump(data, sort_keys=False), encoding="utf-8")
    return path, load_config(path)


def test_named_originals_outputs_global_quota_restart(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    image = write_image(config.paths.source / "original.png")
    digest = hashlib.sha256(image.read_bytes()).hexdigest()
    timer = ManualTime()

    class Recording(TimedProvider):
        def __init__(self):
            super().__init__(timer, duration=7)
            self.inputs = []

        def transform(self, request, output):
            self.inputs.append((request.prompt, hashlib.sha256(request.source.staged_path.read_bytes()).hexdigest()))
            return super().transform(request, output)

    provider = Recording()
    for _ in range(2):
        with Processor(config, quiet_logger, clock=timer.clock(),
                       runner=InProcessAttemptRunner(provider)) as processor:
            results = processor.process_variants(image)
            assert [r.variant for r in results] == ["tcf-school", "pakistani-80s"]
            assert all(r.status == JobStatus.SUCCEEDED for r in results)
            assert all(r.variant in r.output_path.name for r in results)
            assert len(processor.state.list_jobs()) == 2
    assert provider.starts == [0, 37]
    assert provider.inputs == [("First portrait.", digest), ("Second portrait.", digest)]
    assert len(list(config.paths.destination.glob("*.png"))) == 2


def test_reset_watch_only_explicit_help():
    parser = build_parser()
    assert parser.parse_args(["watch", "--config", "x", "--reset-state"]).reset_state
    with pytest.raises(SystemExit):
        parser.parse_args(["process", "--config", "x", "--reset-state", "image.png"])


def test_state_modes_refuse_without_modifying_database(tmp_path, quiet_logger):
    _, legacy = write_config(tmp_path)
    with Processor(legacy, quiet_logger):
        pass
    db = legacy.paths.destination / ".tcfcomic" / "state.db"
    before = db.read_bytes()
    named = replace(legacy, provider=replace(legacy.provider, prompts={"one": "Named"}))
    with pytest.raises(AppError, match="reset-state"):
        Processor(named, quiet_logger)
    assert db.read_bytes() == before


def test_reset_archives_without_touching_source_or_outputs(tmp_path, quiet_logger):
    from tcfcomic.runtime import DestinationSession

    _, config = write_config(tmp_path)
    image = write_image(config.paths.source / "original.png")
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(TimedProvider(ManualTime()))) as processor:
        result = processor.process_path(image)
    before = image.read_bytes(), result.output_path.read_bytes()
    with DestinationSession(config.paths.destination) as session:
        backup = session.reset(config, threading.Event(), report=lambda message: None)
        assert backup.name.startswith(".tcfcomic-backup-")
        assert (backup / "state.db").is_file()
        assert not (config.paths.destination / ".tcfcomic").exists()
    assert (image.read_bytes(), result.output_path.read_bytes()) == before


def make_processor(config, logger, timer, provider, **kwargs):
    return Processor(config, logger, clock=timer.clock(), runner=InProcessAttemptRunner(provider), **kwargs)


def admit_variant(processor, image, variant):
    info = image.stat()
    return processor._admit(image, candidate=StableCandidate(image, info.st_size, info.st_mtime_ns),
                            shutdown_event=threading.Event(), variant=variant)


@pytest.mark.parametrize("outcome,expected", [
    ("permanent", [0, 30]), ("ambiguous", [0, 30]), ("corrupt", [0, 30]),
    (85, [0, 85]), ("retryable", [0, 30]),
])
def test_variant_failure_and_429_do_not_block_independent_slot_or_replay_success(
    tmp_path, quiet_logger, outcome, expected,
):
    _, config = named_config(tmp_path)
    config = replace(config, retry=replace(config.retry, max_attempts=1))
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[outcome])
    image = write_image(config.paths.source / "test.png")
    for _ in range(2):
        with make_processor(config, quiet_logger, timer, provider) as processor:
            results = processor.process_variants(image)
            assert results[0].status != JobStatus.SUCCEEDED
            assert results[1].status == JobStatus.SUCCEEDED
            assert [r.attempts for r in results] == [1, 1]
    assert provider.starts == expected


def test_retries_all_count_and_no_unrelated_backlog_drained(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    timer = ManualTime()
    provider = TimedProvider(timer, outcomes=[70, "success"])
    image = write_image(config.paths.source / "target.png")
    other = write_image(config.paths.source / "unrelated.png")
    with make_processor(config, quiet_logger, timer, provider) as processor:
        unrelated = admit_variant(processor, other, "tcf-school")
        results = processor.process_variants(image)
        assert all(r.status == JobStatus.SUCCEEDED for r in results)
        assert provider.starts == [0, 70, 100]
        assert processor.state.attempt_count(unrelated.job_id) == 0


def test_add_variant_only_creates_new_slot_changed_success_not_rerendered(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    timer = ManualTime()
    provider = FakeProvider()
    image = write_image(config.paths.source / "test.png")
    with make_processor(config, quiet_logger, timer, provider) as processor:
        original = processor.process_variants(image)
    changed = replace(config, provider=replace(config.provider, prompts={
        "tcf-school": "New text is NOT permission to rerender.", "pakistani-80s": "Second portrait.",
        "third": "New independent slot.",
    }))
    with make_processor(changed, quiet_logger, timer, provider) as processor:
        results = processor.process_variants(image)
        assert [r.job_id for r in results[:2]] == [r.job_id for r in original]
        assert len(processor.state.list_jobs()) == 3
    assert len(provider.requests) == 3
    assert provider.requests[-1].prompt == "New independent slot."


@pytest.mark.parametrize("change", ["text", "removed", "endpoint", "auth"])
def test_pending_drift_fails_closed_per_slot(tmp_path, quiet_logger, change):
    _, config = named_config(tmp_path)
    if change in {"endpoint", "auth"}:
        config = replace(config, provider=replace(
            config.provider, name="azure_openai", endpoint="https://offline.openai.azure.com",
        ))
    timer = ManualTime()
    provider = FakeProvider()
    image = write_image(config.paths.source / "test.png")
    with make_processor(config, quiet_logger, timer, provider) as processor:
        first = admit_variant(processor, image, "tcf-school")
        second = admit_variant(processor, image, "pakistani-80s")
    prompts = dict(config.provider.prompts)
    options = {}
    if change == "text":
        prompts["tcf-school"] = "Changed."
    elif change == "removed":
        del prompts["tcf-school"]
    elif change == "endpoint":
        options["endpoint"] = "https://other.openai.azure.com"
    else:
        options["authentication"] = "interactive"
    changed = replace(config, provider=replace(config.provider, prompts=prompts, **options))
    from test_explicit_input_retry import OfflineAuthenticatedRunner, OfflineSession
    with Processor(changed, quiet_logger, clock=timer.clock(),
                   runner=OfflineAuthenticatedRunner(provider),
                   auth_session=OfflineSession() if change == "auth" else None) as processor:
        assert processor._dispatch_one(threading.Event(), first.job_id)
        assert processor.state.get_job(first.job_id).status == JobStatus.FAILED
        assert processor.state.attempt_count(first.job_id) == 0
        assert processor.state.get_job(second.job_id).status == JobStatus.READY
        if change in {"text", "removed"}:
            assert processor._run_until_terminal(second.job_id, threading.Event()).status == JobStatus.SUCCEEDED
            assert provider.requests[0].prompt == "Second portrait."
        else:
            assert provider.requests == []


@pytest.mark.parametrize("boundary", ["before_rename", "after_rename"])
def test_variant_publication_crash_recovers_without_extra_attempt(tmp_path, quiet_logger, boundary):
    _, config = named_config(tmp_path)
    image = write_image(config.paths.source / "test.png")
    timer = ManualTime()
    provider = FakeProvider()
    def crash(point):
        if point == boundary:
            raise RuntimeError("simulated crash")
    with make_processor(config, quiet_logger, timer, provider, publication_hook=crash) as processor:
        with pytest.raises(RuntimeError, match="simulated"):
            processor.process_variants(image)
    with make_processor(config, quiet_logger, timer, provider) as processor:
        results = processor.process_variants(image)
        assert all(r.status == JobStatus.SUCCEEDED and r.variant in r.output_path.name for r in results)
        assert len(provider.requests) == 2
        assert not list(processor.staging.glob("*.input"))


def test_variant_dispatch_crash_is_ambiguous_only_in_own_slot(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    timer = ManualTime()
    provider = FakeProvider()
    image = write_image(config.paths.source / "test.png")
    with make_processor(config, quiet_logger, timer, provider) as processor:
        first = admit_variant(processor, image, "tcf-school")
        second = admit_variant(processor, image, "pakistani-80s")
        processor.state.claim_ready(first.job_id, processor.clock.now())
        processor.state.begin_attempt(first.job_id)
    with make_processor(config, quiet_logger, timer, provider) as processor:
        assert processor.state.get_job(first.job_id).status == JobStatus.AMBIGUOUS
        assert processor.state.get_job(second.job_id).status == JobStatus.READY
        result = processor.process_variants(image)
        assert result[1].status == JobStatus.SUCCEEDED
        assert len(provider.requests) == 1


def test_oversize_metadata_repair_never_selects_other_variant(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    timer = ManualTime()
    provider = FakeProvider()
    image = write_image(config.paths.source / "test.png")
    with make_processor(config, quiet_logger, timer, provider) as processor:
        first = admit_variant(processor, image, "tcf-school")
        processor.config = replace(config, limits=replace(config.limits, max_input_bytes=1))
        with pytest.raises(AppError) as caught:
            admit_variant(processor, image, "pakistani-80s")
        assert caught.value.code == ErrorCode.IMAGE_LIMIT_EXCEEDED
        assert admit_variant(processor, image, "tcf-school").job_id == first.job_id
        assert len(processor.state.list_jobs()) == 1


def test_watch_backpressure_does_not_acknowledge_missing_second_variant(tmp_path, quiet_logger, monkeypatch):
    import tcfcomic.processor as module
    _, config = named_config(tmp_path)
    config = replace(config, watch=replace(config.watch, poll_interval_seconds=10))
    timer = ManualTime()
    provider = FakeProvider()
    image = write_image(config.paths.source / "test.png")
    monkeypatch.setattr(module, "_MAX_STAGED_JOBS", 1)
    with make_processor(config, quiet_logger, timer, provider) as processor:
        processor.watch(max_cycles=6)
        jobs = processor.state.list_jobs()
        assert len(jobs) == 2
        assert {j.variant for j in jobs} == {"tcf-school", "pakistani-80s"}
        assert all(j.status == JobStatus.SUCCEEDED for j in jobs)
    assert len(provider.requests) == 2


def test_named_database_refuses_legacy_without_mutation(tmp_path, quiet_logger):
    _, config = named_config(tmp_path)
    with Processor(config, quiet_logger):
        pass
    db = config.paths.destination / ".tcfcomic" / "state.db"
    before = db.read_bytes()
    legacy = replace(config, provider=replace(config.provider, prompts=None))
    for action in (
        lambda: Processor(legacy, quiet_logger),
        lambda: StateStore(db),
    ):
        with pytest.raises(AppError, match="reset-state"):
            action()
        assert db.read_bytes() == before


@pytest.mark.parametrize("outcomes,code,count", [
    (["success", "success"], 0, 2), (["permanent", "success"], 5, 1),
    (["success", "permanent"], 5, 1),
])
def test_named_cli_stdout_exit_and_offline_execution(tmp_path, quiet_logger, monkeypatch, capsys, outcomes, code, count):
    path, config = named_config(tmp_path)
    timer = ManualTime()
    provider = FakeProvider(outcomes)
    image = write_image(config.paths.source / "test.png")
    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(provider), clock=timer.clock(), **kwargs)
    monkeypatch.setattr(cli, "Processor", Offline)
    assert cli.main(["process", "--config", str(path), str(image)]) == code
    captured = capsys.readouterr()
    lines = captured.out.splitlines()
    assert len(lines) == count
    assert all(Path(line).is_file() for line in lines)
    assert "First portrait." not in captured.err and "Second portrait." not in captured.err
    assert len(provider.requests) == 2


def test_fatal_auth_stops_variants_and_preserves_prior_stdout(tmp_path, monkeypatch, capsys):
    path, config = named_config(tmp_path)
    image = write_image(config.paths.source / "test.png")
    timer = ManualTime()
    class Provider(FakeProvider):
        def transform(self, request, output):
            if request.variant == "pakistani-80s":
                raise PermanentProviderError(ErrorCode.AUTHENTICATION_FAILED, "Offline auth failure.")
            return super().transform(request, output)
    class Offline(Processor):
        def __init__(self, config, logger, **kwargs):
            super().__init__(config, logger, runner=InProcessAttemptRunner(Provider()), clock=timer.clock(), **kwargs)
    monkeypatch.setattr(cli, "Processor", Offline)
    assert cli.main(["process", "--config", str(path), str(image)]) == 3
    lines = capsys.readouterr().out.splitlines()
    assert len(lines) == 1 and "tcf-school" in lines[0]


@pytest.mark.parametrize("azure", [False, True])
def test_two_three_frame_mpo_originals_variants_explicit_retry_audits(tmp_path, quiet_logger, monkeypatch, azure):
    _, config = named_config(tmp_path)
    images = [write_mpo(config.paths.source / f"synthetic-{index}.JPG") for index in range(2)]
    timer = ManualTime()
    class Normalizing(FakeProvider):
        def transform(self, request, output):
            with ExitStack() as stack:
                name, stream, mime = _prepare_upload(request, stack, azure=azure)
                payload = stream.read()
            assert (name, mime) == ("input.png", "image/png")
            with Image.open(io.BytesIO(payload)) as image:
                assert image.n_frames == 1 and image.size == (18, 24)
                assert not image.getexif() and image.info == {}
                assert image.getpixel((0, 0))[0] > 240
            assert b"private" not in payload
            return super().transform(request, output)
    provider = Normalizing()
    original_bytes = [image.read_bytes() for image in images]
    with make_processor(config, quiet_logger, timer, provider) as processor:
        def reject(*args, **kwargs):
            raise AppError(ErrorCode.INVALID_IMAGE, "Historical input rejection.")
        with monkeypatch.context() as patch:
            patch.setattr("tcfcomic.processor.validate_input", reject)
            old = [admit_variant(processor, image, variant)
                   for image in images for variant in config.provider.variants]
        assert all(r.status == JobStatus.FAILED and r.attempts == 0 for r in old)
        processor.config = replace(config, provider=replace(config.provider, prompts={
            "tcf-school": "Changed first.", "pakistani-80s": "Changed second.",
        }))
        for image in images:
            assert all(r.status == JobStatus.FAILED for r in processor.process_variants(image))
            assert all(r.status == JobStatus.SUCCEEDED for r in processor.process_variants(
                image, retry_input_rejection=True,
            ))
        jobs = processor.state.list_jobs()
        assert {j.job_id for j in jobs} == {r.job_id for r in old}
        audits = list(config.paths.quarantine.glob("*.mpo-request-transition.*.json"))
        assert len(audits) == 4
        for audit in audits:
            payload = json.loads(audit.read_bytes())
            job = processor.state.get_job(payload["job_id"])
            identity = processor._request_identity(job.variant)
            expected = hashlib.sha256(json.dumps(
                [identity.provider, identity.model, identity.prompt_hash, identity.variant],
                separators=(",", ":"), ensure_ascii=True,
            ).encode()).hexdigest()
            assert payload["variant"] == job.variant
            assert payload["new_request_sha256"] == expected
        for image in images:
            processor.process_variants(image, retry_input_rejection=True)
    assert len(provider.requests) == 4
    assert [image.read_bytes() for image in images] == original_bytes
