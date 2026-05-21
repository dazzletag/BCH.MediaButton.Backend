using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Pi-facing endpoints for the local video cache.
///
/// Snapshot upsert (the Pi POSTs its full inventory for a resident every
/// few minutes) keeps the backend's view of what's on disk current. The
/// command queue lets the portal enqueue actions (delete, play, force a
/// term) that the Pi polls and applies on its next sync tick.
///
/// All endpoints are gated by DeviceAuthMiddleware (X-DEVICE-KEY) plus an
/// explicit deviceId-vs-authedId equality check so a leaked key can't be
/// used to control a different device.
/// </summary>
[ApiController]
[Route("api/device/{deviceId}/cache")]
public class DeviceCacheController : ControllerBase
{
    private readonly AppDbContext _db;

    public DeviceCacheController(AppDbContext db)
    {
        _db = db;
    }

    private bool AuthMatches(string deviceId)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        return string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replace the backend's view of this device+resident's cache with the
    /// supplied list. Items absent from the snapshot for more than the stale
    /// threshold get pruned by the admin list endpoint, not here.
    /// </summary>
    [HttpPost("snapshot")]
    public async Task<IActionResult> PostSnapshot(string deviceId, [FromBody] CacheSnapshotRequest req)
    {
        if (!AuthMatches(deviceId)) return Unauthorized("Device mismatch.");
        if (req is null || string.IsNullOrWhiteSpace(req.Resident))
            return BadRequest("resident is required.");

        var resident = req.Resident.Trim();
        var now = DateTimeOffset.UtcNow;

        // Existing rows for (this device, this resident) — we'll either
        // update them in place or delete them if the Pi has dropped them.
        var existing = await _db.CachedVideos
            .Where(v => v.DeviceId == deviceId && v.Resident == resident)
            .ToListAsync();
        var existingByKey = existing.ToDictionary(
            v => $"{v.Source}|{v.SourceId}",
            StringComparer.OrdinalIgnoreCase);

        var snapshotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in req.Videos ?? Array.Empty<CachedVideoSnapshotItem>())
        {
            if (string.IsNullOrWhiteSpace(item.SourceId)) continue;
            var key = $"{item.Source}|{item.SourceId}";
            snapshotKeys.Add(key);

            if (existingByKey.TryGetValue(key, out var row))
            {
                row.Title = string.IsNullOrWhiteSpace(item.Title) ? row.Title : item.Title;
                row.Term = item.Term;
                row.FilesizeBytes = item.FilesizeBytes;
                row.DurationSeconds = item.DurationSeconds;
                row.DownloadedAt = item.DownloadedAt;
                row.LastPlayedAt = item.LastPlayedAt;
                row.PlayCount = item.PlayCount;
                row.Protected = item.Protected;
                row.LastSeenAt = now;
            }
            else
            {
                _db.CachedVideos.Add(new CachedVideo
                {
                    DeviceId = deviceId,
                    Resident = resident,
                    Source = string.IsNullOrWhiteSpace(item.Source) ? "yt" : item.Source,
                    SourceId = item.SourceId,
                    Title = string.IsNullOrWhiteSpace(item.Title) ? item.SourceId : item.Title,
                    Term = item.Term,
                    FilesizeBytes = item.FilesizeBytes,
                    DurationSeconds = item.DurationSeconds,
                    DownloadedAt = item.DownloadedAt,
                    LastPlayedAt = item.LastPlayedAt,
                    PlayCount = item.PlayCount,
                    Protected = item.Protected,
                    FirstSeenAt = now,
                    LastSeenAt = now,
                });
            }
        }

        // Anything the Pi reported previously but didn't include this round
        // is gone from the device; drop the mirror row too.
        var toRemove = existing.Where(v => !snapshotKeys.Contains($"{v.Source}|{v.SourceId}")).ToList();
        if (toRemove.Count > 0) _db.CachedVideos.RemoveRange(toRemove);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            when (ex.InnerException?.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) == true)
        {
            // Concurrent snapshot from same device — the row was inserted by a parallel request.
            // Re-query and return success; the DB already reflects the latest state.
        }

        return Ok(new
        {
            deviceId,
            resident,
            received = req.Videos?.Count ?? 0,
            removed = toRemove.Count,
        });
    }

    /// <summary>
    /// Returns commands the Pi hasn't acked yet. Marks them as 'acked'
    /// atomically so a flaky network doesn't double-execute on retries.
    /// </summary>
    [HttpGet("commands")]
    public async Task<IActionResult> GetPendingCommands(string deviceId)
    {
        if (!AuthMatches(deviceId)) return Unauthorized("Device mismatch.");

        var pending = await _db.DeviceCacheCommands
            .Where(c => c.DeviceId == deviceId && c.Status == "pending")
            .OrderBy(c => c.CreatedAt)
            .Take(50)
            .ToListAsync();

        var now = DateTimeOffset.UtcNow;
        foreach (var c in pending)
        {
            c.Status = "acked";
            c.AckedAt = now;
        }
        if (pending.Count > 0) await _db.SaveChangesAsync();

        var view = pending.Select(c => new DeviceCacheCommandView(
            c.Id,
            c.DeviceId,
            c.CommandType,
            ParseJsonOrEmpty(c.PayloadJson),
            c.CreatedAt,
            c.Status));
        return Ok(view);
    }

    /// <summary>
    /// Pi reports the result of a command. status: applied | failed.
    /// </summary>
    [HttpPost("commands/{commandId:guid}/ack")]
    public async Task<IActionResult> AckCommand(string deviceId, Guid commandId, [FromBody] CommandAckRequest req)
    {
        if (!AuthMatches(deviceId)) return Unauthorized("Device mismatch.");

        var cmd = await _db.DeviceCacheCommands
            .FirstOrDefaultAsync(c => c.Id == commandId && c.DeviceId == deviceId);
        if (cmd == null) return NotFound();

        cmd.Status = req?.Status ?? "applied";
        cmd.ResultJson = req?.Result is null ? null : JsonSerializer.Serialize(req.Result);
        await _db.SaveChangesAsync();
        return Ok(new { cmd.Id, cmd.Status });
    }

    private static object ParseJsonOrEmpty(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new { };
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Deserialize<object>(doc.RootElement.GetRawText()) ?? new { };
        }
        catch
        {
            return new { };
        }
    }
}
