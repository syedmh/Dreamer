from pathlib import Path


def test_readme_discloses_provider_data_flow_and_verified_python_scope() -> None:
    project = Path(__file__).parents[2]
    readme = (project / "README.md").read_text(encoding="utf-8")
    normalized = " ".join(readme.split())

    assert "sends the immutable staged image bytes" in normalized
    assert "configured transformation prompt" in normalized
    assert "image bytes and prompts remain local" in normalized
    assert "executed on Python 3.13.15" in normalized
    assert "Python 3.11 was not available" in normalized
    assert "No application tests were executed under Python 3.11" in normalized
    assert 'python -m pip install -c constraints.txt -e ".[dev]"' in readme
    assert "non-hashed" in readme
    assert "Input byte, dimension, pixel, and decompression-limit failures" in normalized
    assert "input failures and return exit code 4" in normalized
    assert "Provider/output byte, dimension, pixel, and decompression-limit failures" in normalized
    assert "return exit code 5" in normalized
    assert "Staged input integrity and configured limits are checked after staging during admission" in normalized
    assert "after an atomic queue claim, immediately before each provider attempt" in normalized
    assert "streams and compares its size and SHA-256 with durable identity" in normalized
    assert "non-following file metadata (excluding access time) to remain unchanged" in normalized
    assert "provider subsequently reopens the staged path" in normalized
    assert "accepted local path-reopen TOCTOU residual between verification and the provider read" in normalized
    assert "it is not eliminated" in normalized
    assert "By default, only proven pre-dispatch connect, connect-timeout, and pool-timeout failures" in normalized
    assert "plus completely classified eligible HTTP 409 and 429 responses" in normalized
    assert "Terminal/unknown classifications always veto." in normalized
    assert "By default, eligible HTTP 408/5xx responses" in normalized
    assert "are marked ambiguous and are not automatically replayed" in normalized
    assert "`azure_response_retries` is an optional boolean, default `false`" in normalized
    assert "never unanswered transport failures" in normalized
    assert "process/watch flag relaxes request-identity equality, not the source" in normalized
    assert "or interactive authentication failure" in normalized
    assert "canonical HTTP diagnostic grammar with `Classification evidence v1 complete.`" in normalized
    assert "Only the provider adapter writes this suffix" in normalized
    assert "Every historical attempt must independently qualify" in normalized
    assert "cannot erase earlier moderation, uncertainty, or unversioned evidence" in normalized
    assert "*.egg-info/" in (project / ".gitignore").read_text(encoding="utf-8")

    constraints = (project / "constraints.txt").read_text(encoding="utf-8")
    entries = [
        line for line in constraints.splitlines() if line and not line.startswith("#")
    ]
    assert entries
    assert all("==" in line for line in entries)
    assert "--hash" not in constraints


def test_readme_scopes_fallback_source_recheck_per_mode() -> None:
    readme = (Path(__file__).parents[2] / "README.md").read_text(encoding="utf-8")
    normalized = " ".join(readme.split())

    assert "In both `process` and `watch`, the source version is" in normalized
    assert "Before a fallback is dispatched the current file is re-verified" not in normalized
    assert "In `process`, the current file is re-verified" in normalized
    assert "(`fallback_skipped_source_changed`)" in normalized
    assert "In `watch`, a fallback that is waiting for its primary" in normalized
    assert "`watch` does not re-hash the file for this check" in normalized
    assert "identical size and modification time during the pending window is not detected" in normalized
    assert "also carries an access code" in normalized
