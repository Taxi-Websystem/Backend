using Backend.Data;
using Backend.Hubs;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class UserWhitelistController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<PresenceHub> _presenceHub;

    public UserWhitelistController(ApplicationDbContext context, IHubContext<PresenceHub> presenceHub)
    {
        _context = context;
        _presenceHub = presenceHub;
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
        await BroadcastDashboardDataChanged("whitelist", "create", entry.Id);
        return CreatedAtAction(nameof(GetById), new { id = entry.Id }, entry);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UserWhitelist entry)
    {
        if (id != entry.Id) return BadRequest();

        var existing = await _context.UserWhitelists.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null) return NotFound();

        var currentUserId = GetCurrentUserId();
        var currentRole = GetCurrentRole();

        var isSelf = currentUserId.HasValue && currentUserId.Value == id;

        if (isSelf)
        {
            if (currentRole != UserRole.SuperAdmin || existing.Role != UserRole.SuperAdmin)
                return Forbid();
            if (entry.Role != UserRole.SuperAdmin)
                return BadRequest(new { message = "Змінити власну роль можна лише через налаштування передачі прав SuperAdmin." });

            existing.PhoneNumber = entry.PhoneNumber;
            existing.IsActive = entry.IsActive;
        }
        else
        {
            if (existing.Role == UserRole.SuperAdmin && entry.Role != UserRole.SuperAdmin)
                return Forbid();

            if (existing.Role == UserRole.Manager && entry.Role == UserRole.Driver && currentRole != UserRole.SuperAdmin)
                return Forbid();

            if (!CanManageWhitelistRole(currentRole, entry.Role))
                return Forbid();

            existing.PhoneNumber = entry.PhoneNumber;
            existing.Role = entry.Role;
            existing.IsActive = entry.IsActive;
        }

        existing.CreatedAt = DateTime.SpecifyKind(existing.CreatedAt, DateTimeKind.Utc);

        var linkedProfile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id);
        if (linkedProfile is not null)
        {
            linkedProfile.Role = entry.Role;
            linkedProfile.PhoneNumber = entry.PhoneNumber;
            if (entry.Role == UserRole.Driver)
                linkedProfile.UserStatus = UserStatus.Offline;
        }

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

        await BroadcastDashboardDataChanged("whitelist", "update", existing.Id);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var currentUserId = GetCurrentUserId();
        if (currentUserId == id)
            return Forbid();

        var entry = await _context.UserWhitelists.FindAsync(id);
        if (entry is null) return NotFound();

        var currentRole = GetCurrentRole();

        if (entry.Role == UserRole.SuperAdmin)
            return Forbid();

        if (currentRole == UserRole.Manager && entry.Role != UserRole.Driver)
            return Forbid();

        var linkedProfile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id);
        if (linkedProfile is not null)
            _context.UserProfiles.Remove(linkedProfile);

        _context.UserWhitelists.Remove(entry);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("whitelist", "delete", entry.Id);
        return NoContent();
    }

    private Task BroadcastDashboardDataChanged(string entity, string action, int userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private UserRole GetCurrentRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(role, out var parsedRole))
            return UserRole.Driver;

        return parsedRole;
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var parsedId))
            return null;

        return parsedId;
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
