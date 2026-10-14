using System.Security.Claims;
using MediaButtonBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Services;

/// <summary>
/// Centralises resident-access checks so Relative and Activities users
/// only see the residents they are authorised to access.
/// Admin users (Azure AD "Admin" role) always bypass these checks.
/// </summary>
public class UserAccessService
{
    private readonly AppDbContext _db;

    public UserAccessService(AppDbContext db) => _db = db;

    public bool IsAdmin(ClaimsPrincipal user) =>
        user.Claims.Any(c =>
            (c.Type == "roles" || c.Type == ClaimTypes.Role) &&
            string.Equals(c.Value, "Admin", StringComparison.OrdinalIgnoreCase));

    public string? GetOid(ClaimsPrincipal user) =>
        user.Claims.FirstOrDefault(c =>
            c.Type == "oid" ||
            c.Type == "http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

    public string? GetEmail(ClaimsPrincipal user) =>
        user.Claims.FirstOrDefault(c =>
            c.Type == "preferred_username" ||
            c.Type == "upn" ||
            c.Type == "email" ||
            c.Type == ClaimTypes.Upn ||
            c.Type == ClaimTypes.Email)?.Value;

    /// <summary>
    /// Returns a list of resident keys the caller may access.
    /// Returns null to indicate "all residents" (admin path).
    /// </summary>
    public async Task<List<string>?> GetAllowedResidentsAsync(ClaimsPrincipal user)
    {
        if (IsAdmin(user)) return null;

        var profile = await ResolveProfileAsync(user);
        if (profile == null) return [];

        if (profile.Role == "Activities")
        {
            // Everything, including homes opened since this was set.
            if (profile.AllCareHomes) return null;

            var homes = await HomesForAsync(profile);
            if (homes.Count == 0) return [];

            return await _db.ResidentPlaylists
                .Where(r => r.CareHomeId != null && homes.Contains(r.CareHomeId.Value))
                .Select(r => r.Resident)
                .Distinct()
                .ToListAsync();
        }

        if (profile.Role == "Relative")
        {
            return await _db.ResidentAccessGrants
                .Where(g => g.UserProfileId == profile.Id)
                .Select(g => g.ResidentKey)
                .ToListAsync();
        }

        return [];
    }

    public async Task<bool> CanAccessResidentAsync(ClaimsPrincipal user, string residentKey)
    {
        if (IsAdmin(user)) return true;

        var profile = await ResolveProfileAsync(user);
        if (profile == null) return false;

        if (profile.Role == "Activities")
        {
            if (profile.AllCareHomes) return true;

            var homes = await HomesForAsync(profile);
            if (homes.Count == 0) return false;

            var snapshot = await _db.ResidentPlaylists.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Resident == residentKey);
            return snapshot?.CareHomeId is Guid home && homes.Contains(home);
        }

        if (profile.Role == "Relative")
            return await _db.ResidentAccessGrants
                .AnyAsync(g => g.UserProfileId == profile.Id && g.ResidentKey == residentKey);

        return false;
    }

    /// <summary>
    /// The homes this user may see: the granted ones, plus the single home on
    /// the old column. The old column is still honoured so that a profile
    /// which has not been re-saved since multi-home arrived does not silently
    /// lose the access it already had.
    /// </summary>
    private async Task<HashSet<Guid>> HomesForAsync(Models.UserProfile profile)
    {
        var homes = await _db.UserCareHomeGrants
            .Where(g => g.UserProfileId == profile.Id)
            .Select(g => g.CareHomeId)
            .ToListAsync();

        var set = homes.ToHashSet();
        if (profile.CareHomeId.HasValue)
            set.Add(profile.CareHomeId.Value);
        return set;
    }

    private async Task<Models.UserProfile?> ResolveProfileAsync(ClaimsPrincipal user)
    {
        var oid = GetOid(user);
        var email = GetEmail(user);

        Models.UserProfile? profile = null;

        if (!string.IsNullOrWhiteSpace(oid))
            profile = await _db.UserProfiles.FirstOrDefaultAsync(u => u.AzureAdObjectId == oid);

        if (profile == null && !string.IsNullOrWhiteSpace(email))
            profile = await _db.UserProfiles.FirstOrDefaultAsync(u =>
                u.AzureAdEmail.ToLower() == email.ToLower());

        // Back-fill OID on first match by email so future lookups use the faster OID path
        if (profile != null && !string.IsNullOrWhiteSpace(oid) &&
            string.IsNullOrWhiteSpace(profile.AzureAdObjectId))
        {
            profile.AzureAdObjectId = oid;
            await _db.SaveChangesAsync();
        }

        return profile;
    }
}
