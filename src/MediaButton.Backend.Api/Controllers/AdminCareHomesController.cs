using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/care-homes")]
[Authorize(Policy = "AdminOnly")]
public class AdminCareHomesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AdminCareHomesController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List() =>
        Ok(await _db.CareHomes.OrderBy(c => c.Name).ToListAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCareHomeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name)) return BadRequest("Name is required.");
        var home = new CareHome { Name = req.Name.Trim() };
        _db.CareHomes.Add(home);
        await _db.SaveChangesAsync();
        return Ok(home);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateCareHomeRequest req)
    {
        var home = await _db.CareHomes.FindAsync(id);
        if (home == null) return NotFound();
        home.Name = req.Name.Trim();
        await _db.SaveChangesAsync();
        return Ok(home);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var home = await _db.CareHomes.FindAsync(id);
        if (home == null) return NotFound();
        _db.CareHomes.Remove(home);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}

public record CreateCareHomeRequest(string Name);
