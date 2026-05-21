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
    private readonly ILogger<AdminResidentMobizioController> _log;

    public AdminResidentMobizioController(
        AppDbContext db, MobizioService mobizio, StorageSasService sas,
        IConfiguration config, ILogger<AdminResidentMobizioController> log)
    {
        _db = db;
        _mobizio = mobizio;
        _sas = sas;
        _config = config;
        _log = log;
    }

    /// <summary>Diagnostic: trace This Is Me field fetch for a resident by known Mobizio IDs.</summary>
    [HttpGet("{resident}/debug-this-is-me")]
    public async Task<IActionResult> DebugThisIsMe(string resident, [FromQuery] string caseId, [FromQuery] string tenantCaseId)
    {
        var diag = new List<string>();
        try
        {
            var token = await _mobizio.GetTokenForDebugAsync();
            var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var fields = await _mobizio.GetThisIsMeFieldsAsync(http, caseId, tenantCaseId, diag);
            diag.Add($"Fields returned: {fields.Count}");
            return Ok(new { diag, fields });
        }
        catch (Exception ex)
        {
            diag.Add($"Exception: {ex.Message}");
            return StatusCode(500, new { diag });
        }
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

        // Look up stored Mobizio IDs to skip the expensive 5000-case list search
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == residentKey);
        var knownCaseId = snapshot?.MobizioId;
        var knownTenantCaseId = snapshot?.MobizioTenantId;
        if (!string.IsNullOrWhiteSpace(knownCaseId))
            diag.Add($"Found stored Mobizio IDs: caseId={knownCaseId}, tenantCaseId={knownTenantCaseId}");

        // Collect already-imported element IDs so the service can skip them and fetch the next batch
        var safeResidentForQuery = residentKey.Replace("/", "-").Replace("\\", "-");
        var blobPrefix = $"photo/{safeResidentForQuery}/mobizio/elem_";
        var existingPaths = await _db.MediaAssets
            .Where(m => m.BlobPath.StartsWith(blobPrefix))
            .Select(m => m.BlobPath)
            .ToListAsync();
        var alreadyImported = existingPaths
            .Select(p =>
            {
                var seg = p[blobPrefix.Length..];
                var dot = seg.IndexOf('.');
                return int.TryParse(dot >= 0 ? seg[..dot] : seg, out var id) ? id : -1;
            })
            .Where(id => id >= 0)
            .ToHashSet();
        diag.Add($"{alreadyImported.Count} element(s) already imported, will skip those");

        // 1. Fetch photo bytes from Access Care Planning (decoded from encodedValue in elements)
        IReadOnlyList<(int ElementId, byte[] Data, string ContentType)> photos;
        try
        {
            photos = await _mobizio.GetActivityPhotoUrlsAsync(residentKey, diag, alreadyImported,
                knownCaseId: knownCaseId, knownTenantCaseId: knownTenantCaseId);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ActivityPhotos] Failed to query Mobizio for {Resident}", residentKey);
            return StatusCode(500, new { error = $"Failed to query Mobizio: {ex.Message}", diag });
        }

        if (photos.Count == 0)
            return Ok(new { count = 0, mediaIds = Array.Empty<Guid>(), diag });

        // 2. Upload each photo to Azure Blob Storage and register a MediaAsset
        var container = _config["Storage:ContainerPhotos"] ?? "photos";
        var safeResident = residentKey.Replace("/", "-").Replace("\\", "-");
        var mediaIds = new List<Guid>();

        foreach (var (elementId, data, contentType) in photos)
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
                mediaIds.Add(existing.Id);
                continue;
            }

            try
            {
                var ext = contentType switch
                {
                    "image/png" => ".png",
                    "image/gif" => ".gif",
                    "image/webp" => ".webp",
                    _ => ".jpg"
                };
                var blobPath = $"{blobPathPrefix}{ext}";

                await using var stream = new MemoryStream(data);
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
                diag.Add($"Element {elementId}: imported as {media.Id}");
            }
            catch (Exception ex)
            {
                diag.Add($"Element {elementId}: exception — {ex.Message}");
            }
        }

        return Ok(new { count = mediaIds.Count, mediaIds, diag });
    }
}
