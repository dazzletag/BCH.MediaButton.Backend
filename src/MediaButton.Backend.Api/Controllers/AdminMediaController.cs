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

    [HttpPost("upload-url")]
    public IActionResult GetUploadUrl([FromBody] MediaUploadRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return BadRequest("FileName is required.");
        }

        var container = request.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

        var safeName = Path.GetFileName(request.FileName);
        var blobPath = $"{request.Type.ToString().ToLowerInvariant()}/{Guid.NewGuid():N}/{safeName}";

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
    public async Task<ActionResult<IEnumerable<object>>> ListMedia([FromQuery] MediaType? type = null)
    {
        var query = _db.MediaAssets.AsQueryable();
        if (type.HasValue)
        {
            query = query.Where(m => m.Type == type.Value);
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
                m.UploadedAt
            }).ToListAsync();

        return Ok(items);
    }
}
