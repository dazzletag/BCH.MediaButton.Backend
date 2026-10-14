using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Hands a device the welcome picture a person has approved for its resident.
///
/// Only approved pictures are served. A candidate nobody has looked at must
/// never reach a screen, which is the entire reason generation moved off the
/// device, so there is deliberately no way to ask for one here.
///
/// A device with no approved picture gets 204 and keeps drawing its own plain
/// card. That is the normal state for most residents and is not an error.
/// </summary>
[ApiController]
[Route("api/device/{deviceId}/resident")]
public class DeviceWelcomeImageController(
    AppDbContext db,
    StorageSasService sas,
    IConfiguration config) : ControllerBase
{
    private string PhotosContainer => config["Storage:ContainerPhotos"] ?? "photos";

    [HttpGet("{resident}/welcome-image")]
    public async Task<IActionResult> Get(string deviceId, string resident)
    {
        var authedDevice = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(authedDevice, deviceId, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Device mismatch.");

        var approved = await db.ResidentWelcomeImages
            .Where(w => w.Resident == resident && w.Status == WelcomeImageStatus.Approved)
            .OrderByDescending(w => w.ApprovedAtUtc)
            .FirstOrDefaultAsync();

        if (approved is null)
            return NoContent();

        // Signed afresh on every request. A stored SAS goes stale and the Pi
        // then quietly shows nothing — the same fault that took the photo
        // menu out once already.
        var (uri, expiresAt) = sas.GetReadSasUri(PhotosContainer, approved.BlobPath);

        return Ok(new
        {
            resident,
            id = approved.Id,
            url = uri.AbsoluteUri,
            expiresAt,
            // The device caches the file; this tells it when to fetch again
            // without having to compare images.
            approvedAtUtc = approved.ApprovedAtUtc,
            subject = approved.Subject,
        });
    }
}
