using MediaButtonBackend.Data;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Searching the programme guide from the portal, so staff and relatives can
/// find the programmes a resident always used to watch.
/// </summary>
[ApiController]
[Route("api/admin/epg")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminEpgController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminEpgController(AppDbContext db) => _db = db;

    /// <summary>
    /// Which care homes have a guide, how much of one, and how fresh it is.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var rows = await _db.EpgEvents
            .GroupBy(e => e.CareHomeId)
            .Select(g => new
            {
                careHomeId = g.Key,
                events = g.Count(),
                channels = g.Select(e => e.ChannelName).Distinct().Count(),
                earliest = g.Min(e => e.StartUtc),
                latest = g.Max(e => e.StartUtc),
                lastUpdated = g.Max(e => e.UpdatedAt),
            })
            .ToListAsync();

        var homes = await _db.CareHomes.ToDictionaryAsync(c => c.Id, c => c.Name);
        return Ok(rows.Select(r => new
        {
            r.careHomeId,
            careHome = homes.TryGetValue(r.careHomeId, out var n) ? n : "(unknown)",
            r.events,
            r.channels,
            r.earliest,
            r.latest,
            r.lastUpdated,
        }));
    }

    /// <summary>
    /// Find programmes. Matches title and subtitle; optionally narrowed to a
    /// channel or a time window.
    ///
    /// Results are grouped by serial rather than listed flat: a soap runs five
    /// times a week, and forty near-identical rows would bury everything else.
    /// Each group reports how many showings it has, which is what makes
    /// "follow this one" a sensible thing to offer.
    /// </summary>
    [HttpGet("search")]
    public async Task<IActionResult> Search(
        [FromQuery] Guid careHomeId,
        [FromQuery] string? q,
        [FromQuery] string? channel,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int limit = 100)
    {
        if (careHomeId == Guid.Empty) return BadRequest("careHomeId is required.");
        limit = Math.Clamp(limit, 1, 500);

        var query = _db.EpgEvents.Where(e => e.CareHomeId == careHomeId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(e => EF.Functions.Like(e.Title, $"%{term}%")
                                  || (e.Subtitle != null && EF.Functions.Like(e.Subtitle, $"%{term}%")));
        }
        if (!string.IsNullOrWhiteSpace(channel))
            query = query.Where(e => e.ChannelName == channel);

        // Default to what is still to come: a guide is for planning, and past
        // showings cannot be recorded.
        var lower = from ?? DateTimeOffset.UtcNow;
        query = query.Where(e => e.StopUtc >= lower);
        if (to.HasValue) query = query.Where(e => e.StartUtc <= to.Value);

        var events = await query
            .OrderBy(e => e.StartUtc)
            .Take(limit * 4)
            .Select(e => new
            {
                e.Id,
                e.ChannelName,
                e.ChannelNumber,
                e.Title,
                e.Subtitle,
                e.StartUtc,
                e.StopUtc,
                e.SeriesCrid,
                e.EpisodeCrid,
                e.Genre,
                e.IsHd,
                e.IsSubtitled,
                e.IsAudioDescribed,
            })
            .ToListAsync();

        // Group by serial where the broadcast gives us one; otherwise each
        // programme stands alone.
        var groups = events
            .GroupBy(e => e.SeriesCrid ?? $"one-off:{e.Title}|{e.ChannelName}|{e.StartUtc:o}")
            .Select(g => new
            {
                key = g.Key,
                seriesCrid = g.First().SeriesCrid,
                isSeries = g.First().SeriesCrid != null,
                title = g.First().Title,
                channelName = g.First().ChannelName,
                channelNumber = g.First().ChannelNumber,
                genre = g.First().Genre,
                showings = g.Count(),
                nextStartUtc = g.Min(e => e.StartUtc),
                events = g.OrderBy(e => e.StartUtc).Take(8).ToList(),
            })
            .OrderBy(g => g.nextStartUtc)
            .Take(limit)
            .ToList();

        return Ok(new { careHomeId, count = groups.Count, results = groups });
    }

    /// <summary>The channels this home can actually receive.</summary>
    [HttpGet("channels")]
    public async Task<IActionResult> Channels([FromQuery] Guid careHomeId)
    {
        if (careHomeId == Guid.Empty) return BadRequest("careHomeId is required.");
        var rows = await _db.EpgEvents
            .Where(e => e.CareHomeId == careHomeId)
            .GroupBy(e => new { e.ChannelName, e.ChannelNumber })
            .Select(g => new { g.Key.ChannelName, g.Key.ChannelNumber, events = g.Count() })
            .OrderBy(g => g.ChannelNumber ?? int.MaxValue).ThenBy(g => g.ChannelName)
            .ToListAsync();
        return Ok(rows);
    }
}
