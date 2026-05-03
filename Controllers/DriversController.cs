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
public class DriversController : ControllerBase
{
    private readonly ApplicationDbContext _context;

    public DriversController(ApplicationDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<UserProfile>>> GetAll()
    {
        var drivers = await (from profile in _context.UserProfiles
                             join whitelist in _context.UserWhitelists
                                 on profile.UserId equals whitelist.Id
                             where profile.Role == UserRole.Driver
                                   && whitelist.Role == UserRole.Driver
                                   && whitelist.IsActive
                                   && !string.IsNullOrWhiteSpace(profile.Name)
                                   && profile.Name != profile.PhoneNumber
                             select profile)
            .ToListAsync();

        return drivers;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfile>> GetById(int id)
    {
        var driver = await _context.UserProfiles.FindAsync(id);
        if (driver is null || driver.Role != UserRole.Driver)
            return NotFound();

        if (!await _context.UserWhitelists.AnyAsync(w =>
                w.Id == driver.UserId &&
                w.IsActive &&
                w.Role == UserRole.Driver))
            return NotFound();

        return driver;
    }

    [HttpPost]
    public async Task<ActionResult<UserProfile>> Create(UserProfile driver)
    {
        driver.Role = UserRole.Driver;
        var phone = NormalizePhone(driver.PhoneNumber);
        if (phone is null)
            return BadRequest(new { message = "Некоректний формат телефону. Використовуйте +380XXXXXXXXX." });

        var whitelistEntry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == phone);
        if (whitelistEntry is null)
        {
            whitelistEntry = new UserWhitelist
            {
                PhoneNumber = phone,
                Role = UserRole.Driver,
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

            if (whitelistEntry.Role is UserRole.Manager or UserRole.SuperAdmin)
                return BadRequest(new { message = "Для цього номера вже призначена адміністративна роль." });

            whitelistEntry.Role = UserRole.Driver;
            _context.UserWhitelists.Update(whitelistEntry);
            await _context.SaveChangesAsync();
        }

        if (await _context.UserProfiles.AnyAsync(p => p.UserId == whitelistEntry.Id))
            return BadRequest(new { message = "Профіль для цього номера вже існує." });

        driver.UserId = whitelistEntry.Id;
        driver.PhoneNumber = whitelistEntry.PhoneNumber;
        driver.DriverStatus = DriverStatus.Offline;

        _context.UserProfiles.Add(driver);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(GetById), new { id = driver.Id }, driver);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, UserProfile driver)
    {
        if (id != driver.Id)
            return BadRequest();

        var existingDriver = await _context.UserProfiles.FindAsync(id);
        if (existingDriver is null || existingDriver.Role != UserRole.Driver)
            return NotFound();

        var normalizedPhone = NormalizePhone(driver.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { message = "Некоректний формат телефону. Використовуйте +380XXXXXXXXX." });

        var whitelistEntry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.Id == existingDriver.UserId);

        if (whitelistEntry is null || !whitelistEntry.IsActive || whitelistEntry.Role != UserRole.Driver)
            return BadRequest(new { message = "Активний запис водія у whitelist не знайдено." });

        if (normalizedPhone != whitelistEntry.PhoneNumber)
        {
            var phoneTaken = await _context.UserWhitelists
                .AnyAsync(w => w.PhoneNumber == normalizedPhone && w.Id != whitelistEntry.Id);
            if (phoneTaken)
                return BadRequest(new { message = "Номер телефону вже зайнятий." });
        }

        whitelistEntry.PhoneNumber = normalizedPhone;
        existingDriver.PhoneNumber = normalizedPhone;
        existingDriver.Name = driver.Name;
        existingDriver.CarMake = driver.CarMake;
        existingDriver.CarModel = driver.CarModel;
        existingDriver.LicensePlate = driver.LicensePlate;
        existingDriver.DriverStatus = driver.DriverStatus;
        existingDriver.Role = UserRole.Driver;
        existingDriver.UserId = whitelistEntry.Id;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserProfiles.AnyAsync(d => d.Id == id && d.Role == UserRole.Driver))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var driver = await _context.UserProfiles.FindAsync(id);
        if (driver is null || driver.Role != UserRole.Driver)
            return NotFound();

        _context.UserProfiles.Remove(driver);
        await _context.SaveChangesAsync();
        return NoContent();
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
