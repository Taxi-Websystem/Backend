using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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
    public async Task<ActionResult<IEnumerable<DriverListItemDto>>> GetAll()
    {
        var driverRows = await (from profile in _context.UserProfiles
                                join whitelist in _context.UserWhitelists
                                    on profile.UserId equals whitelist.Id
                                where profile.Role == UserRole.Driver
                                      && whitelist.Role == UserRole.Driver
                                      && whitelist.IsActive
                                      && !string.IsNullOrWhiteSpace(profile.Name)
                                      && profile.Name != profile.PhoneNumber
                                join ride in _context.Rides.Where(r => r.Status == RideStatus.Completed)
                                    on profile.Id equals ride.DriverId into ridesGroup
                                select new DriverListItemDto
                                {
                                    Id = profile.Id,
                                    UserId = profile.UserId,
                                    PhoneNumber = profile.PhoneNumber,
                                    Name = profile.Name,
                                    CarMake = profile.CarMake,
                                    CarModel = profile.CarModel,
                                    CarColor = profile.CarColor,
                                    LicensePlate = profile.LicensePlate,
                                    Role = profile.Role,
                                    UserStatus = profile.UserStatus,
                                    TripCount = ridesGroup.Count(),
                                    AverageRating = ridesGroup
                                        .Where(r => r.Rating.HasValue)
                                        .Select(r => r.Rating)
                                        .Average()
                                })
            .ToListAsync();

        return driverRows;
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

        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarMake))
            return BadRequest(new { message = "Марка авто: лише латинські літери, цифри, пробіл та дефіс." });
        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarModel))
            return BadRequest(new { message = "Модель авто: лише латинські літери, цифри, пробіл та дефіс." });
        if (!CarFieldValidation.IsValidCarColorUa(driver.CarColor))
            return BadRequest(new { message = "Колір авто: лише українською (кирилиця) та дефіс." });

        driver.UserId = whitelistEntry.Id;
        driver.PhoneNumber = whitelistEntry.PhoneNumber;
        // Temporary rule: newly created drivers start as Online.
        driver.UserStatus = UserStatus.Online;

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

        var actorRole = GetActorRole();
        var targetRole = driver.Role;

        if (targetRole != UserRole.Driver && targetRole != UserRole.Manager)
            return BadRequest(new { message = "Дозволені лише ролі Водій або Менеджер." });

        if (targetRole == UserRole.Manager && actorRole != UserRole.SuperAdmin)
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Призначити менеджера з картки водія може лише адміністратор." });

        if (targetRole == UserRole.Manager)
        {
            whitelistEntry.Role = UserRole.Manager;
            existingDriver.Role = UserRole.Manager;
        }
        else
        {
            whitelistEntry.Role = UserRole.Driver;
            existingDriver.Role = UserRole.Driver;
        }

        if (normalizedPhone != whitelistEntry.PhoneNumber)
        {
            var phoneTaken = await _context.UserWhitelists
                .AnyAsync(w => w.PhoneNumber == normalizedPhone && w.Id != whitelistEntry.Id);
            if (phoneTaken)
                return BadRequest(new { message = "Номер телефону вже зайнятий." });
        }

        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarMake))
            return BadRequest(new { message = "Марка авто: лише латинські літери, цифри, пробіл та дефіс." });
        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarModel))
            return BadRequest(new { message = "Модель авто: лише латинські літери, цифри, пробіл та дефіс." });
        if (!CarFieldValidation.IsValidCarColorUa(driver.CarColor))
            return BadRequest(new { message = "Колір авто: лише українською (кирилиця) та дефіс." });

        whitelistEntry.PhoneNumber = normalizedPhone;
        existingDriver.PhoneNumber = normalizedPhone;
        existingDriver.Name = driver.Name;
        existingDriver.CarMake = driver.CarMake;
        existingDriver.CarModel = driver.CarModel;
        existingDriver.CarColor = driver.CarColor;
        existingDriver.LicensePlate = driver.LicensePlate;
        existingDriver.UserStatus = targetRole == UserRole.Manager ? UserStatus.Offline : driver.UserStatus;
        existingDriver.UserId = whitelistEntry.Id;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.UserProfiles.AnyAsync(d => d.Id == id))
                return NotFound();
            throw;
        }

        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool removeFromWhitelist = false)
    {
        var driver = await _context.UserProfiles.FindAsync(id);
        if (driver is null || driver.Role != UserRole.Driver)
            return NotFound();

        var whitelist = await _context.UserWhitelists.FindAsync(driver.UserId);
        if (whitelist is null)
            return NotFound();

        if (whitelist.Role != UserRole.Driver)
            return BadRequest(new { message = "Цей профіль не є активним водієм у whitelist." });

        _context.UserProfiles.Remove(driver);

        if (removeFromWhitelist)
            _context.UserWhitelists.Remove(whitelist);

        await _context.SaveChangesAsync();
        return NoContent();
    }

    private UserRole GetActorRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(role, out var parsed) ? parsed : UserRole.Driver;
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
