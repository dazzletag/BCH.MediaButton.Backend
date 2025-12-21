using System.IO;
using System.Linq;
using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    private static IReadOnlyList<JsonElement> ParseManual(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<JsonElement>()
            : JsonSerializer.Deserialize<List<JsonElement>>(json, _jsonOptions) ?? new List<JsonElement>();

    private async Task<IReadOnlyList<object?>> ResolveMediaAsync(IReadOnlyList<JsonElement> items, string resident)
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
            var resolved = await ResolveItemAsync(item, normalizedResident, residentMedia);
            if (resolved != null)
            {
                output.Add(resolved);
            }
        }

        return output;
    }

    private async Task<object?> ResolveItemAsync(JsonElement item, string normalizedResident, List<MediaAsset> residentMedia)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            var str = item.GetString();
            if (string.IsNullOrWhiteSpace(str)) return null;
            return await ResolveStringItemAsync(str.Trim(), normalizedResident, residentMedia);
        }

        if (item.ValueKind != JsonValueKind.Object)
        {
            return item.Deserialize<object?>(_jsonOptions);
        }

        var obj = JsonSerializer.Deserialize<Dictionary<string, object?>>(item.GetRawText(), _jsonOptions)
                  ?? new Dictionary<string, object?>();

        var url = ExtractString(obj, "url", "mediaUrl", "source", "href");
        var radio = ExtractString(obj, "radioUrl", "radio", "station");
        var type = ExtractString(obj, "type", "kind", "mediaType", "media_type");
        var name = ExtractString(obj, "name", "title", "label");

        if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(radio))
        {
            url = radio;
            type ??= "radio";
        }

        if (!string.IsNullOrWhiteSpace(url) && url.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveMediaReferenceAsync(url, normalizedResident);
            if (resolved is { } mediaRef)
            {
                obj["url"] = mediaRef.Url;
                obj["name"] = name ?? mediaRef.Name;
                obj["type"] = type ?? mediaRef.Type;
                return obj;
            }
        }
        else if (string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(name))
        {
            var matchedMedia = ResolveByName(residentMedia, name);
            if (matchedMedia is { } mediaByName)
            {
                obj["url"] = mediaByName.Url;
                obj["type"] = type ?? mediaByName.Type;
                obj["name"] = mediaByName.Name;
                return obj;
            }
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            obj["url"] = url;
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            obj["type"] = type.ToLowerInvariant();
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            obj["name"] = name;
        }

        return obj;
    }

    private async Task<object?> ResolveStringItemAsync(string item, string normalizedResident, List<MediaAsset> residentMedia)
    {
        if (item.StartsWith("media:", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveMediaReferenceAsync(item, normalizedResident);
            if (resolved is { } mediaRef)
            {
                return new
                {
                    url = mediaRef.Url,
                    type = mediaRef.Type,
                    name = mediaRef.Name
                };
            }
            return null;
        }

        var matchedMedia = ResolveByName(residentMedia, item);
        if (matchedMedia is { } mediaByName)
        {
            return new
            {
                url = mediaByName.Url,
                type = mediaByName.Type,
                name = mediaByName.Name
            };
        }

        return item;
    }

    private async Task<(string Url, string Type, string Name)?> ResolveMediaReferenceAsync(string reference, string normalizedResident)
    {
        var idPart = reference.Substring("media:".Length);
        if (!Guid.TryParse(idPart, out var mediaId))
        {
            return null;
        }

        var media = await _db.MediaAssets.FirstOrDefaultAsync(m => m.Id == mediaId);
        if (media == null) return null;

        if (!string.IsNullOrWhiteSpace(normalizedResident) && !media.BlobPath.Contains($"/{normalizedResident}/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var container = media.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

                var (uri, _) = _sas.GetReadSasUri(container, media.BlobPath, ttlMinutesOverride: 1440); // 24h TTL for device playback
        return (uri.ToString(), media.Type.ToString().ToLowerInvariant(), media.Name ?? Path.GetFileName(media.BlobPath));
    }

    private (string Url, string Type, string Name)? ResolveByName(List<MediaAsset> residentMedia, string name)
    {
        var matchedMedia = residentMedia.FirstOrDefault(m =>
            (!string.IsNullOrWhiteSpace(m.Name) && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(Path.GetFileName(m.BlobPath), name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(m.BlobPath), name, StringComparison.OrdinalIgnoreCase));

        if (matchedMedia == null) return null;

        var container = matchedMedia.Type == MediaType.Photo
            ? _config["Storage:ContainerPhotos"] ?? "photos"
            : _config["Storage:ContainerVideos"] ?? "videos";

        var (uri, _) = _sas.GetReadSasUri(container, matchedMedia.BlobPath, ttlMinutesOverride: 1440); // 24h TTL for device playback
        return (uri.ToString(), matchedMedia.Type.ToString().ToLowerInvariant(), matchedMedia.Name ?? Path.GetFileName(matchedMedia.BlobPath));
    }

    private static string? ExtractString(Dictionary<string, object?> obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!obj.TryGetValue(key, out var value) || value is null) continue;
            if (value is string s && !string.IsNullOrWhiteSpace(s)) return s.Trim();
            if (value is JsonElement el)
            {
                if (el.ValueKind == JsonValueKind.String) return el.GetString();
                if (el.ValueKind == JsonValueKind.Number || el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False)
                {
                    return el.ToString();
                }
            }
        }
        return null;
    }
}
