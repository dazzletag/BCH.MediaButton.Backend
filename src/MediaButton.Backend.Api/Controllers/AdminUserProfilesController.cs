using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = "AdminOnly")]
public class AdminUserProfilesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminUserProfilesController(AppDbContext db) => _db = db;

    /// <summary>
    /// Set which homes a profile may see. Relatives get none: their access is
    /// per-resident, and leaving a stale home on the record would be a quiet
    /// way to widen it later.
    /// </summary>
    private void ApplyHomes(UserProfile profile, UpsertUserProfileRequest req)
    {
        var existing = _db.UserCareHomeGrants.Where(g => g.UserProfileId == profile.Id);
        _db.UserCareHomeGrants.RemoveRange(existing);

        if (req.Role != "Activities")
        {
            profile.CareHomeId = null;
            profile.AllCareHomes = false;
            return;
        }

        profile.AllCareHomes = req.AllCareHomes;

        var wanted = (req.CareHomeIds ?? [])
            .Concat(req.CareHomeId is Guid one ? [one] : Array.Empty<Guid>())
            .Distinct()
            .ToList();

        // "All homes" is a standing instruction, not a list, so individual
        // grants are not kept alongside it — they would go stale and mislead.
        if (req.AllCareHomes)
        {
            profile.CareHomeId = null;
            return;
        }

        foreach (var home in wanted)
            _db.UserCareHomeGrants.Add(new UserCareHomeGrant
            {
                UserProfileId = profile.Id,
                CareHomeId = home,
            });

        // Keep the old column meaningful when there is exactly one home, so
        // anything still reading it behaves as before.
        profile.CareHomeId = wanted.Count == 1 ? wanted[0] : null;
    }

    /// <summary>
    /// The homes a profile may see: the granted ones, plus the single home on
    /// the old column, which profiles saved before multi-home still carry.
    /// Done over materialised entities, never inside a query.
    /// </summary>
    private static List<(Guid Id, string Name)> Homes(UserProfile u)
    {
        var homes = u.CareHomeGrants
            .Where(g => g.CareHome != null)
            .Select(g => (g.CareHomeId, g.CareHome!.Name))
            .ToList();

        if (u.CareHomeId is Guid legacy && homes.All(h => h.Item1 != legacy))
            homes.Add((legacy, u.CareHome?.Name ?? "(home)"));

        return homes;
    }

    private async Task<object?> DescribeAsync(Guid id)
    {
        var u = await _db.UserProfiles
            .Where(p => p.Id == id)
            .Include(p => p.CareHome)
            .Include(p => p.CareHomeGrants).ThenInclude(g => g.CareHome)
            .AsNoTracking()
            .FirstOrDefaultAsync();
        if (u is null) return null;

        return new
        {
            u.Id, u.AzureAdEmail, u.DisplayName, u.Role, u.CareHomeId, u.AllCareHomes,
            careHomeIds = Homes(u).Select(h => h.Id).ToList(),
            careHomeNames = Homes(u).Select(h => h.Name).ToList(),
        };
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        // Fetched, then shaped. Folding the old single home into the list has
        // to happen in memory: expressed inside the query it is not something
        // EF can turn into SQL, and it fails at runtime rather than at build,
        // which is how it reached production.
        var rows = await _db.UserProfiles
            .Include(u => u.CareHome)
            .Include(u => u.AccessGrants)
            .Include(u => u.CareHomeGrants).ThenInclude(g => g.CareHome)
            .OrderBy(u => u.DisplayName ?? u.AzureAdEmail)
            .AsNoTracking()
            .ToListAsync();

        var profiles = rows.Select(u => new
        {
            u.Id,
            u.AzureAdEmail,
            u.DisplayName,
            u.Role,
            u.CareHomeId,
            u.AllCareHomes,
            careHomeName = u.CareHome?.Name,
            careHomeIds = Homes(u).Select(h => h.Id).ToList(),
            careHomeNames = Homes(u).Select(h => h.Name).ToList(),
            residents = u.AccessGrants.Select(g => g.ResidentKey).ToList()
        }).ToList();

        return Ok(profiles);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertUserProfileRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.AzureAdEmail))
            return BadRequest("AzureAdEmail is required.");
        if (req.Role != "Activities" && req.Role != "Relative")
            return BadRequest("Role must be 'Activities' or 'Relative'.");

        var profile = new UserProfile
        {
            AzureAdEmail = req.AzureAdEmail.Trim().ToLowerInvariant(),
            DisplayName = req.DisplayName?.Trim(),
            Role = req.Role,
        };

        _db.UserProfiles.Add(profile);
        ApplyHomes(profile, req);
        await _db.SaveChangesAsync();
        return Ok(await DescribeAsync(profile.Id));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertUserProfileRequest req)
    {
        var profile = await _db.UserProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        if (req.Role != "Activities" && req.Role != "Relative")
            return BadRequest("Role must be 'Activities' or 'Relative'.");

        profile.AzureAdEmail = req.AzureAdEmail.Trim().ToLowerInvariant();
        profile.DisplayName = req.DisplayName?.Trim();
        profile.Role = req.Role;
        ApplyHomes(profile, req);

        await _db.SaveChangesAsync();
        return Ok(await DescribeAsync(profile.Id));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var profile = await _db.UserProfiles.FindAsync(id);
        if (profile == null) return NotFound();
        _db.UserProfiles.Remove(profile);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // --- Resident access grants for a Relative user ---

    [HttpGet("{id:guid}/residents")]
    public async Task<IActionResult> ListResidentGrants(Guid id)
    {
        var grants = await _db.ResidentAccessGrants
            .Where(g => g.UserProfileId == id)
            .OrderBy(g => g.ResidentKey)
            .ToListAsync();
        return Ok(grants);
    }

    [HttpPost("{id:guid}/residents")]
    public async Task<IActionResult> AddResidentGrant(Guid id, [FromBody] AddResidentGrantRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ResidentKey))
            return BadRequest("ResidentKey is required.");

        var profile = await _db.UserProfiles.FindAsync(id);
        if (profile == null) return NotFound();

        var exists = await _db.ResidentAccessGrants
            .AnyAsync(g => g.UserProfileId == id && g.ResidentKey == req.ResidentKey);
        if (exists) return Conflict("Already granted.");

        var grant = new ResidentAccessGrant
        {
            UserProfileId = id,
            ResidentKey = req.ResidentKey,
            GrantedBy = User.Identity?.Name
        };
        _db.ResidentAccessGrants.Add(grant);
        await _db.SaveChangesAsync();
        return Ok(grant);
    }

    [HttpDelete("{id:guid}/residents/{residentKey}")]
    public async Task<IActionResult> RemoveResidentGrant(Guid id, string residentKey)
    {
        var grant = await _db.ResidentAccessGrants
            .FirstOrDefaultAsync(g => g.UserProfileId == id && g.ResidentKey == residentKey);
        if (grant == null) return NotFound();
        _db.ResidentAccessGrants.Remove(grant);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record UpsertUserProfileRequest(
    string AzureAdEmail,
    string? DisplayName,
    string Role,
    /// <summary>Kept so older callers still work; folded into CareHomeIds.</summary>
    Guid? CareHomeId,
    IReadOnlyList<Guid>? CareHomeIds = null,
    bool AllCareHomes = false);

public record AddResidentGrantRequest(string ResidentKey);
