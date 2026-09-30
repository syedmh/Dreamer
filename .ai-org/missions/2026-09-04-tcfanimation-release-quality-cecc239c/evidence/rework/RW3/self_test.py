"""RW3 developer self-evidence for validator and source-integrity hardening."""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from unittest.mock import patch


PROJECT = Path(r"C:\Users\syedhu\source\repos\Dreamer\TCFAnimation")
MISSION = Path(
    r"C:\Users\syedhu\source\repos\Dreamer\.ai-org\missions"
    r"\2026-09-04-tcfanimation-release-quality-cecc239c"
)
EVIDENCE = Path(__file__).resolve().parent
TEMPORARY = EVIDENCE / "temporary"
FRAME_EXTRACTION = PROJECT / "FrameExtraction"
sys.path.insert(0, str(FRAME_EXTRACTION))
sys.path.insert(0, str(PROJECT / ".tools" / "python"))

from PIL import Image  # type: ignore  # noqa: E402

import validate_release  # noqa: E402
from file_integrity import sha256_file  # noqa: E402


SOURCE_CONTRACTS = {
    "LTurning.png": (
        1502641,
        "9EDD38F303B17CD043EDCCABF2E6C2BC50F182B9A2B918B4BDECF1B2861E3A91",
    ),
    "RTurning.png": (
        1390487,
        "2D20B97B4BC630DBFF9D6DD932F3314A9BC6FE0587013E6C12E72BF1D40D5840",
    ),
    "RWalking2.png": (
        495754,
        "CD56287A4830D068292793256DBEB5A29E1EB9D888520A5339FE3957E7B7FA3A",
    ),
    "Clapping2.png": (
        654450,
        "FBB46FBEAD0D5815F4E23307240535C600C29D0DF650B493137D05E762319C00",
    ),
    "CrossArm3.png": (
        628721,
        "276413B76F13D4940FD8B746D3AEA13D27922A47EACD750DCCC6FF622A6A8192",
    ),
    "CrossArm4.png": (
        632179,
        "475614A7B2DB0B7469FA88E9B7B5F5C8548F8095B56DAD99F6270098E9174A3F",
    ),
}
EXTRACTORS = (
    "extract_directional_turns.py",
    "extract_right_walk.py",
    "extract_clap.py",
    "extract_cross_arm.py",
)
results: list[dict[str, object]] = []


def add_result(name: str, passed: bool, detail: str) -> None:
    results.append({"name": name, "passed": passed, "detail": detail})
    print(f"RW3_TEST name={name} passed={passed} detail={detail}")
    if not passed:
        raise RuntimeError(f"RW3 self-test failed: {name} - {detail}")


def sha256_bytes(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(1 << 20):
            digest.update(block)
    return digest.hexdigest().upper()


def frame_snapshot() -> dict[str, tuple[str, str, str, tuple[int, int]]]:
    records = {}
    for relative in validate_release.EXPECTED_FRAME_PATHS:
        path = PROJECT / relative
        with Image.open(path) as image:
            decoded = hashlib.sha256(image.tobytes()).hexdigest().upper()
            records[relative] = (
                sha256_bytes(path),
                decoded,
                image.mode,
                image.size,
            )
    return records


def source_snapshot() -> dict[str, str]:
    return {
        name: sha256_file(PROJECT / name, size)
        for name, (size, _) in SOURCE_CONTRACTS.items()
    }


def included_project_files() -> list[Path]:
    excluded_parts = {
        ".ai-org",
        ".tools",
        ".vs",
        "__pycache__",
        "bin",
        "obj",
    }
    return sorted(
        path
        for path in PROJECT.rglob("*")
        if path.is_file()
        and not any(part in excluded_parts for part in path.parts)
    )


def project_snapshot(
    *,
    exclude_runtime_frames: bool = False,
) -> dict[str, tuple[int, str]]:
    frame_paths = set(validate_release.EXPECTED_FRAME_PATHS)
    records = {}
    for path in included_project_files():
        relative = path.relative_to(PROJECT).as_posix()
        if exclude_runtime_frames and relative in frame_paths:
            continue
        records[relative] = (path.stat().st_size, sha256_bytes(path))
    return records


def run_python(script: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [sys.executable, "-B", str(script)],
        cwd=Path(r"C:\Windows"),
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
    )


def expect_runtime_error(action, fragment: str) -> str:
    try:
        action()
    except RuntimeError as exception:
        message = str(exception)
        if fragment.casefold() not in message.casefold():
            raise RuntimeError(
                f"Expected error containing {fragment!r}; got {message!r}."
            ) from exception
        return message
    raise RuntimeError(f"Expected RuntimeError containing {fragment!r}.")


def compare_protected_baseline() -> dict[str, object]:
    baseline_path = (
        MISSION / "evidence" / "rework" / "baseline" / "protected-files.json"
    )
    baseline = json.loads(baseline_path.read_text(encoding="utf-8"))
    missing = []
    changed = []
    for record in baseline["files"]:
        path = PROJECT / record["path"]
        if not path.is_file():
            missing.append(record["path"])
            continue
        if (
            path.stat().st_size != record["length"]
            or sha256_bytes(path) != record["sha256"]
        ):
            changed.append(record["path"])
    return {
        "status": "PASS" if not missing and not changed else "FAIL",
        "checked": len(baseline["files"]),
        "missing": missing,
        "changed": changed,
    }


def main() -> int:
    if TEMPORARY.exists():
        shutil.rmtree(TEMPORARY)
    TEMPORARY.mkdir(parents=True)
    try:
        preset = (PROJECT / "export_presets.cfg").read_text(encoding="utf-8")
        filter_controls = {
            "nonempty_include": preset.replace(
                'include_filter=""', 'include_filter="*.png"', 1
            ),
            "nonempty_exclude": preset.replace(
                'exclude_filter=""', 'exclude_filter="*.cs"', 1
            ),
            "duplicate_include": preset.replace(
                'include_filter=""',
                'include_filter=""\ninclude_filter=""',
                1,
            ),
            "duplicate_exclude": preset.replace(
                'exclude_filter=""',
                'exclude_filter=""\nexclude_filter=""',
                1,
            ),
        }
        rejected_filters = 0
        validate_release.validate_empty_export_filters(preset)
        for control in filter_controls.values():
            expect_runtime_error(
                lambda control=control: (
                    validate_release.validate_empty_export_filters(control)
                ),
                "filter",
            )
            rejected_filters += 1
        add_result(
            "exact-empty-filters",
            rejected_filters == 4,
            "positive=pass negative_controls=4",
        )

        metadata_controls = (
            b"res://Waving.png",
            (
                r"C:\repo\TCFAnimation\FrameExtraction\validate_release.py"
            ).encode("utf-16-le"),
        )
        denied_counts = tuple(
            len(validate_release.denied_metadata_tokens(payload))
            for payload in metadata_controls
        )
        add_result(
            "metadata-token-controls",
            all(count > 0 for count in denied_counts),
            f"ascii_matches={denied_counts[0]} utf16_matches={denied_counts[1]}",
        )

        missing_path = TEMPORARY / "must-not-open.bin"
        oversize = validate_release.PackEntry(
            100,
            validate_release.MAXIMUM_METADATA_ENTRY_SIZE + 1,
        )
        expect_runtime_error(
            lambda: validate_release.read_pack_entry(
                missing_path,
                100,
                20 * 1024 * 1024,
                oversize,
            ),
            "maximum",
        )
        out_of_pack = validate_release.PackEntry(199, 2)
        expect_runtime_error(
            lambda: validate_release.read_pack_entry(
                missing_path,
                100,
                100,
                out_of_pack,
            ),
            "outside",
        )
        aggregate = {
            f"entry_{index}": validate_release.PackEntry(
                0,
                4 * 1024 * 1024,
            )
            for index in range(4)
        }
        aggregate["entry_4"] = validate_release.PackEntry(0, 1)
        expect_runtime_error(
            lambda: validate_release.validate_metadata_descriptors(
                aggregate,
                set(aggregate),
            ),
            "aggregate",
        )
        add_result(
            "bounded-pack-controls",
            not missing_path.exists(),
            "entry_4mib_plus_1=preopen aggregate_16mib_plus_1=preopen "
            "out_of_pack=preopen",
        )

        sparse = TEMPORARY / "oversized-source.bin"
        expected_size = 1024
        with sparse.open("wb") as stream:
            stream.seek(expected_size)
            stream.write(b"\0")
        with patch.object(
            Path,
            "open",
            side_effect=AssertionError("decode/open sentinel invoked"),
        ) as open_sentinel:
            expect_runtime_error(
                lambda: sha256_file(sparse, expected_size),
                "byte size",
            )
        add_result(
            "oversized-source-precheck",
            open_sentinel.call_count == 0,
            "wrong_size=rejected path_open_calls=0 decode_sentinel=false",
        )

        source_hashes_before = source_snapshot()
        expected_hashes = {
            name: expected_hash
            for name, (_, expected_hash) in SOURCE_CONTRACTS.items()
        }
        add_result(
            "six-source-streaming",
            source_hashes_before == expected_hashes,
            "sources=6 chunk_size=1048576 predecode=true",
        )

        implementation_files = (
            *EXTRACTORS,
            "validate_release.py",
        )
        implementation_text = {
            name: (FRAME_EXTRACTION / name).read_text(encoding="utf-8")
            for name in implementation_files
        }
        extractor_calls = all(
            "sha256_file(" in implementation_text[name]
            for name in EXTRACTORS
        )
        add_result(
            "integrity-wiring",
            extractor_calls
            and "sha256_file(path, expected_bytes)"
            in implementation_text["validate_release.py"],
            "extractors=4 validator=1 streaming_helper=true",
        )

        frames_before = frame_snapshot()
        nonframes_before = project_snapshot(exclude_runtime_frames=True)
        pass_summaries = []
        for pass_number in (1, 2):
            for extractor in EXTRACTORS:
                completed = run_python(FRAME_EXTRACTION / extractor)
                if completed.returncode != 0:
                    raise RuntimeError(
                        f"{extractor} pass {pass_number} failed with "
                        f"{completed.returncode}:\n{completed.stdout}\n"
                        f"{completed.stderr}"
                    )
            source_after = source_snapshot()
            frames_after = frame_snapshot()
            nonframes_after = project_snapshot(exclude_runtime_frames=True)
            if source_after != source_hashes_before:
                raise RuntimeError(
                    f"Source hashes changed during extraction pass {pass_number}."
                )
            if frames_after != frames_before:
                raise RuntimeError(
                    f"Frame bytes/pixels changed during pass {pass_number}."
                )
            if nonframes_after != nonframes_before:
                raise RuntimeError(
                    f"Non-frame project output changed during pass {pass_number}."
                )
            pass_summaries.append(
                {
                    "pass": pass_number,
                    "sources": len(source_after),
                    "frames": len(frames_after),
                    "nonframes": len(nonframes_after),
                }
            )
        add_result(
            "two-pass-determinism",
            len(pass_summaries) == 2,
            "passes=2 sources=6 frames=33 encoded_and_decoded=unchanged "
            "nonframe_output=0",
        )

        artifact = PROJECT / "Build" / "TCFAnimation.exe"
        entries = validate_release.read_pack_manifest(artifact)
        validate_release.validate_pack_payload(artifact, entries)
        add_result(
            "isolated-package",
            len(entries) == 73,
            "entries=73 engine_metadata=4 import_metadata=33 "
            "denied_tokens=0 scripts=2x1byte",
        )

        validator_before = project_snapshot()
        completed = run_python(FRAME_EXTRACTION / "validate_release.py")
        validator_after = project_snapshot()
        marker = (
            "ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true "
            "export_resources=34 artifact_manifest=checked_if_present"
        )
        add_result(
            "validator-read-only",
            completed.returncode == 0
            and marker in completed.stdout
            and validator_before == validator_after,
            f"exit={completed.returncode} marker={marker in completed.stdout} "
            f"files={len(validator_before)} unchanged="
            f"{validator_before == validator_after}",
        )

        protected = compare_protected_baseline()
        (EVIDENCE / "protected-comparison.json").write_text(
            json.dumps(protected, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        add_result(
            "protected-baseline",
            protected["status"] == "PASS",
            f"checked={protected['checked']} missing={len(protected['missing'])} "
            f"changed={len(protected['changed'])}",
        )

        summary = {
            "status": "PASS",
            "tests": len(results),
            "extractionPasses": pass_summaries,
            "results": results,
        }
        (EVIDENCE / "self-test-results.json").write_text(
            json.dumps(summary, indent=2) + "\n",
            encoding="utf-8",
            newline="\n",
        )
        print(f"RW3_SELF_TEST_PASS tests={len(results)}")
        return 0
    finally:
        shutil.rmtree(TEMPORARY, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
