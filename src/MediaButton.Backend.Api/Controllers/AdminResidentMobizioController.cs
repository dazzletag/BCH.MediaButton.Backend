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
    private readonly ILogger<AdminResidentMobizioController> _log;

    public AdminResidentMobizioController(
        AppDbContext db, MobizioService mobizio, StorageSasService sas,
        IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<AdminResidentMobizioController> log)
    {
        _db = db;
        _mobizio = mobizio;
        _sas = sas;
        _config = config;
        _httpFactory = httpFactory;
        _log = log;
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

        _log.LogInformation("[ActivityPhotos] Starting import for resident: {Resident}", residentKey);
        var diag = new List<string>();

        // 1. Ask Mobizio for photo download URLs
        IReadOnlyList<(int ElementId, string Url)> photoUrls;
        try
        {
            photoUrls = await _mobizio.GetActivityPhotoUrlsAsync(residentKey);
            diag.Add($"Mobizio returned {photoUrls.Count} photo URL(s)");
            _log.LogInformation("[ActivityPhotos] Mobizio returned {Count} photo URL(s) for {Resident}",
                photoUrls.Count, residentKey);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ActivityPhotos] Failed to query Mobizio for {Resident}", residentKey);
            return StatusCode(500, new { error = $"Failed to query Mobizio: {ex.Message}", diag });
        }

        if (photoUrls.Count == 0)
        {
            diag.Add("No photos found — resident may not have activity record submissions with photos, or the case ID could not be matched");
            return Ok(new { count = 0, mediaIds = Array.Empty<Guid>(), diag });
        }

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
                diag.Add($"Element {elementId}: already imported as {existing.Id}");
                _log.LogInformation("[ActivityPhotos] Element {ElementId} already imported, reusing {MediaId}",
                    elementId, existing.Id);
                mediaIds.Add(existing.Id);
                continue;
            }

            try
            {
                _log.LogInformation("[ActivityPhotos] Downloading element {ElementId} from Mobizio", elementId);
                var response = await http.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                {
                    diag.Add($"Element {elementId}: download failed ({(int)response.StatusCode} {response.ReasonPhrase})");
                    _log.LogWarning("[ActivityPhotos] Download failed for element {ElementId}: {Status}",
                        elementId, response.StatusCode);
                    continue;
                }

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
                _log.LogInformation("[ActivityPhotos] Uploaded element {ElementId} to {BlobPath}", elementId, blobPath);

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
                diag.Add($"Element {elementId}: imported as {media.Id}");
                _log.LogInformation("[ActivityPhotos] Registered MediaAsset {MediaId} for element {ElementId}",
                    media.Id, elementId);
            }
            catch (Exception ex)
            {
                diag.Add($"Element {elementId}: exception — {ex.Message}");
                _log.LogError(ex, "[ActivityPhotos] Exception processing element {ElementId}", elementId);
            }
        }

        _log.LogInformation("[ActivityPhotos] Completed for {Resident}: {Imported}/{Total} imported",
            residentKey, mediaIds.Count, photoUrls.Count);

        return Ok(new { count = mediaIds.Count, mediaIds, diag });
    }
}
