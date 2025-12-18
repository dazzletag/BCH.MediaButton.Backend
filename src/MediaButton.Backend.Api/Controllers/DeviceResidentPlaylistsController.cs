using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/device/{deviceId}/resident")]
public class DeviceResidentPlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public DeviceResidentPlaylistsController(AppDbContext db)
    {
        _db = db;
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
    public async Task<ActionResult<ManualPlaylistResponse>> GetManual(string deviceId, string resident)
    {
        var authedDevice = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(authedDevice, deviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Device mismatch.");
        }

        var key = NormalizeResident(resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == key);
        var items = ParseManual(snapshot?.ManualPlaylistJson);
        return Ok(new ManualPlaylistResponse(key, items, snapshot?.ManualUpdatedAt, snapshot?.ManualUpdatedBy));
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
}
