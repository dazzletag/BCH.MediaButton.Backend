using System.Security.Cryptography;
using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/devices")]
[Authorize(Policy = "AdminOnly")]
public class AdminDevicesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminDevicesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<DeviceListItem>>> GetAll()
    {
        var devices = await _db.Devices.ToListAsync();
        var playlistIds = devices.Where(d => d.PlaylistId.HasValue).Select(d => d.PlaylistId!.Value).Distinct().ToList();
        var playlists = await _db.Playlists
            .Where(p => playlistIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        var result = devices.Select(d => new DeviceListItem(
            d.DeviceId,
            d.DisplayName,
            d.PlaylistId,
            d.PlaylistId.HasValue && playlists.TryGetValue(d.PlaylistId.Value, out var name) ? name : null,
            d.DeviceKey,
            d.ResidentKey,
            d.MobizioId));

        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<DeviceListItem>> Create([FromBody] DeviceCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId))
            return BadRequest("DeviceId is required.");

        var existing = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == request.DeviceId);
        if (existing != null)
            return Conflict($"Device '{request.DeviceId}' already exists.");

        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

        var device = new Device
        {
            DeviceId = request.DeviceId,
            DisplayName = request.DisplayName,
            DeviceKey = key
        };

        _db.Devices.Add(device);
        await _db.SaveChangesAsync();

        return Ok(new DeviceListItem(device.DeviceId, device.DisplayName, null, null, device.DeviceKey, null, null));
    }

    [HttpPatch("{deviceId}")]
    public async Task<IActionResult> Rename(string deviceId, [FromBody] DeviceRenameRequest request)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return NotFound();

        device.DisplayName = request.DisplayName;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{deviceId}")]
    public async Task<IActionResult> Delete(string deviceId)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null) return NotFound();

        _db.Devices.Remove(device);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
