# Photo Preview Windows App

## Scope

- Build a C# Windows desktop application in `TCFPreview`.
- Let the user choose a source folder containing photos.
- Preview the current supported photo.
- Navigate to the previous or next photo.
- Let the user choose a destination folder.
- Copy the current photo to the destination.

## Acceptance criteria

1. The app supports common formats available through built-in Windows imaging: `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif`, `.tif`, and `.tiff`.
2. Photo discovery is non-recursive, case-insensitive, and ordered by file name.
3. Selecting a source folder loads the first supported photo and shows its name and `1 of N`.
4. Previous and Next are disabled when no corresponding photo exists.
5. Preview loading does not keep the source file locked.
6. Copy requires a valid destination folder and preserves the original file name.
7. Name collisions produce a new non-overwriting name such as `photo (1).jpg`.
8. Empty folders, unreadable images, deleted files, and copy failures produce visible error or status messages.
9. Core discovery, navigation, and copy behavior is separated from the UI and covered by automated tests.
10. Release build, tests, independent review, desktop QA, and final judgment pass.

## Out of scope

- Editing, deleting, rotating, or recursively scanning photos.
- Cloud synchronization or database storage.
- Commit, push, packaging installer, or production deployment.
