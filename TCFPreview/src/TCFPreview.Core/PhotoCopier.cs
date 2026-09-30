namespace TCFPreview.Core;

public static class PhotoCopier
{
    public static async Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        CancellationToken cancellationToken = default)
    {
        return await CopyAsync(
            sourcePath,
            destinationDirectory,
            static (source, destination, token) => source.CopyToAsync(destination, token),
            cancellationToken);
    }

    internal static async Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        Func<Stream, Stream, CancellationToken, Task> copyAsync,
        CancellationToken cancellationToken = default)
    {
        return await CopyAsync(
            sourcePath,
            destinationDirectory,
            copyAsync,
            File.Delete,
            static (source, destination) => File.Move(source, destination, overwrite: false),
            cancellationToken);
    }

    internal static async Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        Func<Stream, Stream, CancellationToken, Task> copyAsync,
        Action<string> deleteFile,
        CancellationToken cancellationToken = default)
    {
        return await CopyAsync(
            sourcePath,
            destinationDirectory,
            copyAsync,
            deleteFile,
            static (source, destination) => File.Move(source, destination, overwrite: false),
            cancellationToken);
    }

    internal static async Task<CopyResult> CopyAsync(
        string sourcePath,
        string destinationDirectory,
        Func<Stream, Stream, CancellationToken, Task> copyAsync,
        Action<string> deleteFile,
        Action<string, string> moveFile,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(copyAsync);
        ArgumentNullException.ThrowIfNull(deleteFile);
        ArgumentNullException.ThrowIfNull(moveFile);

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source photo does not exist.", sourcePath);
        }

        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Destination folder does not exist: {destinationDirectory}");
        }

        string fileName = Path.GetFileName(sourcePath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        (string stagingPath, FileStream staging) = CreateStagingFile(
            destinationDirectory,
            cancellationToken);
        Exception? primaryException = null;
        bool ownsStagingFile = true;

        try
        {
            await using (staging)
            {
                await copyAsync(source, staging, cancellationToken);
            }

            for (int collisionNumber = 0; ; collisionNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string candidateName = collisionNumber == 0
                    ? fileName
                    : $"{fileNameWithoutExtension} ({collisionNumber}){extension}";
                string destinationPath = Path.Combine(destinationDirectory, candidateName);

                try
                {
                    moveFile(stagingPath, destinationPath);
                    ownsStagingFile = false;
                    return new CopyResult(sourcePath, destinationPath);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    continue;
                }

            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            if (ownsStagingFile)
            {
                try
                {
                    deleteFile(stagingPath);
                }
                catch (Exception cleanupException) when (primaryException is not null)
                {
                    primaryException.Data["PhotoCopier.CleanupException"] = cleanupException;
                }
            }
        }
    }

    private static (string Path, FileStream Stream) CreateStagingFile(
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string stagingPath = Path.Combine(
                destinationDirectory,
                $".tcfpreview-{Guid.NewGuid():N}.tmp");

            try
            {
                FileStream staging = new(
                    stagingPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 81920,
                    useAsync: true);
                return (stagingPath, staging);
            }
            catch (IOException) when (File.Exists(stagingPath))
            {
                continue;
            }
        }
    }
}
