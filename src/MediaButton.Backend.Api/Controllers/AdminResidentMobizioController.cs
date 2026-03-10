using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/residents")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminResidentMobizioController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MobizioService _mobizio;
    private readonly StorageSasService _sas;
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;

    public AdminResidentMobizioController(
        AppDbContext db, MobizioService mobizio, StorageSasService sas,
        IConfiguration config, IHttpClientFactory httpFactory)
    {
        _db = db;
        _mobizio = mobizio;
        _sas = sas;
        _config = config;
        _httpFactory = httpFactory;
    }

    /// <summary>
    /// Fetches recent activity record photos for a resident from Mobizio,
    /// uploads them to Azure Blob Storage, registers MediaAsset records,
    /// and returns the new media IDs ready to be added to the resident's playlist.
    /// </summary>
    [HttpPost("{resident}/fetch-activity-photos")]
    public async Task<IActionResult> FetchActivityPhotos(string resident)
    {
        var residentKey = (resident ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(residentKey))
            return BadRequest("Resident name is required.");

        // 1. Ask Mobizio for photo download URLs
        IReadOnlyList<(int ElementId, string Url)> photoUrls;
        try
        {
            photoUrls = await _mobizio.GetActivityPhotoUrlsAsync(residentKey);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to query Mobizio: {ex.Message}");
        }

        if (photoUrls.Count == 0)
            return Ok(new { count = 0, mediaIds = Array.Empty<Guid>() });

        // 2. Download each photo and upload to Azure Blob Storage
        var container = _config["Storage:ContainerPhotos"] ?? "photos";
        var safeResident = residentKey.Replace("/", "-").Replace("\\", "-");

        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);

        var mediaIds = new List<Guid>();

        foreach (var (elementId, url) in photoUrls)
        {
            // Avoid re-importing the same element
            var blobPathPrefix = $"photo/{safeResident}/mobizio/elem_{elementId}";
            var alreadyExists = await _db.MediaAssets
                .AnyAsync(m => m.BlobPath.StartsWith(blobPathPrefix));
            if (alreadyExists)
            {
                var existing = await _db.MediaAssets
                    .FirstAsync(m => m.BlobPath.StartsWith(blobPathPrefix));
                mediaIds.Add(existing.Id);
                continue;
            }

            try
            {
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                var ext = contentType switch
                {
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    _ => ".jpg"
                };
                var blobPath = $"{blobPathPrefix}{ext}";

                await using var stream = await response.Content.ReadAsStreamAsync();
                await _sas.UploadBlobAsync(container, blobPath, stream, contentType);

                var media = new MediaAsset
                {
                    BlobPath = blobPath,
                    ContentType = contentType,
                    Type = MediaType.Photo,
                    Name = $"Activity photo — {residentKey}",
                    UploadedBy = User.Identity?.Name ?? "mobizio-import"
                };
                _db.MediaAssets.Add(media);
                await _db.SaveChangesAsync();
                mediaIds.Add(media.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FetchActivityPhotos] Skipping element {elementId}: {ex.Message}");
            }
        }

        return Ok(new { count = mediaIds.Count, mediaIds });
    }
}
