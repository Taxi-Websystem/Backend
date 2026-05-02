using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Backend.Data;
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
}

public record SendCodeRequest(string PhoneNumber);
public record VerifyCodeRequest(string PhoneNumber, string Code);
