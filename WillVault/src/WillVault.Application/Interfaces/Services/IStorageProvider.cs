namespace WillVault.Application.Interfaces.Services;

/// <summary>
/// Abstraction for file storage operations (local disk, Azure Blob, S3, etc.).
/// </summary>
public interface IStorageProvider
{
    /// <summary>
    /// Uploads a file and returns the storage path or identifier.
    /// </summary>
    /// <param name="stream">The file content stream.</param>
    /// <param name="fileName">The desired file name.</param>
    /// <param name="contentType">The MIME content type of the file.</param>
    /// <returns>The storage path or identifier for the uploaded file.</returns>
    Task<string> UploadAsync(Stream stream, string fileName, string contentType, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a file by its storage path and returns a readable stream.
    /// </summary>
    /// <param name="path">The storage path or identifier of the file.</param>
    /// <returns>A stream containing the file content.</returns>
    Task<Stream> DownloadAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a file from storage.
    /// </summary>
    /// <param name="path">The storage path or identifier of the file to delete.</param>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks whether a file exists at the specified storage path.
    /// </summary>
    /// <param name="path">The storage path or identifier to check.</param>
    /// <returns><c>true</c> if the file exists; otherwise, <c>false</c>.</returns>
    Task<bool> ExistsAsync(string path, CancellationToken cancellationToken = default);
}
