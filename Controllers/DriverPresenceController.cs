using System.Security.Claims;
using Backend.Data;
using Backend.Hubs;
using Backend.Models.Enums;
using Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Backend.Controllers;

[ApiController]
[Route("api/presence")]
[Authorize]
public class PresenceController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IHubContext<PresenceHub> _presenceHub;

    public PresenceController(
        ApplicationDbContext context,
        IUserSettingsService userSettingsService,
        IHubContext<PresenceHub> presenceHub)
    {
        _context = context;
        _userSettingsService = userSettingsService;
        _presenceHub = presenceHub;
    }

    [HttpGet("settings")]
    public async Task<ActionResult<PresenceSettingsDto>> GetSettings()
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { message = "Невалідний токен." });
        }

        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
        if (profile is null)
        {
            return NotFound(new { message = "Профіль не знайдено." });
        }

        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);

        if (settings.IsAutoStatusEnabled
            && profile.UserStatus == UserStatus.Offline
            && PresenceHub.HasActiveConnections(userId.Value))
        {
            profile.UserStatus = UserStatus.Online;
            await _context.SaveChangesAsync();
            await BroadcastStatusChanged(userId.Value, UserStatus.Online);
        }

        return Ok(new PresenceSettingsDto(
            settings.IsAutoStatusEnabled,
            profile.UserStatus,
            !settings.IsAutoStatusEnabled));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<PresenceSettingsDto>> UpdateSettings([FromBody] UpdatePresenceSettingsRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { message = "Невалідний токен." });
        }

        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
        if (profile is null)
        {
            return NotFound(new { message = "Профіль не знайдено." });
        }
        if (profile.Role != UserRole.Driver)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Лише водій може змінювати автостатус." });
        }

        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);
        settings.IsAutoStatusEnabled = request.IsAutoStatusEnabled;

        if (settings.IsAutoStatusEnabled
            && profile.UserStatus == UserStatus.Offline
            && PresenceHub.HasActiveConnections(userId.Value))
        {
            profile.UserStatus = UserStatus.Online;
            await _context.SaveChangesAsync();
            await BroadcastStatusChanged(userId.Value, UserStatus.Online);
        }
        else
        {
            await _context.SaveChangesAsync();
        }

        return Ok(new PresenceSettingsDto(
            settings.IsAutoStatusEnabled,
            profile.UserStatus,
            !settings.IsAutoStatusEnabled));
    }

    [HttpPost("status")]
    public async Task<ActionResult<PresenceSettingsDto>> SetManualStatus([FromBody] SetPresenceStatusRequest request)
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { message = "Невалідний токен." });
        }

        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
        if (profile is null)
        {
            return NotFound(new { message = "Профіль не знайдено." });
        }
        if (profile.Role != UserRole.Driver)
        {
            return StatusCode(StatusCodes.Status403Forbidden,
                new { message = "Лише водій може керувати статусом вручну." });
        }

        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);
        if (settings.IsAutoStatusEnabled)
        {
            return BadRequest(new { message = "Ручне керування доступне лише коли автостатус вимкнено." });
        }

        if (request.Status is not UserStatus.Online and not UserStatus.Offline)
        {
            return BadRequest(new { message = "Ручне перемикання дозволене лише між Online/Offline." });
        }

        if (profile.UserStatus == UserStatus.InRide)
        {
            return BadRequest(new { message = "Неможливо змінити статус під час активної поїздки." });
        }

        profile.UserStatus = request.Status;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(userId.Value, profile.UserStatus);

        return Ok(new PresenceSettingsDto(
            settings.IsAutoStatusEnabled,
            profile.UserStatus,
            !settings.IsAutoStatusEnabled));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> MarkOfflineOnLogout()
    {
        var userId = GetCurrentUserId();
        if (userId is null)
        {
            return Unauthorized(new { message = "Невалідний токен." });
        }

        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId.Value);
        if (profile is null)
        {
            return NotFound(new { message = "Профіль не знайдено." });
        }

        if (profile.UserStatus != UserStatus.InRide
            && profile.UserStatus != UserStatus.Offline)
        {
            profile.UserStatus = UserStatus.Offline;
            await _context.SaveChangesAsync();
            await BroadcastStatusChanged(userId.Value, UserStatus.Offline);
        }

        return NoContent();
    }

    private int? GetCurrentUserId()
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(idClaim, out var id) ? id : null;
    }

    private Task BroadcastStatusChanged(int userId, UserStatus status)
        => _presenceHub.Clients.All.SendAsync("PresenceChanged", new { userId, status = status.ToString() });
}

public record PresenceSettingsDto(bool IsAutoStatusEnabled, UserStatus CurrentStatus, bool IsManualControlAllowed);
public record UpdatePresenceSettingsRequest(bool IsAutoStatusEnabled);
public record SetPresenceStatusRequest(UserStatus Status);
