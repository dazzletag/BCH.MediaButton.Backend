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

    /// <summary>
    /// Re-sign an absolute blob URL that points at our own storage account.
    /// Playlist items are stored with the signed URL they had when they were
    /// saved, so the SAS is frozen at that moment and the link is dead once its
    /// TTL passes — re-signing on the way out keeps saved media playable
    /// indefinitely. Returns null when the URL is not a blob of ours in one of
    /// <paramref name="allowedContainers"/>, so callers can pass the original
    /// value through untouched.
    /// </summary>
    public Uri? TryRefreshReadSasUri(string? url, IEnumerable<string> allowedContainers, int? ttlMinutesOverride = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (!string.Equals(uri.Host, _blobServiceClient.Uri.Host, StringComparison.OrdinalIgnoreCase)) return null;

        var path = uri.AbsolutePath.TrimStart('/');
        var slash = path.IndexOf('/');
        if (slash <= 0) return null;

        var container = Uri.UnescapeDataString(path[..slash]);
        var blobPath = Uri.UnescapeDataString(path[(slash + 1)..]);
        if (string.IsNullOrWhiteSpace(blobPath)) return null;
        if (!allowedContainers.Contains(container, StringComparer.OrdinalIgnoreCase)) return null;

        try
        {
            return GetReadSasUri(container, blobPath, ttlMinutesOverride).uri;
        }
        catch
        {
            return null;
        }
    }

    public async Task UploadBlobAsync(string container, string blobPath, Stream content, string contentType)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        await blobClient.UploadAsync(content, new Azure.Storage.Blobs.Models.BlobUploadOptions
        {
            HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType }
        });
    }

    public async Task<bool> DeleteBlobIfExistsAsync(string container, string blobPath)
    {
        var blobClient = _blobServiceClient.GetBlobContainerClient(container).GetBlobClient(blobPath);
        var resp = await blobClient.DeleteIfExistsAsync();
        return resp.Value;
    }
}
