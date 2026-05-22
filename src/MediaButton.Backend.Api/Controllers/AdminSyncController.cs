using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/sync")]
[Authorize(Policy = "AdminOnly")]
public class AdminSyncController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MobizioService _mobizio;

    public AdminSyncController(AppDbContext db, MobizioService mobizio)
    {
        _db = db;
        _mobizio = mobizio;
    }

    /// <summary>
    /// Diagnostic: returns raw top-level field names from the first Mobizio case,
    /// so we can confirm the branch field name.
    /// </summary>
    [HttpGet("mobizio-case-structure")]
    public async Task<IActionResult> CaseStructure()
    {
        var sample = await _mobizio.GetCaseStructureSampleAsync();
        return Ok(sample);
    }

    /// <summary>
    /// Reads all active cases from Mobizio, auto-creates CareHome records for each
    /// distinct branch name found, and sets CareHomeId on each ResidentPlaylistSnapshot
    /// that matches a resident name. Returns a summary of what was created/updated.
    /// </summary>
    [HttpPost("care-homes-from-mobizio")]
    public async Task<IActionResult> SyncCareHomes()
    {
        var diag = new List<string>();

        // 1. Fetch all active residents from Mobizio (includes branch)
        List<MobizioResidentSummary> residents;
        try
        {
            var list = await _mobizio.ListActiveResidentsAsync();
            residents = list.ToList();
            diag.Add($"Mobizio returned {residents.Count} active case(s)");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Mobizio fetch failed: {ex.Message}", diag });
        }

        // 2. Log what branch values we actually found (helps diagnose field name)
        var branchSample = residents
            .GroupBy(r => r.Branch ?? "(null)")
            .OrderByDescending(g => g.Count())
            .Take(10)
            .Select(g => new { branch = g.Key, count = g.Count() })
            .ToList();
        diag.Add($"Branch values found: {System.Text.Json.JsonSerializer.Serialize(branchSample)}");

        var residentsWithBranch = residents.Where(r => !string.IsNullOrWhiteSpace(r.Branch)).ToList();
        if (residentsWithBranch.Count == 0)
        {
            diag.Add("No branch data found on cases — check case structure via GET /api/admin/sync/mobizio-case-structure");
            return Ok(new { homesCreated = 0, residentsAssigned = 0, diag });
        }

        // 3. Ensure a CareHome row exists for each distinct branch name
        var distinctBranches = residentsWithBranch
            .Select(r => r.Branch!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existingHomes = await _db.CareHomes.ToListAsync();
        var homeByName = existingHomes.ToDictionary(h => h.Name, h => h, StringComparer.OrdinalIgnoreCase);

        int homesCreated = 0;
        foreach (var branch in distinctBranches)
        {
            if (!homeByName.ContainsKey(branch))
            {
                var home = new CareHome { Name = branch };
                _db.CareHomes.Add(home);
                homeByName[branch] = home;
                homesCreated++;
                diag.Add($"Created care home: '{branch}'");
            }
        }
        if (homesCreated > 0)
            await _db.SaveChangesAsync();

        // 4. Match each Mobizio resident to a ResidentPlaylistSnapshot and set CareHomeId
        var snapshots = await _db.ResidentPlaylists.ToListAsync();
        var snapshotByName = snapshots.ToDictionary(s => s.Resident, StringComparer.OrdinalIgnoreCase);

        int residentsAssigned = 0;
        int residentsNotFound = 0;

        foreach (var r in residentsWithBranch)
        {
            if (!snapshotByName.TryGetValue(r.Name, out var snapshot))
            {
                residentsNotFound++;
                continue;
            }

            var home = homeByName[r.Branch!.Trim()];
            if (snapshot.CareHomeId == home.Id) continue;

            snapshot.CareHomeId = home.Id;
            residentsAssigned++;
        }

        if (residentsAssigned > 0)
            await _db.SaveChangesAsync();

        diag.Add($"Care homes created: {homesCreated}");
        diag.Add($"Residents assigned to a home: {residentsAssigned}");
        diag.Add($"Mobizio residents with no matching portal snapshot: {residentsNotFound}");

        return Ok(new { homesCreated, residentsAssigned, residentsNotFound, diag });
    }
}
