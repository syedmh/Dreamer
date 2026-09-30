# RW1-RW4 final developer self-verification

Date: 2026-09-04

Status: PASS. This is developer self-verification only; independent RW5-RW8
gates remain out of scope.

## Rework matrices

- RW1: `RW1_SELF_TEST_PASS tests=13`
- RW2: `RW2_SELF_TEST_PASS tests=16`
- RW3: `RW3_SELF_TEST_PASS tests=10`
- RW4: `RW4_SELF_TEST_PASS tests=12`
- Total: 51 passed, 0 failed, 0 blocked, 0 skipped.

RW4 ran with ambient `PIP_*` variables removed and
`PIP_CONFIG_FILE=nul`. The download command used only
`https://packagefeedproxy.microsoft.io/pypi/simple/`, `--require-hashes`,
`--only-binary=:all:`, CPython 3.13, and Windows x86-64 targeting. The exact
wheel set was:

| Artifact | Bytes | SHA-256 |
|---|---:|---|
| `numpy-2.5.2-cp313-cp313-win_amd64.whl` | 12460532 | `85AACCB24182C25DF891AD0EC333585967E115269D5F1B17F2C9AE005BC96657` |
| `opencv_python-5.0.0.93-cp37-abi3-win_amd64.whl` | 44000345 | `F90BA04B8F73BC5C3814037699739F0156F597338A98F05956C684E7C3CA10D2` |
| `pillow-12.3.0-cp313-cp313-win_amd64.whl` | 7239691 | `1CCA606CD25738DF4ED873D5AD46BBDB3D83B5CBCA291F6B4FF13A4DF6B0BBE8` |

Offline `pip install --dry-run` and mission-local installation both used
`--no-index --find-links --require-hashes --only-binary=:all:`. Imports
reported NumPy 2.5.2, OpenCV 5.0.0, and Pillow 12.3.0. A mission-local lock
with a tampered NumPy hash exited 1 and wrote no artifact.

## Final verification

- Debug build: exit 0, 0 warnings, 0 errors.
- Release build: exit 0, 0 warnings, 0 errors.
- ControllerProbe: 466 `PASS` lines and
  `CONTROLLER_PROBE_PASS assertions=466 ... global_input=true ...`.
- Python compile/import: seven files compiled in memory; six release modules
  imported; lock validator emitted
  `requirements_lock_ok=index=microsoft_proxy ...`.
- Official extractor determinism: two fresh-process passes, six source hashes
  unchanged, 33 encoded and decoded frame hashes unchanged, zero non-frame
  output.
- Read-only validator:
  `ASSET_RELEASE_CHECK_PASS frames=33 sources=6 read_only=true
  export_resources=34 artifact_manifest=checked_if_present`.
- `dotnet format ... --verify-no-changes --no-restore`: exit 0.
- Scoped `git diff --check`: exit 0. Git emitted only its existing line-ending
  advisory for `extract_directional_turns.py`.
- Godot 4.5.1 Mono headless development import and startup: exit 0. The import
  recreated the intentionally absent `GlobalInputPolicy.cs.uid`; because it
  was confirmed absent before the test, the generated sidecar was removed
  afterward.
- Arbitrary-cwd batch fixture: child exit 37 propagated as launcher exit 37
  after an authenticated `GODOT_SELECTED`.
- Arbitrary-cwd isolated export: seed files 50, pre-import files 51, generated
  solution 994 bytes with SHA-256
  `FE3E86D84D18FD948E059483F5CB09B956E4F7B91A0B33FACD337727A2F4EC79`;
  import/export exits 0 with zero `ERROR:` diagnostics.
- Final executable:
  `Build/TCFAnimation.exe`, 100203544 bytes, SHA-256
  `A5DCE8BCCC3DA52412E521568B38A6B4C08AE737975F1A91C5BA970894147DA1`.
- Final managed assembly:
  `Build/data_TCFAnimation_windows_x86_64/TCFAnimation.dll`, 71168 bytes,
  SHA-256
  `6E861818F683ACB5C8E2B40A3E4EEC72183D36E27A03032359D5BEE0A25999E5`.
- Exported runtime:
  `RUNTIME_SMOKE_PASS frames=33 dialogue_ui=true`, exit 0.
- Package hygiene: 73 entries, one runtime scene, two one-byte script
  placeholders, 33 runtime textures, 33 import metadata entries, four engine
  metadata entries, zero denied tokens, no source content, no PDB, no absolute
  project path.
- Preservation: 74/74 protected files unchanged; no missing/changed protected
  files; no remaining `.release-stage-*`; no generated
  `GlobalInputPolicy.cs.uid`; no `FrameExtraction/__pycache__`.
