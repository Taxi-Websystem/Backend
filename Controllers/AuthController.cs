using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
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
                DriverStatus = DriverStatus.Offline
            });
        }
        else
        {
            userProfile.PhoneNumber = whitelist.PhoneNumber;
            userProfile.Role = whitelist.Role;
            _context.UserProfiles.Update(userProfile);
        }

        await _context.SaveChangesAsync();

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
            role = whitelist.Role.ToString()
        });
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
                DriverStatus = DriverStatus.Offline
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
                DriverStatus = DriverStatus.Offline
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
}

public record SendCodeRequest(string PhoneNumber);
public record VerifyCodeRequest(string PhoneNumber, string Code);
public record TransferSuperAdminRequest(int TargetWhitelistId);
