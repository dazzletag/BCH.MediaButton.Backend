using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace MediaButtonBackend.Services;

/// <summary>
/// Ensures Blob Storage CORS rules allow our frontend origins to PUT directly using SAS.
/// </summary>
public class StorageCorsInitializer
{
    private readonly BlobServiceClient _blobServiceClient;
    private readonly IConfiguration _config;

    public StorageCorsInitializer(IConfiguration config)
    {
        _config = config;
        var conn = _config["Storage:ConnectionString"] ?? throw new InvalidOperationException("Storage connection string missing.");
        _blobServiceClient = new BlobServiceClient(conn);
    }

    public async Task EnsureCorsAsync()
    {
        var enabled = _config.GetValue<bool?>("Storage:EnsureCors") ?? true;
        if (!enabled)
        {
            return;
        }

        var originsRaw = _config["Storage:CorsOrigins"] ?? "http://localhost:5173,https://mediabutton.azurewebsites.net";
        var origins = originsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var props = await _blobServiceClient.GetPropertiesAsync();
        props.Value.Cors.Clear();
        props.Value.Cors.Add(
            new BlobCorsRule
            {
                AllowedHeaders = "*",
                ExposedHeaders = "*",
                AllowedOrigins = string.Join(",", origins),
                AllowedMethods = "GET,PUT,POST,OPTIONS,HEAD",
                MaxAgeInSeconds = 3600
            });

        await _blobServiceClient.SetPropertiesAsync(props);
    }
}
