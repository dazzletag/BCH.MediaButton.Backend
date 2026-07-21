using MediaButtonBackend.Data;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Read-only engagement & usage reporting for the portal's Reporting tab.
///
/// The only usage signal the backend holds today is the per-video play
/// counter the Pi maintains and pushes up in its cache snapshot
/// (<see cref="Models.CachedVideo"/>: PlayCount, LastPlayedAt, DownloadedAt).
/// Radio listening, photo slideshows and raw button/session events are not
/// captured server-side yet, so every metric here is derived from cached
/// video plays and library state. Numbers are labelled accordingly in the UI.
///
/// Access is scoped through <see cref="UserAccessService"/>: Admins see every
/// home, Activities users see their home, Relatives see their granted
/// residents — identical to the rest of the /api/admin surface.
/// </summary>
[ApiController]
[Route("api/admin/reporting")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminReportingController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserAccessService _access;

    public AdminReportingController(AppDbContext db, UserAccessService access)
    {
        _db = db;
        _access = access;
    }

    // ~2 Mbps — the same rough assumption the Pi's remote menu and the web
    // Dashboard use to guess a video's length from its byte size.
    private const long BytesPerSecond = 250_000;

    [HttpGet("summary")]
    public async Task<IActionResult> Summary(
        [FromQuery] Guid? careHomeId,
        [FromQuery] string? resident,
        [FromQuery] string? source,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to)
    {
        var isAdmin = _access.IsAdmin(User);
        var allowed = await _access.GetAllowedResidentsAsync(User); // null => all residents

        // Resident -> care home mapping (drives both scoping and grouping).
        var snapshots = await _db.ResidentPlaylists.AsNoTracking()
            .Select(r => new { r.Resident, r.CareHomeId })
            .ToListAsync();
        var homes = await _db.CareHomes.AsNoTracking()
            .Select(c => new { c.Id, c.Name })
            .ToListAsync();
        var homeName = homes.ToDictionary(h => h.Id, h => h.Name);
        var residentHome = new Dictionary<string, Guid?>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in snapshots) residentHome[s.Resident] = s.CareHomeId;

        // Pull the scoped cache rows into memory. The fleet is small (a handful
        // of homes × devices × dozens of videos), so in-memory aggregation is
        // both simpler and safer than translating PlayCount×Duration + date
        // bucketing into SQL.
        var q = _db.CachedVideos.AsNoTracking().AsQueryable();
        if (allowed != null)
        {
            if (allowed.Count == 0) return Ok(EmptySummary(isAdmin));
            q = q.Where(v => allowed.Contains(v.Resident));
        }
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(v => v.Source == source);

        var raw = await q.Select(v => new
        {
            v.Resident,
            v.DeviceId,
            v.Source,
            v.SourceId,
            v.Title,
            v.Term,
            v.PlayCount,
            v.DurationSeconds,
            v.FilesizeBytes,
            v.DownloadedAt,
            v.LastPlayedAt,
            v.LastSeenAt,
        }).ToListAsync();

        // Attach care home + apply the care-home / resident filters in memory.
        var residentFilter = string.IsNullOrWhiteSpace(resident) ? null : resident.Trim();
        var rows = new List<Row>(raw.Count);
        foreach (var v in raw)
        {
            residentHome.TryGetValue(v.Resident, out var chId);
            if (careHomeId.HasValue && chId != careHomeId.Value) continue;
            if (residentFilter != null &&
                !string.Equals(v.Resident, residentFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var duration = v.DurationSeconds.GetValueOrDefault() > 0
                ? v.DurationSeconds!.Value
                : (v.FilesizeBytes.GetValueOrDefault() > 0
                    ? (int)(v.FilesizeBytes!.Value / BytesPerSecond)
                    : 0);

            rows.Add(new Row
            {
                Resident = v.Resident,
                CareHomeId = chId,
                CareHomeName = chId.HasValue && homeName.TryGetValue(chId.Value, out var n) ? n : null,
                DeviceId = v.DeviceId,
                Source = string.IsNullOrWhiteSpace(v.Source) ? "yt" : v.Source,
                SourceId = v.SourceId,
                Title = v.Title,
                Term = string.IsNullOrWhiteSpace(v.Term) ? null : v.Term!.Trim(),
                PlayCount = Math.Max(0, v.PlayCount),
                DurationSeconds = duration,
                FilesizeBytes = v.FilesizeBytes.GetValueOrDefault(),
                DownloadedAt = v.DownloadedAt,
                LastPlayedAt = v.LastPlayedAt,
                LastSeenAt = v.LastSeenAt,
            });
        }

        var now = DateTimeOffset.UtcNow;

        // Engagement window: a row counts toward "plays in period" when its
        // last-played timestamp falls inside the range. This is the truest
        // proxy we have — the Pi only reports a cumulative counter plus the
        // most-recent play time, not a dated event stream.
        bool InWindow(Row r) =>
            r.LastPlayedAt.HasValue &&
            (from is null || r.LastPlayedAt >= from) &&
            (to is null || r.LastPlayedAt <= to);

        var engaged = rows.Where(InWindow).ToList();

        var totalPlays = engaged.Sum(r => (long)r.PlayCount);
        var watchSeconds = engaged.Sum(r => (long)r.PlayCount * r.DurationSeconds);
        var activeResidents = engaged.Where(r => r.PlayCount > 0)
            .Select(r => r.Resident).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Library health is reported all-time (within the non-date filters) —
        // a resident's whole collection, not just what was touched this period.
        var distinctVideos = rows
            .GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        var uniqueVideos = distinctVideos.Count;
        var libraryBytes = rows.Sum(r => r.FilesizeBytes);
        var totalResidents = rows.Select(r => r.Resident)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        // Devices: derived from the cache rows that are actually reporting.
        var deviceGroups = rows.GroupBy(r => r.DeviceId)
            .Select(g => new { DeviceId = g.Key, LastSeen = g.Max(r => r.LastSeenAt) })
            .ToList();
        var deviceCount = deviceGroups.Count;
        var onlineDevices = deviceGroups.Count(d => (now - d.LastSeen) <= TimeSpan.FromMinutes(15));
        var activeDevices = deviceGroups.Count(d => (now - d.LastSeen) <= TimeSpan.FromHours(24));

        var neverPlayed = distinctVideos.Count(g => g.Sum(r => r.PlayCount) == 0);

        // Per-care-home comparison (all homes in scope).
        var careHomeRows = rows
            .GroupBy(r => r.CareHomeId)
            .Select(g =>
            {
                var eng = g.Where(InWindow).ToList();
                return new CareHomeStat(
                    CareHomeId: g.Key,
                    Name: g.Key.HasValue && homeName.TryGetValue(g.Key.Value, out var n) ? n : "Unassigned",
                    Residents: g.Select(r => r.Resident).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    ActiveResidents: eng.Where(r => r.PlayCount > 0).Select(r => r.Resident).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    Devices: g.Select(r => r.DeviceId).Distinct().Count(),
                    Plays: eng.Sum(r => (long)r.PlayCount),
                    WatchSeconds: eng.Sum(r => (long)r.PlayCount * r.DurationSeconds),
                    Videos: g.GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase).Count(),
                    LibraryBytes: g.Sum(r => r.FilesizeBytes),
                    LastActive: g.Where(r => r.LastPlayedAt.HasValue).Select(r => r.LastPlayedAt).DefaultIfEmpty(null).Max());
            })
            .OrderByDescending(h => h.Plays)
            .ToList();

        // Per-resident breakdown.
        var residentRows = rows
            .GroupBy(r => r.Resident, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var eng = g.Where(InWindow).ToList();
                var topTerm = eng.Where(r => r.Term != null)
                    .GroupBy(r => r.Term!, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(t => t.Sum(r => r.PlayCount))
                    .Select(t => t.Key)
                    .FirstOrDefault();
                return new ResidentStat(
                    Resident: g.Key,
                    CareHomeName: g.Select(r => r.CareHomeName).FirstOrDefault(n => n != null),
                    Plays: eng.Sum(r => (long)r.PlayCount),
                    WatchSeconds: eng.Sum(r => (long)r.PlayCount * r.DurationSeconds),
                    Videos: g.GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase).Count(),
                    LibraryBytes: g.Sum(r => r.FilesizeBytes),
                    Devices: g.Select(r => r.DeviceId).Distinct().Count(),
                    LastActive: g.Where(r => r.LastPlayedAt.HasValue).Select(r => r.LastPlayedAt).DefaultIfEmpty(null).Max(),
                    TopTerm: topTerm);
            })
            .OrderByDescending(r => r.Plays)
            .ThenByDescending(r => r.Videos)
            .ToList();

        // Top videos leaderboard — collapse the same video cached on several
        // devices into one line, summing plays.
        var topVideos = engaged
            .GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new VideoStat(
                    Title: first.Title,
                    Source: first.Source,
                    SourceId: first.SourceId,
                    Term: g.Select(r => r.Term).FirstOrDefault(t => t != null),
                    Resident: first.Resident,
                    Plays: g.Sum(r => (long)r.PlayCount),
                    WatchSeconds: g.Sum(r => (long)r.PlayCount * r.DurationSeconds),
                    LastPlayedAt: g.Select(r => r.LastPlayedAt).Max());
            })
            .Where(v => v.Plays > 0)
            .OrderByDescending(v => v.Plays)
            .Take(15)
            .ToList();

        // Top search terms (what themes residents actually engage with).
        var topTerms = engaged
            .Where(r => r.Term != null && r.PlayCount > 0)
            .GroupBy(r => r.Term!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TermStat(
                Term: g.Key,
                Plays: g.Sum(r => (long)r.PlayCount),
                Videos: g.GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(t => t.Plays)
            .Take(12)
            .ToList();

        // Source split (YouTube vs family uploads vs other).
        var sourceSplit = engaged
            .GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SourceStat(
                Source: g.Key,
                Plays: g.Sum(r => (long)r.PlayCount),
                Videos: g.GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(s => s.Plays)
            .ToList();

        // Library growth — items added per day by download date, over the last
        // 90 days (or the selected download window if a range is set).
        var growthFrom = from ?? now.AddDays(-90);
        var growthTo = to ?? now;
        var libraryGrowth = rows
            .Where(r => r.DownloadedAt >= growthFrom && r.DownloadedAt <= growthTo)
            .GroupBy(r => r.DownloadedAt.UtcDateTime.Date)
            .Select(g => new GrowthPoint(
                Date: g.Key.ToString("yyyy-MM-dd"),
                Added: g.GroupBy(r => $"{r.Source}|{r.SourceId}", StringComparer.OrdinalIgnoreCase).Count(),
                Bytes: g.Sum(r => r.FilesizeBytes)))
            .OrderBy(p => p.Date)
            .ToList();

        // Play recency — how fresh is engagement, by distinct video.
        int last7 = 0, last30 = 0, older = 0, never = 0;
        foreach (var g in distinctVideos)
        {
            var last = g.Select(r => r.LastPlayedAt).Max();
            if (last is null) { never++; continue; }
            var age = now - last.Value;
            if (age <= TimeSpan.FromDays(7)) last7++;
            else if (age <= TimeSpan.FromDays(30)) last30++;
            else older++;
        }

        var kpis = new Kpis(
            TotalPlays: totalPlays,
            EstimatedWatchSeconds: watchSeconds,
            UniqueVideos: uniqueVideos,
            LibraryBytes: libraryBytes,
            ActiveResidents: activeResidents,
            TotalResidents: totalResidents,
            OnlineDevices: onlineDevices,
            ActiveDevices: activeDevices,
            DeviceCount: deviceCount,
            NeverPlayedVideos: neverPlayed,
            AvgPlaysPerActiveResident: activeResidents > 0 ? Math.Round((double)totalPlays / activeResidents, 1) : 0);

        return Ok(new SummaryResponse(
            Scope: new ScopeInfo(isAdmin, totalResidents, careHomeRows.Count, deviceCount, now),
            Kpis: kpis,
            CareHomes: careHomeRows,
            Residents: residentRows,
            TopVideos: topVideos,
            TopTerms: topTerms,
            SourceSplit: sourceSplit,
            LibraryGrowth: libraryGrowth,
            PlayRecency: new RecencyBuckets(last7, last30, older, never)));
    }

    private static SummaryResponse EmptySummary(bool isAdmin) => new(
        Scope: new ScopeInfo(isAdmin, 0, 0, 0, DateTimeOffset.UtcNow),
        Kpis: new Kpis(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        CareHomes: new List<CareHomeStat>(),
        Residents: new List<ResidentStat>(),
        TopVideos: new List<VideoStat>(),
        TopTerms: new List<TermStat>(),
        SourceSplit: new List<SourceStat>(),
        LibraryGrowth: new List<GrowthPoint>(),
        PlayRecency: new RecencyBuckets(0, 0, 0, 0));

    private sealed class Row
    {
        public string Resident = "";
        public Guid? CareHomeId;
        public string? CareHomeName;
        public string DeviceId = "";
        public string Source = "yt";
        public string SourceId = "";
        public string Title = "";
        public string? Term;
        public int PlayCount;
        public int DurationSeconds;
        public long FilesizeBytes;
        public DateTimeOffset DownloadedAt;
        public DateTimeOffset? LastPlayedAt;
        public DateTimeOffset LastSeenAt;
    }
}

// --- response DTOs ---

public record SummaryResponse(
    ScopeInfo Scope,
    Kpis Kpis,
    IReadOnlyList<CareHomeStat> CareHomes,
    IReadOnlyList<ResidentStat> Residents,
    IReadOnlyList<VideoStat> TopVideos,
    IReadOnlyList<TermStat> TopTerms,
    IReadOnlyList<SourceStat> SourceSplit,
    IReadOnlyList<GrowthPoint> LibraryGrowth,
    RecencyBuckets PlayRecency);

public record ScopeInfo(bool IsAdmin, int ResidentCount, int CareHomeCount, int DeviceCount, DateTimeOffset GeneratedAtUtc);

public record Kpis(
    long TotalPlays,
    long EstimatedWatchSeconds,
    int UniqueVideos,
    long LibraryBytes,
    int ActiveResidents,
    int TotalResidents,
    int OnlineDevices,
    int ActiveDevices,
    int DeviceCount,
    int NeverPlayedVideos,
    double AvgPlaysPerActiveResident);

public record CareHomeStat(
    Guid? CareHomeId, string Name, int Residents, int ActiveResidents, int Devices,
    long Plays, long WatchSeconds, int Videos, long LibraryBytes, DateTimeOffset? LastActive);

public record ResidentStat(
    string Resident, string? CareHomeName, long Plays, long WatchSeconds, int Videos,
    long LibraryBytes, int Devices, DateTimeOffset? LastActive, string? TopTerm);

public record VideoStat(
    string Title, string Source, string SourceId, string? Term, string Resident,
    long Plays, long WatchSeconds, DateTimeOffset? LastPlayedAt);

public record TermStat(string Term, long Plays, int Videos);

public record SourceStat(string Source, long Plays, int Videos);

public record GrowthPoint(string Date, int Added, long Bytes);

public record RecencyBuckets(int Last7, int Last30, int Older, int Never);
