using System.Security.Claims;
using MediaButtonBackend.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Services;

/// <summary>
/// Marker requirement used by the "AdminOrRelative" policy. The matching
/// handler accepts a caller as a legitimate app user if they either carry
/// an AD app-role claim of Admin/Relative/Activities, or have a matching
/// row in UserProfile (added via the Users tab in the portal). The DB
/// path means a fresh Activities user doesn't need a separate enterprise
/// app role assignment to sign in.
/// </summary>
public sealed class AppUserRequirement : IAuthorizationRequirement { }

public sealed class AppUserHandler : AuthorizationHandler<AppUserRequirement>
{
    private readonly AppDbContext _db;
    private readonly UserAccessService _access;

    public AppUserHandler(AppDbContext db, UserAccessService access)
    {
        _db = db;
        _access = access;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AppUserRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true) return;

        var roleValues = context.User.Claims
            .Where(c =>
                string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, ClaimTypes.Role, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (roleValues.Contains("Admin") ||
            roleValues.Contains("Relative") ||
            roleValues.Contains("Activities"))
        {
            context.Succeed(requirement);
            return;
        }

        var oid = _access.GetOid(context.User);
        var email = _access.GetEmail(context.User);

        var hasProfile = false;
        if (!string.IsNullOrWhiteSpace(oid))
            hasProfile = await _db.UserProfiles
                .AnyAsync(u => u.AzureAdObjectId == oid);

        if (!hasProfile && !string.IsNullOrWhiteSpace(email))
            hasProfile = await _db.UserProfiles
                .AnyAsync(u => u.AzureAdEmail.ToLower() == email.ToLower());

        if (hasProfile)
            context.Succeed(requirement);
    }
}
