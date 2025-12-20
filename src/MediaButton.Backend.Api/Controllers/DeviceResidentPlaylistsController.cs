using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MediaButtonBackend.Services;
using System.IO;
using System.Linq;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/device/{deviceId}/resident")]
public class DeviceResidentPlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly StorageSasService _sas;
    private readonly IConfiguration _config;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public DeviceResidentPlaylistsController(AppDbContext db, StorageSasService sas, IConfiguration config)
    {
        _db = db;
        _sas = sas;
        _config = config;
    }

    [HttpPost("ai-playlist")]
    public async Task<IActionResult> UploadAi(string deviceId, [FromBody] AiPlaylistPayload payload)
    {
        var authedDevice = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(authedDevice, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Device mismatch.");
        }

        if (payload == null || string.IsNullOrWhiteSpace(payload.Resident))
        {
            return BadRequest("Resident and playlist are required.");
        }

        if (payload.Playlist == null || payload.Playlist.Count == 0)
        {
            return BadRequest("Playlist items are required.");
        }

        var resident = NormalizeResident(payload.Resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == resident);
        var isNew = snapshot == null;
        snapshot ??= new ResidentPlaylistSnapshot { Resident = resident };

        try
        {
            snapshot.AiPlaylistJson = JsonSerializer.Serialize(payload, _jsonOptions);
            snapshot.AiUpdatedAt = DateTimeOffset.UtcNow;

            if (isNew)
            {
                _db.ResidentPlaylists.Add(snapshot);
            }
            else
            {
                _db.ResidentPlaylists.Update(snapshot);
            }
            await _db.SaveChangesAsync();
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Failed to save AI playlist: {ex.Message}");
        }
    }

    [HttpGet("{resident}/manual-playlist")]
    public async Task<ActionResult<DeviceManualPlaylistResponse>> GetManual(string deviceId, string resident)
    {
        var authedDevice = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(authedDevice, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Device mismatch.");
        }

        var key = NormalizeResident(resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == key);
        var items = ParseManual(snapshot?.ManualPlaylistJson);
        var resolved = await ResolveMediaAsync(items, key);
        return Ok(new DeviceManualPlaylistResponse(key, resolved, snapshot?.ManualUpdatedAt, snapshot?.ManualUpdatedBy));
    }

    private static string NormalizeResident(string resident) =>
        (resident ?? string.Empty).Trim();

    private static IReadOnlyList<string> ParseManual(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();
        try
        {
            var items = JsonSerializer.Deserialize<List<string>>(json, _jsonOptions);
            return items?.Where(i => !string.IsNullOrWhiteSpace(i)).Select(i => i.Trim()).ToList()
                   ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private async Task<IReadOnlyList<object>> ResolveMediaAsync(IReadOnlyList<string> items, string resident)
    {
        var output = new List<object>();
        var normalizedResident = NormalizeResident(resident);
        var residentMedia = string.IsNullOrWhiteSpace(normalizedResident)
            ? new List<MediaAsset>()
            : await _db.MediaAssets
                .Where(m => m.BlobPath.Contains($"/{normalizedResident}/"))
                .OrderByDescending(m => m.UploadedAt)
                .ToListAsync();

        foreach (var item in items)
        {
            if (item.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
            {
                var idPart = item.Substring("media:".Length);
                if (!Guid.TryParse(idPart, out var mediaId))
                {
                    continue;
                }

                var media = await _db.MediaAssets.FirstOrDefaultAsync(m => m.Id == mediaId);
                if (media == null) continue;

                // Enforce resident scoping: blob path includes /{resident}/
                if (string.IsNullOrWhiteSpace(normalizedResident) || !media.BlobPath.Contains($"/{normalizedResident}/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var container = media.Type == MediaType.Photo
                    ? _config["Storage:ContainerPhotos"] ?? "photos"
                    : _config["Storage:ContainerVideos"] ?? "videos";

                var (uri, _) = _sas.GetReadSasUri(container, media.BlobPath, ttlMinutesOverride: 60);
                output.Add(new
                {
                    url = uri.ToString(),
                    type = media.Type.ToString().ToLowerInvariant(),
                    name = media.Name ?? Path.GetFileName(media.BlobPath)
                });
                continue;
            }

            // Legacy: if the item matches an uploaded media name for this resident, resolve it to the blob
            var matchedMedia = residentMedia.FirstOrDefault(m =>
                (!string.IsNullOrWhiteSpace(m.Name) && string.Equals(m.Name, item, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(Path.GetFileName(m.BlobPath), item, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(m.BlobPath), item, StringComparison.OrdinalIgnoreCase));

            if (matchedMedia != null)
            {
                var container = matchedMedia.Type == MediaType.Photo
                    ? _config["Storage:ContainerPhotos"] ?? "photos"
                    : _config["Storage:ContainerVideos"] ?? "videos";

                var (uri, _) = _sas.GetReadSasUri(container, matchedMedia.BlobPath, ttlMinutesOverride: 60);
                output.Add(new
                {
                    url = uri.ToString(),
                    type = matchedMedia.Type.ToString().ToLowerInvariant(),
                    name = matchedMedia.Name ?? Path.GetFileName(matchedMedia.BlobPath)
                });
                continue;
            }

            output.Add(item);
        }

        return output;
    }
}
