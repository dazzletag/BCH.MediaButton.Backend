using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Portal-facing endpoints for the Pi-local video cache.
///
/// Reads come straight from the backend's mirror of each device's cache
/// (populated by DeviceCacheController's snapshot endpoint, so the data
/// lags playback by up to the Pi's snapshot interval — usually a few
/// minutes). Writes (delete / play / force-term) enqueue a row in
/// DeviceCacheCommands; the Pi picks it up on its next sync tick. The
/// UI should show 'pending …' until the command is acked.
/// </summary>
[ApiController]
[Route("api/admin/residents/{resident}/cache")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminCacheController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminCacheController(AppDbContext db)
    {
        _db = db;
    }

    private static string NormalizeResident(string resident) =>
        Uri.UnescapeDataString(resident ?? string.Empty).Trim();

    /// <summary>
    /// Cached videos for this resident across every device they're
    /// assigned to. Sorted by title for stable display.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> ListForResident(string resident)
    {
        var key = NormalizeResident(resident);
        if (string.IsNullOrWhiteSpace(key)) return BadRequest("resident is required.");

        var rows = await _db.CachedVideos
            .Where(v => v.Resident == key)
            .OrderBy(v => v.Title)
            .ToListAsync();

        var videos = rows.Select(v => new CachedVideoView(
            v.Id, v.DeviceId, v.Resident, v.Source, v.SourceId, v.Title, v.Term,
            v.FilesizeBytes, v.DurationSeconds, v.DownloadedAt, v.LastPlayedAt,
            v.PlayCount, v.Protected, v.FirstSeenAt, v.LastSeenAt)).ToList();

        var devices = rows.Select(v => v.DeviceId).Distinct().OrderBy(s => s).ToList();
        DateTimeOffset? lastSeen = rows.Count == 0 ? null : rows.Max(v => v.LastSeenAt);

        return Ok(new ResidentCacheResponse(key, videos, devices, lastSeen));
    }

    /// <summary>
    /// Enqueue a delete command for the given mirrored row. The Pi
    /// removes the file from disk + DB on its next poll and acks back.
    /// The backend's mirror row is removed once the Pi's next snapshot
    /// confirms the deletion.
    /// </summary>
    [HttpDelete("{videoId:guid}")]
    public async Task<IActionResult> EnqueueDelete(string resident, Guid videoId)
    {
        var key = NormalizeResident(resident);
        var row = await _db.CachedVideos.FirstOrDefaultAsync(v => v.Id == videoId && v.Resident == key);
        if (row == null) return NotFound();
        if (row.Protected)
            return BadRequest("Protected (family) uploads cannot be deleted from the portal.");

        var cmd = new DeviceCacheCommand
        {
            DeviceId = row.DeviceId,
            CommandType = "delete_video",
            CreatedBy = User.Identity?.Name,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = row.Source,
                sourceId = row.SourceId,
                resident = row.Resident,
            }),
        };
        _db.DeviceCacheCommands.Add(cmd);
        await _db.SaveChangesAsync();
        return Accepted(new { commandId = cmd.Id, deviceId = row.DeviceId, status = cmd.Status });
    }

    /// <summary>
    /// Enqueue a 'play this cached video now' command. The Pi starts a
    /// manual session with just this one cached file as the playlist.
    /// </summary>
    [HttpPost("{videoId:guid}/play")]
    public async Task<IActionResult> EnqueuePlay(string resident, Guid videoId)
    {
        var key = NormalizeResident(resident);
        var row = await _db.CachedVideos.FirstOrDefaultAsync(v => v.Id == videoId && v.Resident == key);
        if (row == null) return NotFound();

        var cmd = new DeviceCacheCommand
        {
            DeviceId = row.DeviceId,
            CommandType = "play_video",
            CreatedBy = User.Identity?.Name,
            PayloadJson = JsonSerializer.Serialize(new
            {
                source = row.Source,
                sourceId = row.SourceId,
                resident = row.Resident,
                title = row.Title,
            }),
        };
        _db.DeviceCacheCommands.Add(cmd);
        await _db.SaveChangesAsync();
        return Accepted(new { commandId = cmd.Id, deviceId = row.DeviceId, status = cmd.Status });
    }

    /// <summary>
    /// Enqueue a 'force more downloads for this term' command on every
    /// device assigned to the resident. The Pi temporarily bumps the
    /// resident's per-term cap for the named term so the downloader
    /// fetches additional candidates beyond the usual limit.
    /// </summary>
    [HttpPost("force-term")]
    public async Task<IActionResult> EnqueueForceTerm(string resident, [FromBody] ForceTermRequest req)
    {
        var key = NormalizeResident(resident);
        if (req is null || string.IsNullOrWhiteSpace(req.Term))
            return BadRequest("term is required.");

        var devices = await _db.Devices
            .Where(d => d.ResidentKey == key)
            .Select(d => d.DeviceId)
            .ToListAsync();
        if (devices.Count == 0)
            return BadRequest($"No devices are currently assigned to '{key}'.");

        var count = Math.Clamp(req.Count ?? 3, 1, 10);
        var created = new List<Guid>();
        foreach (var deviceId in devices)
        {
            var cmd = new DeviceCacheCommand
            {
                DeviceId = deviceId,
                CommandType = "force_term",
                CreatedBy = User.Identity?.Name,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    resident = key,
                    term = req.Term.Trim(),
                    count,
                }),
            };
            _db.DeviceCacheCommands.Add(cmd);
            created.Add(cmd.Id);
        }
        await _db.SaveChangesAsync();
        return Accepted(new { commandIds = created, devices, term = req.Term.Trim(), count });
    }

    /// <summary>
    /// Returns pending and recently-acked commands for transparency
    /// (e.g., the portal shows 'deleting…' until the Pi confirms).
    /// </summary>
    [HttpGet("commands")]
    public async Task<IActionResult> ListPendingCommands(string resident)
    {
        var key = NormalizeResident(resident);
        // Devices serving this resident, then their pending/acked commands.
        var devices = await _db.Devices
            .Where(d => d.ResidentKey == key)
            .Select(d => d.DeviceId)
            .ToListAsync();
        if (devices.Count == 0) return Ok(Array.Empty<DeviceCacheCommandView>());

        var pendingStatuses = new[] { "pending", "acked" };
        var cmds = await _db.DeviceCacheCommands
            .Where(c => devices.Contains(c.DeviceId) && pendingStatuses.Contains(c.Status))
            .OrderByDescending(c => c.CreatedAt)
            .Take(50)
            .ToListAsync();

        var view = cmds.Select(c =>
        {
            object payload = new { };
            try { payload = JsonSerializer.Deserialize<object>(c.PayloadJson) ?? new { }; }
            catch { /* ignore malformed; show empty payload */ }
            return new DeviceCacheCommandView(c.Id, c.DeviceId, c.CommandType, payload, c.CreatedAt, c.Status);
        });
        return Ok(view);
    }
}
