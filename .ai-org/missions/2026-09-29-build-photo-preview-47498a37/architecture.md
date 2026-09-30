# Architecture

## Decision

Build a dependency-light WinForms application targeting `net10.0-windows`, backed by a framework-independent `net10.0` core library and an MSTest project.

The app supports JPEG, PNG, BMP, GIF, and TIFF through built-in Windows imaging. WebP is intentionally excluded because guaranteed decoding would add a third-party licensing decision or depend on an optional operating-system codec that may not be installed.

## Components

- `TCFPreview.Core`
  - Discovers supported files non-recursively in deterministic file-name order.
  - Owns navigation state and boundary behavior.
  - Copies with `FileMode.CreateNew` and numbered collision names, never overwriting.
- `TCFPreview.WinForms`
  - Owns folder pickers, preview rendering, status text, and button state.
  - Loads images into detached bitmaps so source files are not locked.
  - Disposes replaced previews.
- `TCFPreview.Tests`
  - Covers discovery, navigation, collision-safe copy, errors, and source-file unlock behavior.

## Core contracts

```csharp
IReadOnlyList<string> PhotoDiscovery.Discover(string sourceDirectory);

PhotoNavigator(IReadOnlyList<string> photos);
string? CurrentPath;
int CurrentIndex;
int Count;
bool CanMovePrevious;
bool CanMoveNext;
bool TryMovePrevious(out string? path);
bool TryMoveNext(out string? path);

Task<CopyResult> PhotoCopier.CopyAsync(
    string sourcePath,
    string destinationDirectory,
    CancellationToken cancellationToken = default);
```

## Failure behavior

- Folder-picker cancellation preserves current state.
- Invalid or inaccessible source folders preserve the prior catalog and show an error.
- Empty folders clear the preview and disable navigation/copy.
- Corrupt or deleted images retain navigation context, clear the preview, and show an explicit status.
- Invalid destination folders disable copy or produce a visible corrective error.
- Copy collisions create `name (1).ext`, `name (2).ext`, and so on.
- Permission, disk, path, and other I/O failures never produce success-shaped results.

## UI

- Read-only source and destination path fields with Browse buttons.
- Central zoomed `PictureBox`.
- Filename, position (`1 of N`), and status labels.
- Previous, Next, and Copy buttons with state derived from the current catalog and destination.

## Validation

```powershell
dotnet restore .\TCFPreview.slnx
dotnet build .\TCFPreview.slnx -c Release --no-restore
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build
dotnet run --project .\src\TCFPreview.WinForms\TCFPreview.WinForms.csproj -c Release
```
