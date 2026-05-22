using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/residents")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminResidentPlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly UserAccessService _access;
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public AdminResidentPlaylistsController(AppDbContext db, UserAccessService access)
    {
        _db = db;
        _access = access;
    }

    [HttpGet]
    public async Task<IActionResult> ListResidents()
    {
        var allowed = await _access.GetAllowedResidentsAsync(User);

        IQueryable<string> query = _db.ResidentPlaylists.Select(r => r.Resident);
        if (allowed != null)
            query = query.Where(r => allowed.Contains(r));

        var residents = await query.OrderBy(r => r).ToListAsync();
        return Ok(residents);
    }

    [HttpGet("{resident}/ai-playlist")]
    public async Task<IActionResult> GetAi(string resident)
    {
        var key = NormalizeResident(resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == key);
        if (snapshot?.AiPlaylistJson == null) return NotFound();

        var payload = JsonSerializer.Deserialize<object>(snapshot.AiPlaylistJson, _jsonOptions);
        return Ok(new
        {
            resident = key,
            payload,
            updatedAtUtc = snapshot.AiUpdatedAt
        });
    }

    [HttpGet("{resident}/manual-playlist")]
    public async Task<ActionResult<ManualPlaylistResponse>> GetManual(string resident)
    {
        var key = NormalizeResident(resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == key);
        var items = ParseManual(snapshot?.ManualPlaylistJson)
            .Select(j => ToClrObject(j))
            .Where(o => o is not null)
            .ToList();
        return Ok(new ManualPlaylistResponse(key, items, snapshot?.ManualUpdatedAt, snapshot?.ManualUpdatedBy, snapshot?.LastPolledAt));
    }

    // Called by the frontend "Send playlist to device" button.
    // The manual playlist (which includes radio/URL items) is already saved before this is called.
    // This endpoint exists to acknowledge the action; the Pi picks up changes via its own device endpoint.
    [HttpPut("{resident}/playlist")]
    public IActionResult AssignPlaylist(string resident, [FromBody] ResidentPlaylistAssignment assignment)
    {
        // No-op for now: playlist delivery happens via the manual-playlist snapshot the Pi polls.
        return NoContent();
    }

    [HttpPut("{resident}/manual-playlist")]
    public async Task<IActionResult> SaveManual(string resident, [FromBody] ManualPlaylistUpdate update)
    {
        if (update?.Items == null) return BadRequest("Items are required.");

        var key = NormalizeResident(resident);
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == key);
        var isNew = snapshot == null;
        snapshot ??= new ResidentPlaylistSnapshot { Resident = key };

        try
        {
            snapshot.ManualPlaylistJson = JsonSerializer.Serialize(update.Items, _jsonOptions);
            snapshot.ManualUpdatedAt = DateTimeOffset.UtcNow;
            snapshot.ManualUpdatedBy = User?.Identity?.Name;

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
            return StatusCode(500, $"Failed to save manual playlist: {ex.Message}");
        }
    }

    private static string NormalizeResident(string resident) =>
        (resident ?? string.Empty).Trim();

    private static IReadOnlyList<JsonElement> ParseManual(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? Array.Empty<JsonElement>()
            : JsonSerializer.Deserialize<List<JsonElement>>(json, _jsonOptions) ?? new List<JsonElement>();

    private static object? ToClrObject(JsonElement element) =>
        element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null
            ? null
            : element.Deserialize<object>(_jsonOptions);
}
