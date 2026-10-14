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

    private async Task<object?> DescribeAsync(Guid id) =>
        await _db.UserProfiles
            .Where(u => u.Id == id)
            .Include(u => u.CareHomeGrants).ThenInclude(g => g.CareHome)
            .Select(u => new
            {
                u.Id, u.AzureAdEmail, u.DisplayName, u.Role, u.CareHomeId, u.AllCareHomes,
                careHomeIds = u.CareHomeGrants.Select(g => g.CareHomeId).ToList(),
                careHomeNames = u.CareHomeGrants.Where(g => g.CareHome != null)
                                                .Select(g => g.CareHome!.Name).ToList(),
            })
            .FirstOrDefaultAsync();

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var profiles = await _db.UserProfiles
            .Include(u => u.CareHome)
            .Include(u => u.AccessGrants)
            .Include(u => u.CareHomeGrants).ThenInclude(g => g.CareHome)
            .OrderBy(u => u.DisplayName ?? u.AzureAdEmail)
            .Select(u => new
            {
                u.Id,
                u.AzureAdEmail,
                u.DisplayName,
                u.Role,
                u.CareHomeId,
                u.AllCareHomes,
                careHomeName = u.CareHome != null ? u.CareHome.Name : null,
                // The old single home is folded in, so a profile saved before
                // multi-home existed still lists the home it actually has.
                careHomeIds = u.CareHomeGrants.Select(g => g.CareHomeId)
                    .Concat(u.CareHomeId != null ? new[] { u.CareHomeId.Value } : Array.Empty<Guid>())
                    .Distinct().ToList(),
                careHomeNames = u.CareHomeGrants
                    .Where(g => g.CareHome != null)
                    .Select(g => g.CareHome!.Name).ToList(),
                residents = u.AccessGrants.Select(g => g.ResidentKey).ToList()
            })
            .ToListAsync();

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
