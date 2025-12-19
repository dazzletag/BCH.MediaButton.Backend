using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/media")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminMediaController : ControllerBase
{
    private readonly StorageSasService _sasService;
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminMediaController(StorageSasService sasService, AppDbContext db, IConfiguration config)
    {
        _sasService = sasService;
        _db = db;
        _config = config;
    }

    private static string SanitizeResident(string resident) =>
        (resident ?? string.Empty).Trim().Replace("/", "-").Replace("\\", "-");

    private static string DetectResidentFromPath(string blobPath)
    {
        // Expected format: {type}/{resident}/{guid}/{filename}
        var parts = (blobPath ?? string.Empty).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : string.Empty;
    }

    [HttpPost("upload-url")]
    public IActionResult GetUploadUrl([FromBody] MediaUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest("FileName is required.");
        }
        if (string.IsNullOrWhiteSpace(request.Resident))
        {
            return BadRequest("Resident is required.");
        }

        var container = request.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

        var safeName = Path.GetFileName(request.FileName);
        var resident = SanitizeResident(request.Resident);
        var blobPath = $"{request.Type.ToString().ToLowerInvariant()}/{resident}/{Guid.NewGuid():N}/{safeName}";

        var (uri, expiresAt) = _sasService.GetWriteSasUri(container, blobPath, ttlMinutes: 15);

        return Ok(new MediaUploadResponse(uri, blobPath, expiresAt));
    }

    [HttpPost]
    public async Task<IActionResult> RegisterMedia([FromBody] MediaRegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BlobPath))
        {
            return BadRequest("BlobPath is required.");
        }
        if (string.IsNullOrWhiteSpace(request.Resident))
        {
            return BadRequest("Resident is required.");
        }

        var media = new MediaAsset
        {
            Name = request.Name ?? Path.GetFileName(request.BlobPath),
            BlobPath = request.BlobPath,
            ContentType = request.ContentType,
            Type = request.Type,
            DurationSeconds = request.DurationSeconds,
            UploadedBy = User.Identity?.Name
        };

        _db.MediaAssets.Add(media);
        await _db.SaveChangesAsync();

        return Ok(new { mediaId = media.Id });
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<object>>> ListMedia([FromQuery] MediaType? type = null, [FromQuery] string? resident = null)
    {
        var query = _db.MediaAssets.AsQueryable();
        if (type.HasValue)
        {
            query = query.Where(m => m.Type == type.Value);
        }
        var normalizedResident = SanitizeResident(resident ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(normalizedResident))
        {
            query = query.Where(m => m.BlobPath.Contains($"/{normalizedResident}/"));
        }

        var isAdmin = User.IsInRole("Admin");
        if (!isAdmin)
        {
            var user = User.Identity?.Name ?? string.Empty;
            query = query.Where(m => m.UploadedBy == user);
        }

            var items = await query
            .OrderByDescending(m => m.UploadedAt)
            .Take(200)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.BlobPath,
                m.Type,
                m.ContentType,
                m.DurationSeconds,
                m.UploadedAt,
                Resident = DetectResidentFromPath(m.BlobPath),
                m.UploadedBy
            }).ToListAsync();

        return Ok(items);
    }

    [HttpGet("{id:guid}/sas")]
    public async Task<IActionResult> GetReadSas(Guid id)
    {
        var media = await _db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id);
        if (media == null) return NotFound();

        var isAdmin = User.IsInRole("Admin");
        var user = User.Identity?.Name ?? string.Empty;
        if (!isAdmin && !string.Equals(media.UploadedBy, user, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var container = media.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

        var (uri, expiresAt) = _sasService.GetReadSasUri(container, media.BlobPath);
        return Ok(new { url = uri, expiresAtUtc = expiresAt });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var media = await _db.MediaAssets.FirstOrDefaultAsync(m => m.Id == id);
        if (media == null) return NotFound();

        var isAdmin = User.IsInRole("Admin");
        var user = User.Identity?.Name ?? string.Empty;
        if (!isAdmin && !string.Equals(media.UploadedBy, user, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var container = media.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

        _db.MediaAssets.Remove(media);
        await _db.SaveChangesAsync();

        _ = _sasService.DeleteBlobIfExistsAsync(container, media.BlobPath); // fire and forget
        return NoContent();
    }
}
