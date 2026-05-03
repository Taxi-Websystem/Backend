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
public class ManagersController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public ManagersController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<ManagerListItemDto>>> GetAll()
    {
        var managers = await (from whitelist in _context.UserWhitelists
                              where whitelist.Role == UserRole.Manager || whitelist.Role == UserRole.SuperAdmin
                              join profile in _context.UserProfiles on whitelist.Id equals profile.UserId
                              where !string.IsNullOrWhiteSpace(profile.Name)
                                    && profile.Name != profile.PhoneNumber
                              select new ManagerListItemDto
                              {
                                  Id = profile.Id,
                                  UserId = whitelist.Id,
                                  PhoneNumber = whitelist.PhoneNumber,
                                  Name = profile.Name,
                                  Role = whitelist.Role,
                                  Status = whitelist.IsActive ? UserOnlineStatus.Online : UserOnlineStatus.Offline
                              })
            .OrderBy(item => item.Role)
            .ThenBy(item => item.UserId)
            .ToListAsync();

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
    [Authorize(Policy = "SuperAdminOnly")]
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

        if (!string.IsNullOrWhiteSpace(request.PhoneNumber))
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

        /* GET /managers показує Role з whitelist; профіль інколи розходиться з whitelist → не покладатися лише на profile.Role */
        if (whitelist.Role != UserRole.Manager && whitelist.Role != UserRole.SuperAdmin)
            return NotFound();

        if (whitelist.Role == UserRole.SuperAdmin)
            return BadRequest(new { message = "Неможливо видалити обліковий запис адміністратора (SuperAdmin)." });

        _context.UserProfiles.Remove(profile);

        if (removeFromWhitelist)
            _context.UserWhitelists.Remove(whitelist);

        await _context.SaveChangesAsync();
        return NoContent();
    }

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
