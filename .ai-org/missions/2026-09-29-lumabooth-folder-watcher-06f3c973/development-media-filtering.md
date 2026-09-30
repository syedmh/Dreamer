# Development evidence: JPG/JPEG/PNG-only admission

Date: 2026-09-29

## Outcome

New discovery, live-change tracking, direct snapshots, and upload admission accept only `.jpg`,
`.jpeg`, and `.png`, case-insensitively. Unsupported regular files are ignored without discovery
warnings, spool files, state records, or HTTP requests. Existing persisted resumable work remains
valid and is still scheduled on restart.

## Filtering boundaries

- `SupportedMedia` is the shared explicit allowlist.
- `Reconciler` filters regular files while continuing directory traversal.
- `FileChangeFeed` filters created/changed upserts and translates renames as delete-old/add-new
  according to support on each side.
- `WatcherCoordinator` removes deleted or unsupported injected paths from candidate tracking.
- `FileSnapshotter` returns `RetryLater("unsupported_media_type")` before spool reservation or file
  creation.
- `ContentTypeMap.Get` exposes only JPEG and PNG MIME types; legacy stored MIME validation remains
  compatible with records produced by earlier versions.

## Evidence

- Focused filtering suite:
  `dotnet test .\TCFUploader.slnx --configuration Release --filter "FullyQualifiedName~Discovery|FullyQualifiedName~FileSnapshotterTests|FullyQualifiedName~ContentTypeMapTests|FullyQualifiedName~EndToEndContractTests"`
  - 45 passed, 0 failed, 0 skipped.
- Coordinator/state/restart suite:
  `dotnet test .\TCFUploader.slnx --configuration Release --no-restore --filter "FullyQualifiedName~WatcherCoordinatorTests|FullyQualifiedName~StateRepositoryTests|FullyQualifiedName~RestartRecoveryTests"`
  - 39 passed, 0 failed, 0 skipped.
- Full Release:
  `dotnet restore .\TCFUploader.slnx`
  - All projects up to date.
  `dotnet build .\TCFUploader.slnx --configuration Release --no-restore`
  - Build succeeded, 0 warnings, 0 errors.
  `dotnet test .\TCFUploader.slnx --configuration Release --no-build --no-restore`
  - 136 passed, 0 failed, 0 skipped.
- Publish/package:
  `dotnet publish .\src\TCFUploader\TCFUploader.csproj --configuration Release --runtime win-x64 --self-contained false --no-restore`
  - Five files published.
  - ZIP extraction contained the same five files with matching SHA-256 hashes.
  - Published `TCFUploader.exe --help` returned the expected usage text.
  - Artifacts: `TCFUploader\artifacts\media-filtering-20260929`.

No production endpoint, credential, or browser flow was used. Non-development gates remain
`paused_by_cto`.

## Non-blocking existing condition

`dotnet format --verify-no-changes` reports pre-existing whitespace-only formatting findings in
untouched regions of `WatcherCoordinator.cs`, `StateRepository.cs`, and
`StateRepositoryTests.cs`. The required Release build and all 136 tests pass.
