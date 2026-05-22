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

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var profiles = await _db.UserProfiles
            .Include(u => u.CareHome)
            .Include(u => u.AccessGrants)
            .OrderBy(u => u.DisplayName ?? u.AzureAdEmail)
            .Select(u => new
            {
                u.Id,
                u.AzureAdEmail,
                u.DisplayName,
                u.Role,
                u.CareHomeId,
                careHomeName = u.CareHome != null ? u.CareHome.Name : null,
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
            CareHomeId = req.Role == "Activities" ? req.CareHomeId : null
        };

        _db.UserProfiles.Add(profile);
        await _db.SaveChangesAsync();
        return Ok(profile);
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
        profile.CareHomeId = req.Role == "Activities" ? req.CareHomeId : null;

        await _db.SaveChangesAsync();
        return Ok(profile);
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
    Guid? CareHomeId);

public record AddResidentGrantRequest(string ResidentKey);
