using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

    public AuthController(
        ApplicationDbContext context,
        IMemoryCache cache,
        IConfiguration configuration,
        ILogger<AuthController> logger)
    {
        _context = context;
        _cache = cache;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpPost("send-code")]
    public async Task<IActionResult> SendCode([FromBody] SendCodeRequest request)
    {
        var entry = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber);

        if (entry is null)
            return Unauthorized(new { code = "NOT_FOUND", message = "Номер не зареєстрований у системі." });

        if (!entry.IsActive)
            return Unauthorized(new { code = "INACTIVE", message = "Обліковий запис деактивовано. Якщо ви вважаєте це помилкою, зверніться до підтримки." });

        var code = Random.Shared.Next(100000, 999999).ToString();
        var cacheKey = $"otp:{request.PhoneNumber}";

        _cache.Set(cacheKey, code, TimeSpan.FromMinutes(5));

        _logger.LogInformation("OTP for {Phone}: {Code}", request.PhoneNumber, code);

        return Ok(new { message = "Код надіслано." });
    }

    [HttpPost("verify-code")]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request)
    {
        var cacheKey = $"otp:{request.PhoneNumber}";

        if (!_cache.TryGetValue(cacheKey, out string? cachedCode) || cachedCode != request.Code)
            return Unauthorized(new { message = "Невірний або прострочений код." });

        _cache.Remove(cacheKey);

        var whitelist = await _context.UserWhitelists
            .FirstOrDefaultAsync(w => w.PhoneNumber == request.PhoneNumber && w.IsActive);

        if (whitelist is null)
            return Unauthorized(new { message = "Доступ заборонено." });

        var userProfile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == whitelist.Id);

        if (userProfile is null)
        {
            _context.UserProfiles.Add(new UserProfile
            {
                UserId = whitelist.Id,
                PhoneNumber = whitelist.PhoneNumber,
                Name = whitelist.PhoneNumber,
                Role = whitelist.Role,
                UserStatus = UserStatus.Offline
            });
        }
        else
        {
            userProfile.PhoneNumber = whitelist.PhoneNumber;
            userProfile.Role = whitelist.Role;
            _context.UserProfiles.Update(userProfile);
        }

        await _context.SaveChangesAsync();

        var profileRow = await _context.UserProfiles.AsNoTracking()
            .FirstAsync(p => p.UserId == whitelist.Id);

        var requiresRegistration = ProfileRequiresRegistration(profileRow);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_configuration["Jwt:Key"]!));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, whitelist.Id.ToString()),
            new Claim(ClaimTypes.MobilePhone, whitelist.PhoneNumber),
            new Claim(ClaimTypes.Role, whitelist.Role.ToString())
        };

        var token = new JwtSecurityToken(
            issuer: _configuration["Jwt:Issuer"],
            audience: _configuration["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: credentials
        );

        return Ok(new
        {
            token = new JwtSecurityTokenHandler().WriteToken(token),
            role = whitelist.Role.ToString(),
            requiresRegistration
        });
    }

    /// <summary>Стан профілю для фронтенду (префіл форми завершення реєстрації).</summary>
    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthMeDto>> GetMe()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var whitelistId))
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

        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(idClaim, out var whitelistId))
            return Unauthorized(new { message = "Невалідний токен." });

        var roleClaim = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(roleClaim, out var userRole))
            return Unauthorized(new { message = "Невалідна роль у токені." });

        var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == whitelistId);
        if (profile is null)
            return NotFound(new { message = "Профіль не знайдено." });

        profile.Name = request.Name.Trim();

        if (userRole == UserRole.Driver)
        {
            if (string.IsNullOrWhiteSpace(request.CarBrand))
                return BadRequest(new { message = "Марка авто обов'язкова для водія." });
            if (!CarFieldValidation.IsValidCarBrandOrModel(request.CarBrand))
                return BadRequest(new { message = "Марка авто: лише латинські літери, цифри, пробіл та дефіс." });
            if (string.IsNullOrWhiteSpace(request.CarModel))
                return BadRequest(new { message = "Модель авто обов'язкова для водія." });
            if (!CarFieldValidation.IsValidCarBrandOrModel(request.CarModel))
                return BadRequest(new { message = "Модель авто: лише латинські літери, цифри, пробіл та дефіс." });
            if (string.IsNullOrWhiteSpace(request.CarColor))
                return BadRequest(new { message = "Колір авто обов'язковий для водія." });
            if (!CarFieldValidation.IsValidCarColorUa(request.CarColor))
                return BadRequest(new { message = "Колір авто: лише українською (кирилиця) та пробіли." });
            if (string.IsNullOrWhiteSpace(request.LicensePlate))
                return BadRequest(new { message = "Номер авто обов'язковий для водія." });

            var plate = NormalizeLicensePlate(request.LicensePlate);
            if (plate is null || !LicensePlatePattern.IsMatch(plate))
                return BadRequest(new { message = "Некоректний номер авто. Формат: 2 літери (латиниця або кирилиця), 4 цифри, 2 літери." });

            profile.CarMake = request.CarBrand.Trim();
            profile.CarModel = request.CarModel.Trim();
            profile.CarColor = request.CarColor.Trim();
            profile.LicensePlate = plate;
        }

        await _context.SaveChangesAsync();

        return NoContent();
    }

    private static string? NormalizeLicensePlate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // Літери латиницею та кирилицею + цифри
        var s = new string(raw.Where(c => char.IsLetter(c) || char.IsDigit(c)).ToArray()).ToUpperInvariant();
        return s.Length == 8 ? s : null;
    }

    [HttpPost("transfer-superadmin")]
    [Authorize(Policy = "SuperAdminOnly")]
    public async Task<IActionResult> TransferSuperAdmin([FromBody] TransferSuperAdminRequest request)
    {
        var currentUserIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(currentUserIdClaim, out var currentWhitelistId))
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
        if (currentProfile is null)
        {
            _context.UserProfiles.Add(new UserProfile
            {
                UserId = currentWhitelist.Id,
                PhoneNumber = currentWhitelist.PhoneNumber,
                Name = currentWhitelist.PhoneNumber,
                Role = UserRole.Manager,
                UserStatus = UserStatus.Offline
            });
        }
        else
        {
            currentProfile.Role = UserRole.Manager;
            currentProfile.PhoneNumber = currentWhitelist.PhoneNumber;
        }

        var targetProfile = relatedProfiles.FirstOrDefault(p => p.UserId == targetWhitelist.Id);
        if (targetProfile is null)
        {
            _context.UserProfiles.Add(new UserProfile
            {
                UserId = targetWhitelist.Id,
                PhoneNumber = targetWhitelist.PhoneNumber,
                Name = targetWhitelist.PhoneNumber,
                Role = UserRole.SuperAdmin,
                UserStatus = UserStatus.Offline
            });
        }
        else
        {
            targetProfile.Role = UserRole.SuperAdmin;
            targetProfile.PhoneNumber = targetWhitelist.PhoneNumber;
        }

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

public class CompleteRegistrationRequest
{
    public string Name { get; set; } = string.Empty;
    public string? CarBrand { get; set; }
    public string? CarModel { get; set; }
    public string? CarColor { get; set; }
    public string? LicensePlate { get; set; }
}

public record TransferSuperAdminRequest(int TargetWhitelistId);
