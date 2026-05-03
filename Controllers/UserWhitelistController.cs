using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class UserWhitelistController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public UserWhitelistController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserWhitelist>>> GetAll()
    {
        return await _context.UserWhitelists.ToListAsync();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserWhitelist>> GetById(int id)
    {
        var entry = await _context.UserWhitelists.FindAsync(id);
        if (entry is null) return NotFound();
        return entry;
    }

    [HttpPost]
    public async Task<ActionResult<UserWhitelist>> Create(UserWhitelist entry)
    {
        var currentRole = GetCurrentRole();
        if (!CanManageWhitelistRole(currentRole, entry.Role))
            return Forbid();

        entry.CreatedAt = DateTime.UtcNow;
        _context.UserWhitelists.Add(entry);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = entry.Id }, entry);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UserWhitelist entry)
    {
        if (id != entry.Id) return BadRequest();

        var currentRole = GetCurrentRole();
        if (!CanManageWhitelistRole(currentRole, entry.Role))
            return Forbid();

        entry.CreatedAt = DateTime.SpecifyKind(entry.CreatedAt, DateTimeKind.Utc);
        _context.Entry(entry).State = EntityState.Modified;
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserWhitelists.AnyAsync(e => e.Id == id))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> Delete(int id)
    {
        var entry = await _context.UserWhitelists.FindAsync(id);
        if (entry is null) return NotFound();

        _context.UserWhitelists.Remove(entry);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private UserRole GetCurrentRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(role, out var parsedRole))
            return UserRole.Driver;

        return parsedRole;
    }

    private static bool CanManageWhitelistRole(UserRole actorRole, UserRole targetRole)
    {
        if (targetRole == UserRole.SuperAdmin)
            return false;

        return actorRole switch
        {
            UserRole.SuperAdmin => targetRole is UserRole.Driver or UserRole.Manager,
            UserRole.Manager => targetRole == UserRole.Driver,
            _ => false
        };
    }
}
