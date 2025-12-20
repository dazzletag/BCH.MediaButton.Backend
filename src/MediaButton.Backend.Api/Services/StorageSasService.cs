using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace MediaButtonBackend.Services;

public class StorageSasService
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _config;

    public StorageSasService(IConfiguration config)
    {
        _config = config;
        var conn = _config["Storage:ConnectionString"] ?? throw new InvalidOperationException("Storage connection string missing.");
        _blobServiceClient = new BlobServiceClient(conn);
    }

    public (Uri uri, DateTimeOffset expiresAt) GetWriteSasUri(string container, string blobPath, int ttlMinutes = 15)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttlMinutes);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresAt
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Create | BlobSasPermissions.Write | BlobSasPermissions.Add);

        var sas = blobClient.GenerateSasUri(sasBuilder);
        return (sas, expiresAt);
    }

    public (Uri uri, DateTimeOffset expiresAt) GetReadSasUri(string container, string blobPath, int? ttlMinutesOverride = null)
    {
        var ttl = ttlMinutesOverride ?? _config.GetValue<int?>("Storage:DefaultSasTtlMinutes") ?? 120;
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(ttl);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = container,
            BlobName = blobPath,
            Resource = "b",
            ExpiresOn = expiresAt
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sas = blobClient.GenerateSasUri(sasBuilder);
        return (sas, expiresAt);
    }

    public async Task<bool> DeleteBlobIfExistsAsync(string container, string blobPath)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var resp = await blobClient.DeleteIfExistsAsync();
        return resp.Value;
    }
}
