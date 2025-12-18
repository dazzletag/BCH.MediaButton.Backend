using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
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
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public AdminResidentPlaylistsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> ListResidents()
    {
        var residents = await _db.ResidentPlaylists
            .Select(r => r.Resident)
            .OrderBy(r => r)
            .ToListAsync();

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
        var items = ParseManual(snapshot?.ManualPlaylistJson);
        return Ok(new ManualPlaylistResponse(key, items, snapshot?.ManualUpdatedAt, snapshot?.ManualUpdatedBy));
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
