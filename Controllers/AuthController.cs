using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;

using Backend.Data;
using Backend.Hubs;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;

namespace Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private static readonly Regex LicensePlatePattern = new(@"^[\p{L}]{2}\d{4}[\p{L}]{2}$", RegexOptions.Compiled);

    private readonly ApplicationDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AuthController> _logger;
    private readonly IHubContext<PresenceHub> _presenceHub;

    public AuthController(
        ApplicationDbContext context,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<AuthController> logger,
        IHubContext<PresenceHub> presenceHub)
    {
        _context = context;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
        _presenceHub = presenceHub;
    }

    [HttpPost("send-code")]
    public async Task<IActionResult> SendCode([FromBody] SendCodeRequest request)
    {
        var whitelistEntry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber);

        if (whitelistEntry is null)
            return Unauthorized(new { code = "NOT_FOUND", message = "Номер не зареєстрований у системі." });

        if (!whitelistEntry.IsActive)
            return Unauthorized(new { code = "INACTIVE", message = "Обліковий запис деактивовано. Якщо ви вважаєте це помилкою, зверніться до підтримки." });

        var code = Random.Shared.Next(100000, 999999).ToString();
        var otpCacheKey = BuildOtpCacheKey(request.PhoneNumber);

        _cache.Set(otpCacheKey, code, TimeSpan.FromMinutes(5));

        _logger.LogInformation("OTP for {Phone}: {Code}", request.PhoneNumber, code);

        return Ok(new { message = "Код надіслано." });
    }

    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request)
    {
        var otpCacheKey = BuildOtpCacheKey(request.PhoneNumber);

        if (!_cache.TryGetValue(otpCacheKey, out string? cachedCode) || cachedCode != request.Code)
            return Unauthorized(new { message = "Невірний або прострочений код." });

        _cache.Remove(otpCacheKey);

        var whitelist = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber && w.IsActive);

        if (whitelist is null)
            return Unauthorized(new { message = "Доступ заборонено." });

        var userProfile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == whitelist.Id);

        UpsertUserProfile(userProfile, whitelist, whitelist.Role);

        await _context.SaveChangesAsync();

        var profileRow = await _context.UserProfiles.AsNoTracking()
            .FirstAsync(p => p.UserId == whitelist.Id);

        var requiresRegistration = ProfileRequiresRegistration(profileRow);

        return Ok(new
        {
            token = GenerateJwtToken(whitelist.Id, whitelist.PhoneNumber, whitelist.Role),
            role = whitelist.Role.ToString(),
            requiresRegistration
        });
    }

    [HttpGet("public-stats")]
    [AllowAnonymous]
    public async Task<ActionResult<LoginPublicStatsDto>> GetPublicStats()
    {
        var (todayStartUtc, tomorrowStartUtc) = GetKyivTodayUtcBounds();

        var onlineDrivers = await (from profile in _context.UserProfiles
                                   join whitelist in _context.UserWhitelists on profile.UserId equals whitelist.Id
                                   where profile.Role == UserRole.Driver
                                         && whitelist.Role == UserRole.Driver
                                         && whitelist.IsActive
                                         && profile.UserStatus == UserStatus.Online
                                   select profile.Id)
            .CountAsync();

        var todayTrips = await _context.Rides
            .Where(r => r.CreatedAt >= todayStartUtc && r.CreatedAt < tomorrowStartUtc)
            .CountAsync();

        return Ok(new LoginPublicStatsDto(onlineDrivers, todayTrips));
    }

    private static string BuildOtpCacheKey(string phoneNumber) => $"otp:{phoneNumber}";

    private static (DateTime todayStartUtc, DateTime tomorrowStartUtc) GetKyivTodayUtcBounds()
    {
        var kyivZone = GetKyivTimeZone();
        var kyivNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, kyivZone);
        var kyivDayStart = kyivNow.Date;
        var kyivNextDayStart = kyivDayStart.AddDays(1);
        return (
            TimeZoneInfo.ConvertTimeToUtc(kyivDayStart, kyivZone),
            TimeZoneInfo.ConvertTimeToUtc(kyivNextDayStart, kyivZone));
    }

    private static TimeZoneInfo GetKyivTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("FLE Standard Time");
        }
    }

    private bool TryGetWhitelistIdFromClaims(out int whitelistId)
    {
        var nameIdentifier = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(nameIdentifier, out whitelistId);
    }

    private bool TryGetUserRoleFromClaims(out UserRole userRole)
    {
        var roleClaim = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse(roleClaim, out userRole);
    }

    /// <summary>Стан профілю для фронтенду (префіл форми завершення реєстрації).</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthMeDto>> GetMe()
    {
        if (!TryGetWhitelistIdFromClaims(out var whitelistId))
            return Unauthorized(new { message = "Невалідний токен." });

        var whitelist = await _context.UserWhitelists.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == whitelistId);
        var profile = await _context.UserProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == whitelistId);

        if (whitelist is null || profile is null)
            return NotFound(new { message = "Профіль не знайдено." });

        return Ok(new AuthMeDto(
            whitelist.PhoneNumber,
            profile.Name,
            profile.CarMake,
            profile.CarModel,
            profile.CarColor,
            profile.LicensePlate,
            whitelist.Role.ToString()));
    }

    [HttpPost("complete-registration")]
    [Authorize]
    public async Task<IActionResult> CompleteRegistration([FromBody] CompleteRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { message = "Ім'я обов'язкове." });

        if (!TryGetWhitelistIdFromClaims(out var whitelistId))
            return Unauthorized(new { message = "Невалідний токен." });

        if (!TryGetUserRoleFromClaims(out var userRole))
            return Unauthorized(new { message = "Невалідна роль у токені." });

        var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == whitelistId);
        if (profile is null)
            return NotFound(new { message = "Профіль не знайдено." });

        profile.Name = request.Name.Trim();

        if (userRole == UserRole.Driver)
        {
            var (normalizedLicensePlate, validationError) = ValidateAndNormalizeDriverRegistration(request);
            if (validationError is not null)
                return BadRequest(new { message = validationError });

            profile.CarMake = request.CarBrand!.Trim();
            profile.CarModel = request.CarModel!.Trim();
            profile.CarColor = request.CarColor!.Trim();
            profile.LicensePlate = normalizedLicensePlate;
        }

        await _context.SaveChangesAsync();

        var entity = userRole == UserRole.Driver ? "drivers" : "managers";
        await _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new
        {
            entity,
            action = "update",
            userId = whitelistId
        });

        return NoContent();
    }

    private static string? NormalizeLicensePlate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var s = new string(raw.Where(c => char.IsLetter(c) || char.IsDigit(c)).ToArray()).ToUpperInvariant();
        return s.Length == 8 ? s : null;
    }

    [HttpPost("transfer-superadmin")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> TransferSuperAdmin([FromBody] TransferSuperAdminRequest request)
    {
        if (!TryGetWhitelistIdFromClaims(out var currentWhitelistId))
            return Unauthorized(new { message = "Невалідний токен користувача." });

        if (currentWhitelistId == request.TargetWhitelistId)
            return BadRequest(new { message = "Неможливо передати права самому собі." });

        var currentWhitelist = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.Id == currentWhitelistId && w.Role == UserRole.SuperAdmin && w.IsActive);
        if (currentWhitelist is null)
            return Unauthorized(new { message = "Поточний користувач не є активним SuperAdmin." });

        var targetWhitelist = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.Id == request.TargetWhitelistId && w.Role == UserRole.Manager && w.IsActive);
        if (targetWhitelist is null)
            return BadRequest(new { message = "Цільовий користувач має бути активним Manager." });

        await using var transaction = await _context.Database.BeginTransactionAsync();

        currentWhitelist.Role = UserRole.Manager;
        targetWhitelist.Role = UserRole.SuperAdmin;

        var relatedProfiles = await _context.UserProfiles
            .Where(p => p.UserId == currentWhitelist.Id || p.UserId == targetWhitelist.Id)
            .ToListAsync();

        var currentProfile = relatedProfiles.FirstOrDefault(p => p.UserId == currentWhitelist.Id);
        UpsertUserProfile(currentProfile, currentWhitelist, UserRole.Manager);

        var targetProfile = relatedProfiles.FirstOrDefault(p => p.UserId == targetWhitelist.Id);
        UpsertUserProfile(targetProfile, targetWhitelist, UserRole.SuperAdmin);

        await _context.SaveChangesAsync();
        await transaction.CommitAsync();

        var downgradedToken = GenerateJwtToken(currentWhitelist.Id, currentWhitelist.PhoneNumber, UserRole.Manager);

        return Ok(new
        {
            message = "Права SuperAdmin успішно передано.",
            token = downgradedToken,
            role = UserRole.Manager.ToString()
        });
    }

    private string GenerateJwtToken(int userId, string phoneNumber, UserRole role)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.MobilePhone, phoneNumber),
            new Claim(ClaimTypes.Role, role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials
        );

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private void UpsertUserProfile(UserProfile? profile, UserWhitelist whitelist, UserRole role)
    {
        if (profile is null)
        {
            _context.UserProfiles.Add(new UserProfile
            {
                UserId = whitelist.Id,
                PhoneNumber = whitelist.PhoneNumber,
                Name = whitelist.PhoneNumber,
                Role = role,
                UserStatus = UserStatus.Offline
            });
            return;
        }

        profile.Role = role;
        profile.PhoneNumber = whitelist.PhoneNumber;
        _context.UserProfiles.Update(profile);
    }

    private static (string? normalizedLicensePlate, string? validationError) ValidateAndNormalizeDriverRegistration(
        CompleteRegistrationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.CarBrand))
            return (null, "Марка авто обов'язкова для водія.");
        if (!CarFieldValidation.IsValidCarBrandOrModel(request.CarBrand))
            return (null, "Марка авто: лише латинські літери, цифри, пробіл та дефіс.");

        if (string.IsNullOrWhiteSpace(request.CarModel))
            return (null, "Модель авто обов'язкова для водія.");
        if (!CarFieldValidation.IsValidCarBrandOrModel(request.CarModel))
            return (null, "Модель авто: лише латинські літери, цифри, пробіл та дефіс.");

        if (string.IsNullOrWhiteSpace(request.CarColor))
            return (null, "Колір авто обов'язковий для водія.");
        if (!CarFieldValidation.IsValidCarColorUa(request.CarColor))
            return (null, "Колір авто: лише українською (кирилиця) та дефіс.");

        if (string.IsNullOrWhiteSpace(request.LicensePlate))
            return (null, "Номер авто обов'язковий для водія.");

        var normalizedLicensePlate = NormalizeLicensePlate(request.LicensePlate);
        if (normalizedLicensePlate is null || !LicensePlatePattern.IsMatch(normalizedLicensePlate))
        {
            return (null, "Некоректний номер авто. Формат: 2 літери (латиниця або кирилиця), 4 цифри, 2 літери.");
        }

        return (normalizedLicensePlate, null);
    }

    /// <summary>
    /// Потрібне завершення реєстрації: немає імені (або залишено placeholder телефону),
    /// або водій без повних даних авто (у т.ч. після переведення з менеджера).
    /// </summary>
    private static bool ProfileRequiresRegistration(UserProfile p)
    {
        if (string.IsNullOrWhiteSpace(p.Name))
            return true;
        if (p.Name == p.PhoneNumber)
            return true;

        if (p.Role != UserRole.Driver)
            return false;

        return string.IsNullOrWhiteSpace(p.CarMake)
               || string.IsNullOrWhiteSpace(p.CarModel)
               || string.IsNullOrWhiteSpace(p.CarColor)
               || string.IsNullOrWhiteSpace(p.LicensePlate);
    }
}

public record AuthMeDto(
    string PhoneNumber,
    string Name,
    string? CarBrand,
    string? CarModel,
    string? CarColor,
    string? LicensePlate,
    string Role);

public record SendCodeRequest(string PhoneNumber);
public record VerifyCodeRequest(string PhoneNumber, string Code);
public record LoginPublicStatsDto(int OnlineDrivers, int TodayTrips);

public class CompleteRegistrationRequest
{
    public string Name { get; set; } = string.Empty;
    public string? CarBrand { get; set; }
    public string? CarModel { get; set; }
    public string? CarColor { get; set; }
    public string? LicensePlate { get; set; }
}

public record TransferSuperAdminRequest(int TargetWhitelistId);
