using System.Text.Json;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/residents/{residentId}/care-plans/{section}")]
[Authorize(Policy = "AdminOrRelative")]
public class CarePlansController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ILogger<CarePlansController> _log;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public CarePlansController(AppDbContext db, ILogger<CarePlansController> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Returns the current signed-off version for a care plan section.
    /// </summary>
    [HttpGet("current")]
    public async Task<IActionResult> GetCurrent(string residentId, string section)
    {
        var version = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.IsCurrent)
            .FirstOrDefaultAsync();

        if (version is null)
            return NotFound(new { error = "No current care plan found for this section." });

        return Ok(MapToResponse(version));
    }

    /// <summary>
    /// Returns the version history list for a care plan section.
    /// </summary>
    [HttpGet("versions")]
    public async Task<IActionResult> GetVersions(string residentId, string section)
    {
        var versions = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new CarePlanVersionSummary(
                v.Id,
                v.VersionNumber,
                v.Status,
                v.CreatedByName,
                v.CreatedAt))
            .ToListAsync();

        return Ok(versions);
    }

    /// <summary>
    /// Returns a specific version (read-only).
    /// </summary>
    [HttpGet("versions/{versionId:guid}")]
    public async Task<IActionResult> GetVersion(string residentId, string section, Guid versionId)
    {
        var version = await _db.CarePlanVersions
            .Where(v => v.Id == versionId && v.ResidentId == residentId && v.Section == section)
            .FirstOrDefaultAsync();

        if (version is null)
            return NotFound(new { error = "Version not found." });

        return Ok(MapToResponse(version));
    }

    /// <summary>
    /// Creates a new draft from the current signed-off version.
    /// </summary>
    [HttpPost("draft")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> CreateDraft(string residentId, string section)
    {
        var existing = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.Status == "draft")
            .FirstOrDefaultAsync();

        if (existing is not null)
            return Ok(MapToResponse(existing));

        var current = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.IsCurrent)
            .FirstOrDefaultAsync();

        var nextVersion = current is not null ? current.VersionNumber + 1 : 1;
        var carePlanId = current?.CarePlanId ?? Guid.NewGuid();

        var userId = User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? User.FindFirst("oid")?.Value;
        var userName = User.Identity?.Name ?? User.FindFirst("name")?.Value;

        var draft = new CarePlanVersion
        {
            CarePlanId = carePlanId,
            ResidentId = residentId,
            Section = section,
            VersionNumber = nextVersion,
            Status = "draft",
            AssessmentDataJson = current?.AssessmentDataJson,
            CareActionsDataJson = current?.CareActionsDataJson,
            CreatedById = userId,
            CreatedByName = userName,
            IsCurrent = false
        };

        _db.CarePlanVersions.Add(draft);
        await _db.SaveChangesAsync();

        _log.LogInformation("[CarePlan] Draft v{Version} created for {Resident}/{Section}", nextVersion, residentId, section);
        return Ok(MapToResponse(draft));
    }

    /// <summary>
    /// Saves draft changes (assessment and care actions only).
    /// </summary>
    [HttpPut("draft")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SaveDraft(string residentId, string section, [FromBody] CarePlanDraftRequest request)
    {
        var draft = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.Status == "draft")
            .FirstOrDefaultAsync();

        if (draft is null)
            return NotFound(new { error = "No draft exists. Create one first." });

        if (request.AssessmentData is not null)
            draft.AssessmentDataJson = JsonSerializer.Serialize(request.AssessmentData, JsonOpts);

        if (request.CareActionsData is not null)
            draft.CareActionsDataJson = JsonSerializer.Serialize(request.CareActionsData, JsonOpts);

        await _db.SaveChangesAsync();

        return Ok(MapToResponse(draft));
    }

    /// <summary>
    /// Validates sign-off fields, promotes draft to signed_off, sets it as current.
    /// </summary>
    [HttpPost("sign-off")]
    [Authorize(Policy = "AdminOnly")]
    public async Task<IActionResult> SignOff(string residentId, string section, [FromBody] CarePlanSignOffRequest request)
    {
        var draft = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.Status == "draft")
            .FirstOrDefaultAsync();

        if (draft is null)
            return NotFound(new { error = "No draft to sign off." });

        if (string.IsNullOrWhiteSpace(request.ResidentInvolved))
            return BadRequest(new { error = "Resident/representative involvement is required." });

        if (string.IsNullOrWhiteSpace(request.NextReviewDate))
            return BadRequest(new { error = "Next review date is required." });

        // Update assessment/care actions if provided in the sign-off request
        if (request.AssessmentData is not null)
            draft.AssessmentDataJson = JsonSerializer.Serialize(request.AssessmentData, JsonOpts);
        if (request.CareActionsData is not null)
            draft.CareActionsDataJson = JsonSerializer.Serialize(request.CareActionsData, JsonOpts);

        var userId = User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value
                  ?? User.FindFirst("oid")?.Value;
        var userName = User.Identity?.Name ?? User.FindFirst("name")?.Value;
        var userRole = User.FindFirst("roles")?.Value
                    ?? User.FindFirst(System.Security.Claims.ClaimTypes.Role)?.Value
                    ?? "Staff";

        var signOff = new SignOffData(
            CompletedById: userId,
            CompletedByName: userName,
            CompletedByRole: userRole,
            CompletedAt: DateTimeOffset.UtcNow,
            ResidentInvolved: request.ResidentInvolved,
            NextReviewDate: request.NextReviewDate,
            Notes: request.Notes);

        draft.SignOffJson = JsonSerializer.Serialize(signOff, JsonOpts);
        draft.Status = "signed_off";

        // Clear IsCurrent on all previous versions for this care plan
        var previousVersions = await _db.CarePlanVersions
            .Where(v => v.ResidentId == residentId && v.Section == section && v.IsCurrent)
            .ToListAsync();

        foreach (var prev in previousVersions)
            prev.IsCurrent = false;

        draft.IsCurrent = true;
        await _db.SaveChangesAsync();

        _log.LogInformation("[CarePlan] v{Version} signed off for {Resident}/{Section}", draft.VersionNumber, residentId, section);
        return Ok(MapToResponse(draft));
    }

    private static CarePlanVersionResponse MapToResponse(CarePlanVersion v)
    {
        List<AssessmentField>? assessment = null;
        if (!string.IsNullOrEmpty(v.AssessmentDataJson))
            assessment = JsonSerializer.Deserialize<List<AssessmentField>>(v.AssessmentDataJson, JsonOpts);

        CareActionsData? careActions = null;
        if (!string.IsNullOrEmpty(v.CareActionsDataJson))
            careActions = JsonSerializer.Deserialize<CareActionsData>(v.CareActionsDataJson, JsonOpts);

        SignOffData? signOff = null;
        if (!string.IsNullOrEmpty(v.SignOffJson))
            signOff = JsonSerializer.Deserialize<SignOffData>(v.SignOffJson, JsonOpts);

        return new CarePlanVersionResponse(
            v.Id, v.CarePlanId, v.ResidentId, v.Section,
            v.VersionNumber, v.Status,
            assessment, careActions, signOff,
            v.CreatedById, v.CreatedByName, v.CreatedAt, v.IsCurrent);
    }
}
