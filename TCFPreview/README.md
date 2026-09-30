# TCF Photo Preview

A dependency-light Windows Forms application for browsing supported photos in one folder and copying the current photo to another folder without overwriting existing files.

## Requirements

- Windows
- .NET SDK 10.0.401 or later in the .NET 10 SDK line

## Supported files

The source folder is scanned non-recursively for `.jpg`, `.jpeg`, `.png`, `.bmp`, `.gif`, `.tif`, and `.tiff` files. Matching is case-insensitive and results are ordered by file name.

## Run the application

```powershell
dotnet run --project .\src\TCFPreview.WinForms\TCFPreview.WinForms.csproj -c Release
```

1. Select **Browse** next to Source and choose a folder containing photos.
2. Use **Previous** and **Next** to navigate the ordered photo list.
3. Select **Browse** next to Destination and choose a valid destination folder.
4. Select **Copy** to copy the current photo.

The original filename is preserved when available. If it already exists, the application creates a numbered name such as `photo (1).jpg`; existing files are never overwritten.

## Build and test

```powershell
dotnet restore .\TCFPreview.slnx
dotnet build .\TCFPreview.slnx -c Release --no-restore
dotnet test .\tests\TCFPreview.Tests\TCFPreview.Tests.csproj -c Release --no-build
```
