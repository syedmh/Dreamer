namespace Khilat.Infrastructure.Services;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Khilat.Core.Interfaces;

public class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _containerClient;

    public AzureBlobStorageService(BlobServiceClient blobServiceClient)
    {
        _containerClient = blobServiceClient.GetBlobContainerClient("product-images");
    }

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string contentType)
    {
        await _containerClient.CreateIfNotExistsAsync(PublicAccessType.Blob);

        var blobName = $"{Guid.NewGuid()}/{fileName}";
        var blobClient = _containerClient.GetBlobClient(blobName);

        await blobClient.UploadAsync(fileStream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType }
        });

        return blobClient.Uri.ToString();
    }

    public async Task<bool> DeleteAsync(string blobUrl)
    {
        var uri = new Uri(blobUrl);
        var blobName = string.Join("/", uri.Segments.Skip(2)); // Skip container segment
        var blobClient = _containerClient.GetBlobClient(blobName);
        var response = await blobClient.DeleteIfExistsAsync();
        return response.Value;
    }

    public Task<string> GetSasUrlAsync(string blobUrl, int expiryMinutes = 60)
    {
        var uri = new Uri(blobUrl);
        var blobName = string.Join("/", uri.Segments.Skip(2));
        var blobClient = _containerClient.GetBlobClient(blobName);

        if (!blobClient.CanGenerateSasUri)
        {
            return Task.FromResult(blobUrl);
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = _containerClient.Name,
            BlobName = blobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        return Task.FromResult(blobClient.GenerateSasUri(sasBuilder).ToString());
    }
}
