"""Local restoration uses only synthetic files and an in-process fake provider."""
from __future__ import annotations

import json
import hashlib
import logging
import os
import shutil
from dataclasses import asdict, replace
from pathlib import Path
from types import SimpleNamespace

import pytest
from PIL import Image

from conftest import write_config, write_image
from tcfcomic.domain import AppError, ErrorCode, JobStatus, PermanentProviderError
from tcfcomic.processor import Processor
from tcfcomic.providers.fake import FakeProvider
from tcfcomic.providers.worker import InProcessAttemptRunner
from tcfcomic.quarantine import PUBLISHED_OUTPUT_MISSING, preserve_published_restoration_audits


def history(processor):
    return tuple(tuple(row) for row in processor.state.connection.execute(
        "SELECT * FROM attempts ORDER BY job_id, attempt_no"
    ))


def published(tmp_path, logger, *, named=True):
    _, config = write_config(
        tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com",
    )
    config = replace(
        config, retry=replace(config.retry, azure_response_retries=True),
        provider=replace(config.provider, prompts={"portrait": "Synthetic portrait"} if named else None),
    )
    source = write_image(config.paths.source / "synthetic.png")
    provider = FakeProvider()
    with Processor(config, logger, runner=InProcessAttemptRunner(provider)) as processor:
        result = processor.process_path(source, variant="portrait" if named else "")
        job = processor.state.get_job(result.job_id)
        attempts = history(processor)
    backup = config.paths.destination / "Backup"
    backup.mkdir()
    saved = backup / job.output_name
    result.output_path.rename(saved)
    return config, source, provider, job, attempts, saved


@pytest.mark.parametrize("named", [False, True])
@pytest.mark.parametrize("restore_when", ["startup", "admission"])
@pytest.mark.parametrize("legacy_message", [False, True])
def test_restored_output_reconciles_without_dispatch(
    tmp_path, quiet_logger, named, restore_when, legacy_message,
):
    config, source, provider, original, attempts, saved = published(tmp_path, quiet_logger, named=named)
    output = config.paths.destination / original.output_name
    failure_path = config.paths.quarantine / f"{original.job_id}.json"
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
        failed = processor.state.get_job(original.job_id)
        assert failed.status == JobStatus.FAILED
        assert history(processor) == attempts
        assert len(provider.requests) == 1
    if legacy_message:
        data = json.loads(failure_path.read_bytes())
        data["safe_message"] = "A recovered internal file is missing."
        failure_path.write_text(json.dumps(data) + "\n", encoding="utf-8")
    evidence = failure_path.read_bytes()
    if restore_when == "startup":
        shutil.copyfile(saved, output)
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
        if restore_when == "admission":
            shutil.copyfile(saved, output)
            result = processor.process_path(
                source, variant=original.variant, retry_failed_variants=True,
            )
            assert result.job_id == original.job_id
        restored = processor.state.get_job(original.job_id)
        assert restored.status == JobStatus.SUCCEEDED
        assert replace(restored, updated_at=original.updated_at) == original
        assert history(processor) == attempts
        assert failure_path.read_bytes() == evidence
        audits = list(config.paths.quarantine.glob(f"{original.job_id}.published-failure.*.json"))
        assert len(audits) == 1 and audits[0].read_bytes() == evidence
        markers = list(config.paths.quarantine.glob(f"{original.job_id}.output-restoration.*.json"))
        assert len(markers) == 1
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
        result = processor.process_path(source, variant=original.variant, retry_failed_variants=True)
        assert result.job_id == original.job_id and result.status == JobStatus.SUCCEEDED
        assert processor.state.get_job(original.job_id) == restored
        assert history(processor) == attempts
    assert len(provider.requests) == 1
    assert output.read_bytes() == saved.read_bytes()
    assert failure_path.read_bytes() == evidence


@pytest.fixture
def failed_publication(tmp_path, quiet_logger):
    config, source, provider, original, attempts, saved = published(tmp_path, quiet_logger)
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as processor:
        job = processor.state.get_job(original.job_id)
        assert job.status == JobStatus.FAILED and job.error_code == ErrorCode.STATE_FAILED.value
        failure = config.paths.quarantine / f"{job.job_id}.json"
        yield SimpleNamespace(
            config=config, source=source, provider=provider, processor=processor,
            job=job, attempts=attempts, saved=saved, output=config.paths.destination / job.output_name,
            failure=failure, evidence=failure.read_bytes(),
        )


@pytest.mark.parametrize("damage", [
    "missing", "wrong-hash", "corrupt", "invalid-with-matching-hash", "animated",
    "limits", "byte-limit", "hardlink", "reparse", "renamed", "unsafe-name", "scratch-reference",
])
def test_artifact_denials_preserve_failure_and_never_dispatch(failed_publication, damage, monkeypatch):
    f = failed_publication
    p = f.processor
    if damage != "missing":
        shutil.copyfile(f.saved, f.output)
    if damage == "wrong-hash":
        write_image(f.output, color=(99, 2, 3))
    elif damage in {"corrupt", "invalid-with-matching-hash"}:
        f.output.write_bytes(b"not a png")
        if damage == "invalid-with-matching-hash":
            p.state.connection.execute(
                "UPDATE jobs SET output_sha256=? WHERE job_id=?",
                (hashlib.sha256(f.output.read_bytes()).hexdigest(), f.job.job_id),
            )
    elif damage == "animated":
        with Image.new("RGB", (2, 2), "red") as one, Image.new("RGB", (2, 2), "blue") as two:
            one.save(f.output, format="PNG", save_all=True, append_images=[two])
        p.state.connection.execute(
            "UPDATE jobs SET output_sha256=? WHERE job_id=?",
            (hashlib.sha256(f.output.read_bytes()).hexdigest(), f.job.job_id),
        )
    elif damage == "limits":
        p.config = replace(f.config, limits=replace(f.config.limits, max_width=1))
    elif damage == "byte-limit":
        p.config = replace(f.config, limits=replace(f.config.limits, max_output_bytes=1))
    elif damage == "hardlink":
        f.output.unlink()
        os.link(f.saved, f.output)
    elif damage == "reparse":
        from tcfcomic import path_safety
        original = path_safety.is_reparse_or_link
        monkeypatch.setattr(path_safety, "is_reparse_or_link", lambda path: path == f.output or original(path))
    elif damage in {"renamed", "unsafe-name"}:
        p.state.connection.execute(
            "UPDATE jobs SET output_name=? WHERE job_id=?",
            ("unowned.png" if damage == "renamed" else "..\\escape.png", f.job.job_id),
        )
        if damage == "renamed":
            f.output.rename(f.output.with_name("unowned.png"))
    elif damage == "scratch-reference":
        p.state.connection.execute(
            "UPDATE jobs SET temp_name=? WHERE job_id=?", (".retained.tmp", f.job.job_id),
        )
        (f.config.paths.destination / ".retained.tmp").write_bytes(b"must remain")
    before = p.state.get_job(f.job.job_id)
    output_bytes = f.output.read_bytes() if f.output.exists() else None
    for flags in ({}, {"retry_failed_variants": True, "retry_input_rejection": True}):
        result = p.process_path(f.source, variant=f.job.variant, **flags)
        assert result.job_id == f.job.job_id and result.status == JobStatus.FAILED
        assert p.state.get_job(f.job.job_id) == before
        assert history(p) == f.attempts
        assert f.failure.read_bytes() == f.evidence
        assert (f.output.read_bytes() if f.output.exists() else None) == output_bytes
    assert len(f.provider.requests) == 1
    assert not list(f.config.paths.quarantine.glob(f"{f.job.job_id}.output-restoration.*.json"))


@pytest.mark.parametrize("field,value", [
    ("stage", "provider"), ("safe_message", "The staged input does not belong to the expected job."),
    ("job_id", "b" * 32), ("source_name", "other.png"),
    ("source_version", {"size": 1, "mtime_ns": 1, "sha256": "a" * 64}),
    ("variant", "other"), ("attempts", 2), ("attempts", True),
    ("first_seen_at", "2000-01-01T00:00:00+00:00"),
    ("error_code", "PROVIDER_PERMANENT"), ("failed_at", "2000-01-01T00:00:00+00:00"),
    ("schema_version", True),
])
def test_provenance_mismatch_cannot_promote(failed_publication, field, value):
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    data = json.loads(f.evidence)
    data[field] = value
    f.failure.write_text(json.dumps(data), encoding="utf-8")
    evidence = f.failure.read_bytes()
    assert not f.processor._reconcile_restored_output(f.job)
    assert f.processor.state.get_job(f.job.job_id) == f.job
    assert history(f.processor) == f.attempts
    assert f.failure.read_bytes() == evidence
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("damage", ["duplicate-keys", "oversized", "missing", "hardlink"])
def test_canonical_provenance_requires_bounded_private_file(failed_publication, damage):
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    if damage == "duplicate-keys":
        f.failure.write_bytes(b'{"stage":"recovery",' + f.evidence.lstrip()[1:])
    elif damage == "oversized":
        f.failure.write_bytes(f.evidence + b" " * 65537)
    elif damage == "missing":
        f.failure.unlink()
    else:
        os.link(f.failure, f.failure.with_name("retained-evidence.json"))
    evidence = f.failure.read_bytes() if f.failure.exists() else None
    assert not f.processor._reconcile_restored_output(f.job)
    assert f.processor.state.get_job(f.job.job_id) == f.job
    assert history(f.processor) == f.attempts
    assert (f.failure.read_bytes() if f.failure.exists() else None) == evidence
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("damage", ["latest-not-success", "unfinished", "open-attempt", "gap"])
def test_history_must_end_in_genuine_completed_success(failed_publication, damage):
    f = failed_publication
    p = f.processor
    shutil.copyfile(f.saved, f.output)
    if damage == "latest-not-success":
        p.state.connection.execute(
            """UPDATE attempts SET state='permanent', error_code='PROVIDER_PERMANENT',
               safe_message='Synthetic rejection' WHERE job_id=?""", (f.job.job_id,),
        )
    elif damage == "unfinished":
        p.state.connection.execute("UPDATE attempts SET finished_at=NULL WHERE job_id=?", (f.job.job_id,))
    elif damage == "open-attempt":
        p.state.connection.execute(
            """INSERT INTO attempts (job_id,attempt_no,state,started_at)
               VALUES (?,2,'dispatching',?)""", (f.job.job_id, f.job.created_at),
        )
    else:
        p.state.connection.execute("UPDATE attempts SET attempt_no=2 WHERE job_id=?", (f.job.job_id,))
    attempts = history(p)
    assert not p._reconcile_restored_output(f.job)
    assert p.state.get_job(f.job.job_id) == f.job
    assert history(p) == attempts
    assert f.failure.read_bytes() == f.evidence
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("race", ["job", "history", "source-binding", "output-binding"])
@pytest.mark.parametrize("timing", ["before-transaction", "inside-transaction"])
def test_cas_rechecks_full_snapshot(failed_publication, monkeypatch, race, timing):
    f = failed_publication
    p = f.processor
    shutil.copyfile(f.saved, f.output)
    observed = []

    def mutate():
        if race == "history":
            p.state.connection.execute("UPDATE attempts SET duration_ms=duration_ms+1 WHERE job_id=?", (f.job.job_id,))
        else:
            column, value = {
                "job": ("prompt_hash", "e" * 64), "source-binding": ("source_path_key", "f" * 64),
                "output-binding": ("output_name", "other.png"),
            }[race]
            p.state.connection.execute(f"UPDATE jobs SET {column}=? WHERE job_id=?", (value, f.job.job_id))
        observed.append((p.state.get_job(f.job.job_id), history(p)))

    if timing == "before-transaction":
        original = p.state.reconcile_restored_publication

        def racing(*args, **kwargs):
            mutate()
            return original(*args, **kwargs)

        monkeypatch.setattr(p.state, "reconcile_restored_publication", racing)
    else:
        monkeypatch.setattr(p.state, "_transition_hook", lambda point: mutate() if point == "restored_publication_before_update" else None)
    assert not p._reconcile_restored_output(f.job)
    assert observed and p.state.get_job(f.job.job_id).status == JobStatus.FAILED
    # An evidence exception rolls back the synthetic in-transaction mutation;
    # a pre-transaction competing writer must remain untouched.
    expected = observed[0] if timing == "before-transaction" else (f.job, f.attempts)
    assert (p.state.get_job(f.job.job_id), history(p)) == expected
    assert f.failure.read_bytes() == f.evidence
    assert f.output.read_bytes() == f.saved.read_bytes()
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("race", ["output", "provenance", "history"])
def test_artifacts_rechecked_after_audit_io(failed_publication, monkeypatch, race):
    from tcfcomic import processor as module
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    original = module.preserve_published_restoration_audits
    changed = []

    def racing(*args):
        result = original(*args)
        if race == "history":
            f.processor.state.connection.execute(
                "UPDATE attempts SET duration_ms=duration_ms+1 WHERE job_id=?", (f.job.job_id,),
            )
            changed.append(history(f.processor))
        else:
            target = f.output if race == "output" else f.failure
            target.write_bytes(b"synthetic changed evidence")
            changed.append(target)
        return result

    monkeypatch.setattr(module, "preserve_published_restoration_audits", racing)
    assert not f.processor._reconcile_restored_output(f.job)
    assert changed
    if race == "history":
        assert history(f.processor) == changed[0] != f.attempts
        assert f.failure.read_bytes() == f.evidence
        assert f.output.read_bytes() == f.saved.read_bytes()
    else:
        assert changed[0].read_bytes() == b"synthetic changed evidence"
        assert history(f.processor) == f.attempts
    assert f.processor.state.get_job(f.job.job_id) == f.job
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("artifact", ["failure-copy", "marker"])
def test_conflicting_immutable_audit_is_not_overwritten(failed_publication, artifact):
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    attempts = f.processor.state.completed_publication_attempts(f.job.job_id)
    audits = preserve_published_restoration_audits(f.job, f.config.paths.quarantine, f.evidence, attempts)
    target = audits[0 if artifact == "failure-copy" else 1]
    target.write_bytes(b"conflicting retained audit")
    assert not f.processor._reconcile_restored_output(f.job)
    assert f.processor.state.get_job(f.job.job_id) == f.job
    assert target.read_bytes() == b"conflicting retained audit"
    assert f.failure.read_bytes() == f.evidence
    assert history(f.processor) == f.attempts


@pytest.mark.parametrize("point", ["audit", "restored_publication_after_update", "restored_publication_after_commit"])
def test_prepared_audit_and_transaction_crash_restart(failed_publication, monkeypatch, quiet_logger, point):
    from tcfcomic import processor as module
    f = failed_publication
    p = f.processor
    shutil.copyfile(f.saved, f.output)
    with monkeypatch.context() as patcher:
        if point == "audit":
            original = module.preserve_published_restoration_audits

            def crash(*args):
                original(*args)
                raise RuntimeError("synthetic crash after prepared audit")

            patcher.setattr(module, "preserve_published_restoration_audits", crash)
        else:
            def crash(actual):
                if actual == point:
                    raise RuntimeError("synthetic transaction crash")
            patcher.setattr(p.state, "_transition_hook", crash)
        with pytest.raises(RuntimeError, match="synthetic"):
            p._reconcile_restored_output(f.job)
    assert p.state.get_job(f.job.job_id).status == (
        JobStatus.SUCCEEDED if point.endswith("after_commit") else JobStatus.FAILED
    )
    artifacts = {path.name: path.read_bytes() for path in f.config.paths.quarantine.glob("*.json")}
    assert len(artifacts) == 3
    p.close()
    with Processor(f.config, quiet_logger, runner=InProcessAttemptRunner(f.provider)) as restarted:
        assert restarted.state.get_job(f.job.job_id).status == JobStatus.SUCCEEDED
        assert history(restarted) == f.attempts
    assert artifacts == {path.name: path.read_bytes() for path in f.config.paths.quarantine.glob("*.json")}
    assert len(f.provider.requests) == 1


def test_marker_fingerprints_exact_evidence(failed_publication):
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    attempts = f.processor.state.completed_publication_attempts(f.job.job_id)
    assert f.processor._reconcile_restored_output(f.job)
    marker, = f.config.paths.quarantine.glob(f"{f.job.job_id}.output-restoration.*.json")
    payload = json.loads(marker.read_bytes())

    def fingerprint(value):
        return hashlib.sha256((json.dumps(
            value, ensure_ascii=True, sort_keys=True, separators=(",", ":"),
        ) + "\n").encode("utf-8")).hexdigest()

    assert payload["job_id"] == f.job.job_id
    assert payload["output_sha256"] == f.job.output_sha256
    assert payload["original_provenance_sha256"] == hashlib.sha256(f.evidence).hexdigest()
    assert payload["attempt_history_sha256"] == fingerprint([asdict(item) for item in attempts])
    assert payload["job_snapshot_sha256"] == fingerprint(asdict(f.job))
    assert marker.name.endswith(hashlib.sha256(marker.read_bytes()).hexdigest() + ".json")
    assert f.failure.read_bytes() == f.evidence


def test_known_provider_failure_with_output_refs_stays_terminal(tmp_path, quiet_logger):
    _, config = write_config(tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com")
    config = replace(config, retry=replace(config.retry, azure_response_retries=True))
    source = write_image(config.paths.source / "synthetic.png")
    provider = FakeProvider(["permanent"])
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as p:
        failed = p.process_path(source)
        assert failed.status == JobStatus.FAILED
        output = write_image(config.paths.destination / "unproven.png")
        p.state.connection.execute(
            "UPDATE jobs SET output_name=?, output_sha256=? WHERE job_id=?",
            (output.name, hashlib.sha256(output.read_bytes()).hexdigest(), failed.job_id),
        )
        before, attempts = p.state.get_job(failed.job_id), history(p)
        result = p.process_path(source, retry_failed_variants=True)
        assert result.job_id == failed.job_id and result.status == JobStatus.FAILED
        assert p.state.get_job(failed.job_id) == before
        assert history(p) == attempts
    assert len(provider.requests) == 1


def test_startup_only_offline_entrypoint_no_auth_source_backup_or_dispatch(
    failed_publication, quiet_logger, monkeypatch,
):
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    f.processor.close()

    class NeverDispatch:
        def run(self, *args, **kwargs):
            pytest.fail("local restoration dispatched a provider")

        run_authenticated = run

    class NoSignIn:
        started = True

        def get_token(self, *args, **kwargs):
            pytest.fail("local restoration requested authentication")

    config = replace(f.config, provider=replace(f.config.provider, authentication="interactive"))
    original_open = Path.open
    counts = []
    original_reconcile = Processor._reconcile_restored_output

    def confined_open(path, *args, **kwargs):
        assert path != f.source and f.saved.parent not in path.parents
        return original_open(path, *args, **kwargs)

    def count(processor, job):
        counts.append(job.job_id)
        return original_reconcile(processor, job)

    monkeypatch.setattr(Path, "open", confined_open)
    monkeypatch.setattr(Processor, "_reconcile_restored_output", count)
    with Processor(config, quiet_logger, runner=NeverDispatch(), auth_session=NoSignIn()) as p:
        assert p.state.get_job(f.job.job_id).status == JobStatus.SUCCEEDED
        assert history(p) == f.attempts
    assert counts == [f.job.job_id]
    assert len(f.provider.requests) == 1


def test_actionable_missing_and_restored_messages(tmp_path, quiet_logger):
    records = []

    class Capture(logging.Handler):
        def emit(self, record):
            records.append(record)

    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(Capture())
    config, source, provider, job, attempts, saved = published(tmp_path, quiet_logger)
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as p:
        failure = config.paths.quarantine / f"{job.job_id}.json"
        assert json.loads(failure.read_bytes())["safe_message"] == PUBLISHED_OUTPUT_MISSING
        assert any(getattr(record, "safe_message", "") == PUBLISHED_OUTPUT_MISSING for record in records)
        shutil.copyfile(saved, config.paths.destination / job.output_name)
        result = p.process_path(source, variant=job.variant)
        assert result.status == JobStatus.SUCCEEDED and history(p) == attempts
        assert any(getattr(record, "safe_message", "") ==
                   "Restored output verified; marked successful without a provider request." for record in records)
    assert len(provider.requests) == 1


@pytest.mark.parametrize("ownership", ["output", "stage"])
def test_cross_job_ownership_denies_restoration(failed_publication, ownership):
    f = failed_publication
    p = f.processor
    shutil.copyfile(f.saved, f.output)
    other_source = write_image(f.config.paths.source / "other.png", color=(1, 7, 9))
    other = p.process_path(other_source, variant=f.job.variant)
    other_job = p.state.get_job(other.job_id)
    if ownership == "output":
        p.state.connection.execute(
            "UPDATE jobs SET output_name=? WHERE job_id=?", (f.job.output_name.upper(), other.job_id),
        )
    else:
        p.state.connection.execute(
            "UPDATE jobs SET staged_path=? WHERE job_id=?", (other_job.staged_path, f.job.job_id),
        )
    before = p.state.list_jobs(), history(p)
    assert not p._reconcile_restored_output(p.state.get_job(f.job.job_id))
    assert (p.state.list_jobs(), history(p)) == before
    assert f.output.read_bytes() == f.saved.read_bytes()
    assert f.failure.read_bytes() == f.evidence
    assert len(f.provider.requests) == 2


def test_output_replacement_during_png_validation_is_denied(failed_publication, monkeypatch):
    from tcfcomic import processor as module
    f = failed_publication
    shutil.copyfile(f.saved, f.output)
    replacement = write_image(f.saved.parent / "replacement.png", color=(1, 2, 9))
    original = module.validate_output_png

    def replace_after_validation(path, limits):
        result = original(path, limits)
        os.replace(replacement, path)
        return result

    monkeypatch.setattr(module, "validate_output_png", replace_after_validation)
    assert not f.processor._reconcile_restored_output(f.job)
    assert f.processor.state.get_job(f.job.job_id) == f.job
    assert history(f.processor) == f.attempts
    assert f.failure.read_bytes() == f.evidence
    assert len(f.provider.requests) == 1


@pytest.mark.parametrize("moderation", [False, True])
def test_provider_refusal_explanation_does_not_authorize_replay(tmp_path, quiet_logger, moderation):
    _, config = write_config(tmp_path, provider="azure_openai", endpoint="https://offline.openai.azure.com")
    config = replace(config, retry=replace(config.retry, azure_response_retries=True))
    source = write_image(config.paths.source / "synthetic.png")
    message = "The provider rejected the request. HTTP 400."
    if moderation:
        message += " Provider code moderation_blocked. The provider blocked the input or generated output under its content policy."
    records = []

    class Capture(logging.Handler):
        def emit(self, record):
            records.append(getattr(record, "safe_message", ""))

    class Refusal(FakeProvider):
        def transform(self, request, output):
            self.requests.append(request)
            raise PermanentProviderError(ErrorCode.PROVIDER_PERMANENT, message)

    quiet_logger.setLevel(logging.INFO)
    quiet_logger.addHandler(Capture())
    provider = Refusal()
    with Processor(config, quiet_logger, runner=InProcessAttemptRunner(provider)) as p:
        result = p.process_path(source)
        before = p.state.get_job(result.job_id), history(p)
        failure = config.paths.quarantine / f"{result.job_id}.json"
        evidence = failure.read_bytes()
        with pytest.raises(AppError):
            p.process_path(source, retry_failed_variants=True)
        assert (p.state.get_job(result.job_id), history(p)) == before
        assert failure.read_bytes() == evidence
    expected = "Saved diagnostic records a content-policy rejection" if moderation else "Historical response evidence cannot authorize retry"
    assert any(text.startswith(expected) for text in records)
    assert len(provider.requests) == 1
