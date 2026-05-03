using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class ManagersController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public ManagersController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserProfile>>> GetAll()
    {
        var managers = await (from profile in _context.UserProfiles
                              join whitelist in _context.UserWhitelists
                                  on profile.UserId equals whitelist.Id
                              where profile.Role == UserRole.Manager && whitelist.IsActive
                              select profile)
            .ToListAsync();

        return managers;
    }

    [HttpPost]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<ActionResult<UserProfile>> Create(UserProfile manager)
    {
        manager.Role = UserRole.Manager;
        if (!await IsActiveWhitelistEntry(manager.UserId))
            return BadRequest(new { message = "Користувач має бути активним у whitelist." });

        _context.UserProfiles.Add(manager);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetById), new { id = manager.Id }, manager);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfile>> GetById(int id)
    {
        var manager = await _context.UserProfiles.FindAsync(id);
        if (manager is null || manager.Role != UserRole.Manager)
            return NotFound();

        if (!await IsActiveWhitelistEntry(manager.UserId))
            return NotFound();

        return manager;
    }

    [HttpPut("{id}")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> Update(int id, UserProfile manager)
    {
        if (id != manager.Id)
            return BadRequest();

        if (!await IsActiveWhitelistEntry(manager.UserId))
            return BadRequest(new { message = "Користувач має бути активним у whitelist." });

        manager.Role = UserRole.Manager;
        _context.Entry(manager).State = EntityState.Modified;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserProfiles.AnyAsync(m => m.Id == id && m.Role == UserRole.Manager))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> Delete(int id)
    {
        var manager = await _context.UserProfiles.FindAsync(id);
        if (manager is null || manager.Role != UserRole.Manager)
            return NotFound();

        _context.UserProfiles.Remove(manager);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private Task<bool> IsActiveWhitelistEntry(int userId)
    {
        return _context.UserWhitelists.AnyAsync(w => w.Id == userId && w.IsActive);
    }
}
