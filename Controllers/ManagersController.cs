using Backend.Data;
using Backend.Hubs;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class ManagersController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IHubContext<PresenceHub> _presenceHub;

    public ManagersController(
        ApplicationDbContext context,
        IUserSettingsService userSettingsService,
        IHubContext<PresenceHub> presenceHub)
    {
        _context = context;
        _userSettingsService = userSettingsService;
        _presenceHub = presenceHub;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ManagerListItemDto>>> GetAll()
    {
        var managerRows = await (from whitelist in _context.UserWhitelists
                                 where whitelist.Role == UserRole.Manager || whitelist.Role == UserRole.SuperAdmin
                                 join profile in _context.UserProfiles on whitelist.Id equals profile.UserId
                                 where !string.IsNullOrWhiteSpace(profile.Name)
                                       && profile.Name != profile.PhoneNumber
                                 select new { whitelist, profile })
            .ToListAsync();

        var hasChanges = false;
        foreach (var row in managerRows)
        {
            var settings = await _userSettingsService.GetOrCreateAsync(row.profile.UserId);
            if (!settings.IsAutoStatusEnabled)
            {
                // Для Manager/SuperAdmin автостатус завжди активний.
                settings.IsAutoStatusEnabled = true;
                hasChanges = true;
            }

            if (row.profile.UserStatus == UserStatus.InRide)
            {
                continue;
            }

            var shouldBeOnline = PresenceHub.HasActiveConnections(row.profile.UserId);
            var targetStatus = shouldBeOnline ? UserStatus.Online : UserStatus.Offline;
            if (row.profile.UserStatus != targetStatus)
            {
                row.profile.UserStatus = targetStatus;
                hasChanges = true;
            }
        }

        if (hasChanges)
        {
            await _context.SaveChangesAsync();
        }

        var managers = managerRows
            .Select(row => new ManagerListItemDto
            {
                Id = row.profile.Id,
                UserId = row.whitelist.Id,
                PhoneNumber = row.whitelist.PhoneNumber,
                Name = row.profile.Name,
                Role = row.whitelist.Role,
                Status = row.profile.UserStatus == UserStatus.Online
                    ? UserOnlineStatus.Online
                    : UserOnlineStatus.Offline
            })
            .OrderBy(item => item.Role)
            .ThenBy(item => item.UserId)
            .ToList();

        return managers;
    }

    [HttpPost]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<ActionResult<UserProfile>> Create(CreateManagerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Ім'я обов'язкове." });

        var phone = NormalizePhone(request.PhoneNumber);
        if (phone is null)
            return BadRequest(new { message = "Некоректний формат телефону. Використовуйте +380XXXXXXXXX." });

        var whitelistEntry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == phone);

        if (whitelistEntry is null)
        {
            whitelistEntry = new UserWhitelist
            {
                PhoneNumber = phone,
                Role = UserRole.Manager,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            _context.UserWhitelists.Add(whitelistEntry);
            await _context.SaveChangesAsync();
        }
        else
        {
            if (!whitelistEntry.IsActive)
                return BadRequest(new { message = "Whitelist запис неактивний." });

            if (whitelistEntry.Role == UserRole.SuperAdmin)
                return BadRequest(new { message = "Не можна створити менеджера для номера SuperAdmin." });

            whitelistEntry.Role = UserRole.Manager;
            _context.UserWhitelists.Update(whitelistEntry);
            await _context.SaveChangesAsync();
        }

        if (await _context.UserProfiles.AnyAsync(p => p.UserId == whitelistEntry.Id))
            return BadRequest(new { message = "Профіль для цього номера вже існує." });

        var manager = new UserProfile
        {
            UserId = whitelistEntry.Id,
            PhoneNumber = whitelistEntry.PhoneNumber,
            Name = request.Name.Trim(),
            Role = UserRole.Manager
        };

        _context.UserProfiles.Add(manager);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("managers", "create", manager.UserId);

        return CreatedAtAction(nameof(GetById), new { id = manager.Id }, manager);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfile>> GetById(int id)
    {
        var manager = await _context.UserProfiles.FindAsync(id);
        if (manager is null || (manager.Role != UserRole.Manager && manager.Role != UserRole.SuperAdmin))
            return NotFound();

        if (!await IsActiveWhitelistEntry(manager.UserId))
            return NotFound();

        return manager;
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UpdateManagerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Ім'я обов'язкове." });

        var existing = await _context.UserProfiles.FindAsync(id);
        if (existing is null || (existing.Role != UserRole.Manager && existing.Role != UserRole.SuperAdmin))
            return NotFound();

        var currentUserId = GetCurrentUserId();
        var isSelfEdit = currentUserId.HasValue && currentUserId.Value == existing.UserId;

        if (existing.Role == UserRole.SuperAdmin && !isSelfEdit)
            return Forbid();

        if (!await IsActiveWhitelistEntry(existing.UserId))
            return BadRequest(new { message = "Користувач має бути активним у whitelist." });

        var whitelistEntry = await _context.UserWhitelists.FirstOrDefaultAsync(w => w.Id == existing.UserId);
        if (whitelistEntry is null)
            return BadRequest(new { message = "Whitelist запис користувача не знайдено." });

        var actorRole = User.FindFirstValue(ClaimTypes.Role);
        var isSuperAdminActor = string.Equals(actorRole, UserRole.SuperAdmin.ToString(), StringComparison.Ordinal);

        if (!isSuperAdminActor)
        {
            if (!isSelfEdit)
                return Forbid();

            if (request.Role.HasValue && request.Role.Value != existing.Role)
                return Forbid();

            if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
                return BadRequest(new { message = "Менеджер може змінювати лише власне ім'я." });
        }

        if (request.Role.HasValue && request.Role.Value != existing.Role)
        {
            if (isSelfEdit)
                return BadRequest(new { message = "Не можна змінити власну роль цим запитом." });

            if (request.Role.Value == UserRole.SuperAdmin)
                return BadRequest(new { message = "Роль SuperAdmin призначається лише через передачу прав." });

            if (existing.Role != UserRole.Manager || request.Role.Value != UserRole.Driver)
                return BadRequest(new { message = "Дозволено лише зниження менеджера до водія." });

            whitelistEntry.Role = UserRole.Driver;
            existing.Role = UserRole.Driver;
            existing.UserStatus = UserStatus.Offline;
        }

        existing.Name = request.Name.Trim();
        _context.UserProfiles.Update(existing);

        if (isSuperAdminActor && !string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            var normalizedPhone = NormalizePhone(request.PhoneNumber);
            if (normalizedPhone is null)
                return BadRequest(new { message = "Некоректний формат телефону. Використовуйте +380XXXXXXXXX." });

            whitelistEntry.PhoneNumber = normalizedPhone;
            existing.PhoneNumber = normalizedPhone;
        }

        _context.UserWhitelists.Update(whitelistEntry);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserProfiles.AnyAsync(m => m.Id == id))
                return NotFound();
            throw;
        }

        await BroadcastDashboardDataChanged("managers", "update", existing.UserId);
        return NoContent();
    }

    [HttpDelete("{id}")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool removeFromWhitelist = false)
    {
        var profile = await _context.UserProfiles.FindAsync(id);
        if (profile is null)
            return NotFound();

        var whitelist = await _context.UserWhitelists.FindAsync(profile.UserId);
        if (whitelist is null)
            return NotFound();

        if (whitelist.Role != UserRole.Manager && whitelist.Role != UserRole.SuperAdmin)
            return NotFound();

        if (whitelist.Role == UserRole.SuperAdmin)
            return BadRequest(new { message = "Неможливо видалити обліковий запис адміністратора (SuperAdmin)." });

        _context.UserProfiles.Remove(profile);

        if (removeFromWhitelist)
            _context.UserWhitelists.Remove(whitelist);

        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("managers", "delete", profile.UserId);
        return NoContent();
    }

    private Task BroadcastDashboardDataChanged(string entity, string action, int userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private Task<bool> IsActiveWhitelistEntry(int userId)
    {
        return _context.UserWhitelists.AnyAsync(w => w.Id == userId && w.IsActive);
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var parsedId))
            return null;

        return parsedId;
    }

    private static string? NormalizePhone(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var normalized = phone.Trim();
        if (!normalized.StartsWith("+380"))
            return null;

        return normalized.Length == 13 && normalized.Skip(1).All(char.IsDigit)
            ? normalized
            : null;
    }
}

public class ManagerListItemDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public UserRole Role { get; set; }
    public UserOnlineStatus Status { get; set; }
}

public record CreateManagerRequest(string PhoneNumber, string Name);
public record UpdateManagerRequest(string Name, string? PhoneNumber, UserRole? Role);
