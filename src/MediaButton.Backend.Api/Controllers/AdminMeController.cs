using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/me")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminMeController : ControllerBase
{
    private readonly UserAccessService _access;

    public AdminMeController(UserAccessService access) => _access = access;

    [HttpGet]
    public IActionResult Get()
    {
        var roles = User.Claims
            .Where(c => c.Type == "roles" ||
                        c.Type == System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .Distinct()
            .ToList();

        var email = _access.GetEmail(User);
        var oid = _access.GetOid(User);
        var isAdmin = _access.IsAdmin(User);

        return Ok(new
        {
            email,
            oid,
            roles,
            isAdmin
        });
    }
}
