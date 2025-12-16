using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
}
