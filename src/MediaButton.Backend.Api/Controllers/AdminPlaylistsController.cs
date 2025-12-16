using MediaButtonBackend.Api.Models;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/playlists")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminPlaylistsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly StorageSasService _sasService;
    private readonly IConfiguration _config;

    public AdminPlaylistsController(AppDbContext db, StorageSasService sasService, IConfiguration config)
    {
        _db = db;
        _sasService = sasService;
        _config = config;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlaylistResponse>>> GetAll()
    {
        var list = await _db.Playlists
            .Include(p => p.Items)
            .ThenInclude(i => i.Media)
            .OrderBy(p => p.Name)
            .ToListAsync();

        var mapped = list.Select(MapPlaylist).ToList();
        return Ok(mapped);
    }

    [HttpPost]
    public async Task<ActionResult<PlaylistResponse>> Create([FromBody] PlaylistCreateRequest request)
    {
        var playlist = new Playlist
        {
            Name = request.Name
        };

        foreach (var item in request.Items.OrderBy(i => i.Order))
        {
            playlist.Items.Add(new PlaylistItem
            {
                MediaId = item.MediaId,
                Order = item.Order,
                DurationSeconds = item.DurationSeconds
            });
        }

        _db.Playlists.Add(playlist);
        await _db.SaveChangesAsync();

        // Reload with media for response
        await _db.Entry(playlist).Collection(p => p.Items).LoadAsync();
        foreach (var i in playlist.Items)
        {
            await _db.Entry(i).Reference(x => x.Media).LoadAsync();
        }

        return Ok(MapPlaylist(playlist));
    }

    [HttpPut("{id:guid}")]
    public async Task<ActionResult<PlaylistResponse>> Update(Guid id, [FromBody] PlaylistCreateRequest request)
    {
        var playlist = await _db.Playlists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();

        playlist.Name = request.Name;

        // Replace items
        _db.PlaylistItems.RemoveRange(playlist.Items);
        playlist.Items.Clear();
        foreach (var item in request.Items.OrderBy(i => i.Order))
        {
            playlist.Items.Add(new PlaylistItem
            {
                MediaId = item.MediaId,
                Order = item.Order,
                DurationSeconds = item.DurationSeconds
            });
        }

        await _db.SaveChangesAsync();

        await _db.Entry(playlist).Collection(p => p.Items).LoadAsync();
        foreach (var i in playlist.Items)
        {
            await _db.Entry(i).Reference(x => x.Media).LoadAsync();
        }

        return Ok(MapPlaylist(playlist));
    }

    [HttpPut("/api/admin/devices/{deviceId}/playlist")]
    public async Task<IActionResult> AssignPlaylistToDevice(string deviceId, [FromBody] Guid playlistId)
    {
        var device = await _db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
        if (device == null)
        {
            device = new Device { DeviceId = deviceId };
            _db.Devices.Add(device);
        }

        var exists = await _db.Playlists.AnyAsync(p => p.Id == playlistId);
        if (!exists) return NotFound("Playlist not found.");

        device.PlaylistId = playlistId;
        await _db.SaveChangesAsync();
        return NoContent();
    }

    private PlaylistResponse MapPlaylist(Playlist playlist)
    {
        var items = new List<PlaylistItemResponse>();
        foreach (var item in playlist.Items.OrderBy(i => i.Order))
        {
            if (item.Media == null) continue;
            var container = item.Media.Type == MediaType.Photo
                ? _config["Storage:ContainerPhotos"] ?? "photos"
                : _config["Storage:ContainerVideos"] ?? "videos";
            var (uri, _) = _sasService.GetReadSasUri(container, item.Media.BlobPath);

            items.Add(new PlaylistItemResponse(
                item.Media.Id,
                item.Media.Name,
                item.Media.Type,
                uri,
                item.Order,
                item.DurationSeconds));
        }

        return new PlaylistResponse(playlist.Id, playlist.Name, items);
    }
}
