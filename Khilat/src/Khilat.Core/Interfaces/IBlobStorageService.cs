namespace Khilat.Core.Interfaces;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream fileStream, string fileName, string contentType);
    Task<bool> DeleteAsync(string blobUrl);
    Task<string> GetSasUrlAsync(string blobUrl, int expiryMinutes = 60);
}
