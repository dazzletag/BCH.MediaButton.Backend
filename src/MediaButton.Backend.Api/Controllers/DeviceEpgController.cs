using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Receives the electronic programme guide a device reads off its own aerial.
/// </summary>
[ApiController]
[Route("api/device/{deviceId}/epg")]
public class DeviceEpgController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<DeviceEpgController> _log;

    public DeviceEpgController(AppDbContext db, ILogger<DeviceEpgController> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Upload a batch of programmes. Idempotent: a device re-sending the same
    /// listing updates the existing rows rather than duplicating them, so it
    /// can simply push everything it has on each sync without tracking what it
    /// sent last time.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Upload(string deviceId, [FromBody] EpgUploadPayload payload)
    {
        var authedDevice = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(authedDevice, deviceId, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Device mismatch.");

        if (payload?.Events == null || payload.Events.Count == 0)
            return BadRequest("Events are required.");

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return NotFound("Unknown device.");

        // The EPG belongs to the building. Prefer the device's own care home;
        // fall back to the assigned resident's, so a device that predates the
        // CareHomeId field still works.
        var careHomeId = device.CareHomeId;
        if (careHomeId == null && !string.IsNullOrWhiteSpace(device.ResidentKey))
        {
            careHomeId = await _db.ResidentPlaylists
                .Where(r => r.Resident == device.ResidentKey)
                .Select(r => r.CareHomeId)
                .FirstOrDefaultAsync();
        }
        if (careHomeId == null || careHomeId == Guid.Empty)
            return BadRequest("This device has no care home set, so its EPG cannot be filed. "
                              + "Set CareHomeId on the device first.");

        var home = careHomeId.Value;
        var now = DateTimeOffset.UtcNow;

        // Load only the window being written, so a nightly full upload does not
        // pull the whole table into memory.
        var minStart = payload.Events.Min(e => e.StartUtc);
        var maxStart = payload.Events.Max(e => e.StartUtc);
        var existing = await _db.EpgEvents
            .Where(e => e.CareHomeId == home && e.StartUtc >= minStart && e.StartUtc <= maxStart)
            .ToDictionaryAsync(e => (e.ChannelName, e.StartUtc));

        int added = 0, updated = 0;
        foreach (var ev in payload.Events)
        {
            if (string.IsNullOrWhiteSpace(ev.ChannelName) || string.IsNullOrWhiteSpace(ev.Title))
                continue;

            if (existing.TryGetValue((ev.ChannelName, ev.StartUtc), out var row))
            {
                updated++;
            }
            else
            {
                row = new EpgEvent
                {
                    CareHomeId = home,
                    ChannelName = ev.ChannelName,
                    StartUtc = ev.StartUtc,
                };
                _db.EpgEvents.Add(row);
                existing[(ev.ChannelName, ev.StartUtc)] = row;
                added++;
            }

            row.ChannelNumber = ev.ChannelNumber;
            row.ChannelUuid = Trim(ev.ChannelUuid, 64);
            row.Title = Trim(ev.Title, 400)!;
            row.Subtitle = Trim(ev.Subtitle, 400);
            row.Description = Trim(ev.Description, 2000);
            row.StopUtc = ev.StopUtc;
            row.SeriesCrid = Trim(ev.SeriesCrid, 300);
            row.EpisodeCrid = Trim(ev.EpisodeCrid, 300);
            row.Genre = Trim(ev.Genre, 200);
            row.IsHd = ev.IsHd;
            row.IsSubtitled = ev.IsSubtitled;
            row.IsAudioDescribed = ev.IsAudioDescribed;
            row.SourceDeviceId = deviceId;
            row.UpdatedAt = now;
        }

        // Programmes that finished more than a day ago are of no further use
        // and would otherwise accumulate indefinitely.
        var cutoff = now.AddDays(-1);
        var stale = await _db.EpgEvents
            .Where(e => e.CareHomeId == home && e.StopUtc < cutoff)
            .ToListAsync();
        if (stale.Count > 0) _db.EpgEvents.RemoveRange(stale);

        await _db.SaveChangesAsync();
        _log.LogInformation("EPG from {Device}: {Added} added, {Updated} updated, {Pruned} pruned",
                            deviceId, added, updated, stale.Count);

        return Ok(new { added, updated, pruned = stale.Count, careHomeId = home });
    }

    private static string? Trim(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);
}

/// <summary>A batch of programmes uploaded by a device.</summary>
public record EpgUploadPayload(List<EpgUploadEvent> Events);

/// <summary>One programme as the device read it off the air.</summary>
public record EpgUploadEvent(
    string ChannelName,
    DateTimeOffset StartUtc,
    DateTimeOffset StopUtc,
    string Title,
    int? ChannelNumber = null,
    string? ChannelUuid = null,
    string? Subtitle = null,
    string? Description = null,
    string? SeriesCrid = null,
    string? EpisodeCrid = null,
    string? Genre = null,
    bool IsHd = false,
    bool IsSubtitled = false,
    bool IsAudioDescribed = false);
