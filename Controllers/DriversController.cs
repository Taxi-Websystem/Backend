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
public class DriversController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<PresenceHub> _presenceHub;

    public DriversController(ApplicationDbContext context, IHubContext<PresenceHub> presenceHub)
    {
        _context = context;
        _presenceHub = presenceHub;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<DriverListItemDto>>> GetAll()
    {
        var rawRows = await QueryDriverListRowsAsync();
        return rawRows.Select(MapToListItem).ToList();
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<UserProfile>> GetById(int id)
    {
        var driver = await FindActiveDriverProfileAsync(id);
        if (driver is null)
            return NotFound();

        return driver;
    }

    [HttpPost]
    public async Task<ActionResult<UserProfile>> Create(UserProfile driver)
    {
        driver.Role = UserRole.Driver;

        var phone = PhoneNumberValidation.Normalize(driver.PhoneNumber);
        if (phone is null)
            return BadRequest(new { message = PhoneNumberValidation.InvalidFormatMessage });

        var whitelistResult = await ResolveWhitelistForNewDriverAsync(phone);
        if (whitelistResult.Error is not null)
            return whitelistResult.Error;

        var whitelistEntry = whitelistResult.Whitelist!;

        if (await _context.UserProfiles.AnyAsync(p => p.UserId == whitelistEntry.Id))
        {
            return BadRequest(new
            {
                message = PhoneNumberValidation.DuplicateMessage,
                code = PhoneNumberValidation.PhoneTakenCode
            });
        }

        var carValidationError = GetDriverCarValidationError(driver);
        if (carValidationError is not null)
            return BadRequest(new { message = carValidationError });

        driver.UserId = whitelistEntry.Id;
        driver.PhoneNumber = whitelistEntry.PhoneNumber;
        driver.UserStatus = UserStatus.Offline;

        _context.UserProfiles.Add(driver);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("drivers", "create", driver.UserId);
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

        var normalizedPhone = PhoneNumberValidation.Normalize(driver.PhoneNumber);
        if (normalizedPhone is null)
            return BadRequest(new { message = PhoneNumberValidation.InvalidFormatMessage });

        var whitelistEntry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.Id == existingDriver.UserId);

        if (whitelistEntry is null || !whitelistEntry.IsActive || whitelistEntry.Role != UserRole.Driver)
            return BadRequest(new { message = "Активний запис водія у whitelist не знайдено." });

        var roleValidationError = ValidateRoleChange(driver.Role, GetActorRole());
        if (roleValidationError is not null)
            return roleValidationError;

        ApplyRoleChange(existingDriver, whitelistEntry, driver.Role);

        if (normalizedPhone != whitelistEntry.PhoneNumber
            && await IsPhoneTakenByAnotherUserAsync(normalizedPhone, whitelistEntry.Id))
        {
            return BadRequest(new
            {
                message = PhoneNumberValidation.DuplicateMessage,
                code = PhoneNumberValidation.PhoneTakenCode
            });
        }

        var carValidationError = GetDriverCarValidationError(driver);
        if (carValidationError is not null)
            return BadRequest(new { message = carValidationError });

        ApplyDriverProfileFields(existingDriver, whitelistEntry, driver, normalizedPhone);

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

        await BroadcastDashboardDataChanged("drivers", "update", existingDriver.UserId);
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
        await BroadcastDashboardDataChanged("drivers", "delete", driver.UserId);
        return NoContent();
    }

    private async Task<List<DriverListRow>> QueryDriverListRowsAsync() =>
        await (from profile in _context.UserProfiles
               join whitelist in _context.UserWhitelists
                   on profile.UserId equals whitelist.Id
               where profile.Role == UserRole.Driver
                     && whitelist.Role == UserRole.Driver
                     && whitelist.IsActive
                     && !string.IsNullOrWhiteSpace(profile.Name)
                     && profile.Name != profile.PhoneNumber
               join ride in _context.Rides.Where(r => r.Status == RideStatus.Completed)
                   on profile.Id equals ride.DriverId into ridesGroup
               select new DriverListRow
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
                   AverageRatingRaw = ridesGroup
                       .Where(r => r.Rating.HasValue)
                       .Select(r => r.Rating)
                       .Average()
               })
            .ToListAsync();

    private async Task<UserProfile?> FindActiveDriverProfileAsync(int profileId)
    {
        var driver = await _context.UserProfiles.FindAsync(profileId);
        if (driver is null || driver.Role != UserRole.Driver)
            return null;

        var hasActiveWhitelist = await _context.UserWhitelists.AnyAsync(w =>
            w.Id == driver.UserId &&
            w.IsActive &&
            w.Role == UserRole.Driver);

        return hasActiveWhitelist ? driver : null;
    }

    private async Task<(UserWhitelist? Whitelist, ActionResult<UserProfile>? Error)> ResolveWhitelistForNewDriverAsync(string phone)
    {
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
            return (whitelistEntry, null);
        }

        if (!whitelistEntry.IsActive)
            return (null, BadRequest(new { message = "Whitelist запис неактивний." }));

        if (whitelistEntry.Role is UserRole.Manager or UserRole.SuperAdmin)
        {
            return (null, BadRequest(new { message = "Для цього номера вже призначена адміністративна роль." }));
        }

        whitelistEntry.Role = UserRole.Driver;
        _context.UserWhitelists.Update(whitelistEntry);
        await _context.SaveChangesAsync();
        return (whitelistEntry, null);
    }

    private static DriverListItemDto MapToListItem(DriverListRow row) => new()
    {
        Id = row.Id,
        UserId = row.UserId,
        PhoneNumber = row.PhoneNumber,
        Name = row.Name,
        CarMake = row.CarMake,
        CarModel = row.CarModel,
        CarColor = row.CarColor,
        LicensePlate = row.LicensePlate,
        Role = row.Role,
        UserStatus = row.UserStatus,
        TripCount = row.TripCount,
        AverageRating = row.AverageRatingRaw.HasValue
            ? RoundMoney(row.AverageRatingRaw.Value)
            : null
    };

    private static void ApplyDriverProfileFields(
        UserProfile existingDriver,
        UserWhitelist whitelistEntry,
        UserProfile driver,
        string normalizedPhone)
    {
        whitelistEntry.PhoneNumber = normalizedPhone;
        existingDriver.PhoneNumber = normalizedPhone;
        existingDriver.Name = driver.Name;
        existingDriver.CarMake = driver.CarMake;
        existingDriver.CarModel = driver.CarModel;
        existingDriver.CarColor = driver.CarColor;
        existingDriver.LicensePlate = driver.LicensePlate;
        existingDriver.UserStatus = driver.Role == UserRole.Manager ? UserStatus.Offline : driver.UserStatus;
        existingDriver.UserId = whitelistEntry.Id;
    }

    private Task BroadcastDashboardDataChanged(string entity, string action, int userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private UserRole GetActorRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(role, out var parsed) ? parsed : UserRole.Driver;
    }

    private static string? GetDriverCarValidationError(UserProfile driver)
    {
        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarMake))
            return "Марка авто: лише латинські літери, цифри, пробіл та дефіс.";

        if (!CarFieldValidation.IsValidCarBrandOrModel(driver.CarModel))
            return "Модель авто: лише латинські літери, цифри, пробіл та дефіс.";

        if (!CarFieldValidation.IsValidCarColorUa(driver.CarColor))
            return "Колір авто: лише українською (кирилиця) та дефіс.";

        return null;
    }

    private static IActionResult? ValidateRoleChange(UserRole targetRole, UserRole actorRole)
    {
        if (targetRole is not (UserRole.Driver or UserRole.Manager))
            return new BadRequestObjectResult(new { message = "Дозволені лише ролі Водій або Менеджер." });

        if (targetRole == UserRole.Manager && actorRole != UserRole.SuperAdmin)
        {
            return new ObjectResult(new { message = "Призначити менеджера з картки водія може лише адміністратор." })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }

        return null;
    }

    private static void ApplyRoleChange(UserProfile profile, UserWhitelist whitelist, UserRole targetRole)
    {
        whitelist.Role = targetRole;
        profile.Role = targetRole;
    }

    private Task<bool> IsPhoneTakenByAnotherUserAsync(string phoneNumber, int userId) =>
        _context.UserWhitelists.AnyAsync(w => w.PhoneNumber == phoneNumber && w.Id != userId);

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class DriverListRow
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string PhoneNumber { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? CarMake { get; set; }
        public string? CarModel { get; set; }
        public string? CarColor { get; set; }
        public string? LicensePlate { get; set; }
        public UserRole Role { get; set; }
        public UserStatus UserStatus { get; set; }
        public int TripCount { get; set; }
        public decimal? AverageRatingRaw { get; set; }
    }
}
