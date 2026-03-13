using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/device/{deviceId}")]
public class DeviceController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly StorageSasService _sasService;
    private readonly IConfiguration _config;

    public DeviceController(AppDbContext db, StorageSasService sasService, IConfiguration config)
    {
        _db = db;
        _sasService = sasService;
        _config = config;
    }

    [HttpGet("ping")]
    public IActionResult Ping(string deviceId)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;

        return Ok(new
        {
            routeDeviceId = deviceId,
            authenticatedAs = authedDeviceId,
            ok = authedDeviceId == deviceId
        });
    }

    [HttpGet("playlist")]
    public async Task<IActionResult> GetPlaylist(string deviceId)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Device mismatch.");
        }

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device?.PlaylistId == null)
        {
            return Ok(new DevicePlaylistResponse(deviceId, null, Array.Empty<PlaylistItemResponse>(), device?.ConfigJson));
        }

        var playlist = await _db.Playlists
            .Include(p => p.Items)
            .ThenInclude(i => i.Media)
            .FirstOrDefaultAsync(p => p.Id == device.PlaylistId);

        if (playlist == null)
        {
            return Ok(new DevicePlaylistResponse(deviceId, null, Array.Empty<PlaylistItemResponse>(), device.ConfigJson));
        }

        var items = new List<PlaylistItemResponse>();
        foreach (var item in playlist.Items.OrderBy(i => i.Order))
        {
            if (item.Media == null) continue;
            var container = item.Media.Type == MediaType.Photo
                ? _config["Storage:ContainerPhotos"] ?? "photos"
                : _config["Storage:ContainerVideos"] ?? "videos";
            var (uri, _) = _sasService.GetReadSasUri(container, item.Media.BlobPath);
            items.Add(new PlaylistItemResponse(item.Media.Id, item.Media.Name, item.Media.Type, uri, item.Order, item.DurationSeconds));
        }

        return Ok(new DevicePlaylistResponse(deviceId, playlist.Name, items, device.ConfigJson));
    }

    [HttpGet("config")]
    public async Task<IActionResult> GetConfig(string deviceId)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return Unauthorized("Device mismatch.");
        }

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        return Ok(new DeviceConfigResponse(deviceId, device?.ConfigJson));
    }

    /// <summary>
    /// Returns all active residents from Mobizio care planning.
    /// Used by the Pi setup wizard to populate the resident selection list.
    /// </summary>
    [HttpGet("setup/mobizio-residents")]
    public async Task<IActionResult> ListMobizioResidents(
        string deviceId,
        [FromServices] MobizioService mobizio)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Device mismatch.");

        try
        {
            var residents = await mobizio.ListActiveResidentsAsync();
            return Ok(residents);
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Failed to fetch residents from Mobizio: {ex.Message}");
        }
    }

    /// <summary>
    /// Called by the Pi setup wizard after configuration to register the assigned resident.
    /// Creates the resident record in the database if it doesn't exist, and stores the
    /// Mobizio case ID on both the device and the resident snapshot.
    /// </summary>
    [HttpPost("setup/register-resident")]
    public async Task<IActionResult> RegisterResident(
        string deviceId,
        [FromBody] RegisterResidentRequest request)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Device mismatch.");

        if (string.IsNullOrWhiteSpace(request.ResidentName))
            return BadRequest("residentName is required.");

        var residentKey = request.ResidentName.Trim();

        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return NotFound("Device not found.");

        device.ResidentKey = residentKey;
        device.MobizioId = request.CaseId?.Trim();

        // Ensure resident exists in the playlist snapshots table
        var snapshot = await _db.ResidentPlaylists.FirstOrDefaultAsync(r => r.Resident == residentKey);
        if (snapshot == null)
        {
            snapshot = new ResidentPlaylistSnapshot { Resident = residentKey };
            _db.ResidentPlaylists.Add(snapshot);
        }
        if (!string.IsNullOrWhiteSpace(request.CaseId))
            snapshot.MobizioId = request.CaseId.Trim();

        await _db.SaveChangesAsync();
        return Ok(new { residentKey, mobizioId = device.MobizioId });
    }

    /// <summary>
    /// Returns the "This is Me" profile for a resident from Mobizio care planning.
    /// The Pi uses this to personalise AI playlist generation without needing a local Excel file.
    /// </summary>
    [HttpGet("resident-profile")]
    public async Task<IActionResult> GetResidentProfile(
        string deviceId,
        [FromQuery] string residentKey,
        [FromServices] MobizioService mobizio)
    {
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;
        if (!string.Equals(deviceId, authedDeviceId, StringComparison.OrdinalIgnoreCase))
            return Unauthorized("Device mismatch.");

        if (string.IsNullOrWhiteSpace(residentKey))
            return BadRequest("residentKey is required.");

        try
        {
            var profile = await mobizio.GetResidentProfileAsync(residentKey);
            if (profile is null)
                return NotFound($"No Mobizio profile found for '{residentKey}'.");
            return Ok(profile);
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Failed to fetch profile from Mobizio: {ex.Message}");
        }
    }
}
