using System.Security.Claims;

using Backend.Data;
using Backend.Hubs;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

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
        if (entry is null)
            return NotFound();

        return entry;
    }

    [HttpPost]
    public async Task<ActionResult<UserWhitelist>> Create(UserWhitelist entry)
    {
        if (!CanManageWhitelistRole(GetActorRole(), entry.Role))
            return Forbid();

        var phone = PhoneNumberValidation.Normalize(entry.PhoneNumber);
        if (phone is null)
            return BadRequest(new { message = PhoneNumberValidation.InvalidFormatMessage });

        if (await PhoneNumberValidation.IsPhoneTakenAsync(_context, phone))
            return BadRequest(new { message = PhoneNumberValidation.DuplicateMessage, code = PhoneNumberValidation.PhoneTakenCode });

        entry.PhoneNumber = phone;
        entry.CreatedAt = DateTime.UtcNow;
        _context.UserWhitelists.Add(entry);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("whitelist", "create", entry.Id);
        return CreatedAtAction(nameof(GetById), new { id = entry.Id }, entry);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UserWhitelist entry)
    {
        if (id != entry.Id)
            return BadRequest();

        var existing = await _context.UserWhitelists.FirstOrDefaultAsync(w => w.Id == id);
        if (existing is null)
            return NotFound();

        var currentUserId = GetCurrentUserId();
        var actorRole = GetActorRole();
        var isSelf = currentUserId.HasValue && currentUserId.Value == id;

        var updateError = isSelf
            ? await TryApplySelfSuperAdminUpdateAsync(existing, entry, actorRole)
            : await TryApplyOtherWhitelistUpdateAsync(existing, entry, actorRole);
        if (updateError is not null)
            return updateError;

        existing.CreatedAt = DateTime.SpecifyKind(existing.CreatedAt, DateTimeKind.Utc);
        await SyncLinkedProfileAsync(id, entry.Role, existing.PhoneNumber);

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
        if (GetCurrentUserId() == id)
            return Forbid();

        var entry = await _context.UserWhitelists.FindAsync(id);
        if (entry is null)
            return NotFound();

        var actorRole = GetActorRole();

        if (entry.Role == UserRole.SuperAdmin)
            return Forbid();

        if (actorRole == UserRole.Manager && entry.Role != UserRole.Driver)
            return Forbid();

        var linkedProfile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == id);
        if (linkedProfile is not null)
            _context.UserProfiles.Remove(linkedProfile);

        _context.UserWhitelists.Remove(entry);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("whitelist", "delete", entry.Id);
        return NoContent();
    }

    private async Task<IActionResult?> TryApplySelfSuperAdminUpdateAsync(
        UserWhitelist existing,
        UserWhitelist entry,
        UserRole actorRole)
    {
        if (actorRole != UserRole.SuperAdmin || existing.Role != UserRole.SuperAdmin)
            return Forbid();

        if (entry.Role != UserRole.SuperAdmin)
            return BadRequest(new { message = "Змінити власну роль можна лише через налаштування передачі прав SuperAdmin." });

        return await ApplyPhoneAndActiveStateAsync(existing, entry);
    }

    private async Task<IActionResult?> TryApplyOtherWhitelistUpdateAsync(
        UserWhitelist existing,
        UserWhitelist entry,
        UserRole actorRole)
    {
        if (existing.Role == UserRole.SuperAdmin && entry.Role != UserRole.SuperAdmin)
            return Forbid();

        if (IsManagerDowngradeForbidden(existing.Role, entry.Role, actorRole))
            return Forbid();

        if (!CanManageWhitelistRole(actorRole, entry.Role))
            return Forbid();

        var phoneError = await ApplyPhoneAndActiveStateAsync(existing, entry);
        if (phoneError is not null)
            return phoneError;

        existing.Role = entry.Role;
        return null;
    }

    private async Task<IActionResult?> ApplyPhoneAndActiveStateAsync(UserWhitelist existing, UserWhitelist entry)
    {
        var (normalizedPhone, phoneError) = await ValidatePhoneForUpdateAsync(
            entry.PhoneNumber,
            existing.PhoneNumber,
            existing.Id);
        if (phoneError is not null)
            return phoneError;

        existing.PhoneNumber = normalizedPhone!;
        existing.IsActive = entry.IsActive;
        return null;
    }

    private async Task SyncLinkedProfileAsync(int whitelistUserId, UserRole role, string phoneNumber)
    {
        var linkedProfile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == whitelistUserId);
        if (linkedProfile is null)
            return;

        linkedProfile.Role = role;
        linkedProfile.PhoneNumber = phoneNumber;
        if (role == UserRole.Driver)
            linkedProfile.UserStatus = UserStatus.Offline;
    }

    private Task BroadcastDashboardDataChanged(string entity, string action, int userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private UserRole GetActorRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(role, out var parsedRole) ? parsedRole : UserRole.Driver;
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var parsedId) ? parsedId : null;
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

    private static bool IsManagerDowngradeForbidden(UserRole existingRole, UserRole nextRole, UserRole actorRole) =>
        existingRole == UserRole.Manager && nextRole == UserRole.Driver && actorRole != UserRole.SuperAdmin;

    private async Task<(string? normalizedPhone, IActionResult? errorResult)> ValidatePhoneForUpdateAsync(
        string phoneNumber,
        string currentPhoneNumber,
        int existingEntryId)
    {
        var normalizedPhone = PhoneNumberValidation.Normalize(phoneNumber);
        if (normalizedPhone is null)
            return (null, BadRequest(new { message = PhoneNumberValidation.InvalidFormatMessage }));

        if (normalizedPhone != currentPhoneNumber
            && await PhoneNumberValidation.IsPhoneTakenAsync(_context, normalizedPhone, existingEntryId))
        {
            return (null, BadRequest(new
            {
                message = PhoneNumberValidation.DuplicateMessage,
                code = PhoneNumberValidation.PhoneTakenCode
            }));
        }

        return (normalizedPhone, null);
    }
}
