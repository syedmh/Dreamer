using System.Drawing;
using TCFPreview.Core;

namespace TCFPreview.WinForms;

public sealed record MainFormViewState(
    string FileName,
    string Position,
    string Status,
    bool CanMovePrevious,
    bool CanMoveNext,
    bool CanCopy,
    bool CanBrowse);

public sealed record PreviewPresentationResult(
    Image? Image,
    string Status,
    bool IsAvailable);

public static class MainFormPresentation
{
    public const string InitialStatus = "Choose a source folder to begin.";
    public const string PreviewReadyStatus = "Photo ready.";
    public const string NoCurrentPhotoCopyStatus = "There is no current photo to copy.";
    public const string InvalidDestinationCopyStatus =
        "Choose a valid destination folder before copying.";
    public const string CopyingStatus = "Copying photo...";

    public static MainFormViewState CreateViewState(
        PhotoNavigator navigator,
        bool destinationIsValid,
        bool copyInProgress,
        string status)
    {
        ArgumentNullException.ThrowIfNull(navigator);
        ArgumentNullException.ThrowIfNull(status);

        string? currentPath = navigator.CurrentPath;
        return new MainFormViewState(
            currentPath is null ? "No photo selected" : Path.GetFileName(currentPath),
            currentPath is null
                ? $"0 of {navigator.Count}"
                : $"{navigator.CurrentIndex + 1} of {navigator.Count}",
            status,
            !copyInProgress && navigator.CanMovePrevious,
            !copyInProgress && navigator.CanMoveNext,
            !copyInProgress && currentPath is not null && destinationIsValid,
            !copyInProgress);
    }

    public static string SourceLoadedStatus(int photoCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(photoCount);

        return photoCount == 0
            ? "No supported photos were found in this folder."
            : $"Loaded {photoCount} photo{(photoCount == 1 ? string.Empty : "s")}.";
    }

    public static string SourceFolderErrorStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return $"Unable to load source folder: {message}";
    }

    public static string DestinationStatus(bool destinationIsValid)
    {
        return destinationIsValid
            ? "Destination folder selected."
            : "The selected destination folder is not available.";
    }

    public static string PreviewUnavailableStatus(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"Preview unavailable: {exception.Message}";
    }

    public static string CopySucceededStatus(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        return $"Copied as {Path.GetFileName(destinationPath)}.";
    }

    public static string CopyFailedStatus(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return $"Copy failed: {exception.Message}";
    }

    public static PreviewPresentationResult LoadPreview(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            return new PreviewPresentationResult(
                DetachedImageLoader.Load(path),
                PreviewReadyStatus,
                IsAvailable: true);
        }
        catch (FileNotFoundException ex)
        {
            return PreviewUnavailable(ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return PreviewUnavailable(ex);
        }
        catch (ArgumentException ex)
        {
            return PreviewUnavailable(ex);
        }
        catch (IOException ex)
        {
            return PreviewUnavailable(ex);
        }
        catch (OutOfMemoryException ex)
        {
            // GDI+ reports many corrupt or unsupported image streams with this exception.
            return PreviewUnavailable(ex);
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            return PreviewUnavailable(ex);
        }
    }

    public static Task<string> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return CopyAsync(
            sourcePath,
            destinationDirectory,
            PhotoCopier.CopyAsync,
            cancellationToken);
    }

    public static async Task<string> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        Func<string, string, CancellationToken, Task<CopyResult>> copy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(copy);

        try
        {
            CopyResult result = await copy(
                sourcePath,
                destinationDirectory,
                cancellationToken);
            return CopySucceededStatus(result.DestinationPath);
        }
        catch (UnauthorizedAccessException ex)
        {
            return CopyFailedStatus(ex);
        }
        catch (IOException ex)
        {
            return CopyFailedStatus(ex);
        }
    }

    private static PreviewPresentationResult PreviewUnavailable(Exception exception)
    {
        return new PreviewPresentationResult(
            Image: null,
            PreviewUnavailableStatus(exception),
            IsAvailable: false);
    }
}
