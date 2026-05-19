using System.Security.Claims;
using Backend.Data;
using Backend.Hubs;
using Backend.Models;
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

        return Ok(MapPresenceDto(settings, profile));
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
                new { message = "Лише водій може змінювати ці налаштування." });
        }

        if (request.IsAutoStatusEnabled is null
            && request.IsRouteOptimizationEnabled is null
            && request.IsAutoAcceptOrdersEnabled is null)
        {
            return BadRequest(new { message = "Не вказано налаштування для оновлення." });
        }

        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);

        if (request.IsAutoStatusEnabled is bool autoStatus)
        {
            settings.IsAutoStatusEnabled = autoStatus;

            if (settings.IsAutoStatusEnabled
                && profile.UserStatus == UserStatus.Offline
                && PresenceHub.HasActiveConnections(userId.Value))
            {
                profile.UserStatus = UserStatus.Online;
                await _context.SaveChangesAsync();
                await BroadcastStatusChanged(userId.Value, UserStatus.Online);
                return Ok(MapPresenceDto(settings, profile));
            }
        }

        if (request.IsRouteOptimizationEnabled is bool routeOptimization)
        {
            settings.IsRouteOptimizationEnabled = routeOptimization;
        }

        if (request.IsAutoAcceptOrdersEnabled is bool autoAcceptOrders)
        {
            settings.IsAutoAcceptOrdersEnabled = autoAcceptOrders;
        }

        await _context.SaveChangesAsync();

        return Ok(MapPresenceDto(settings, profile));
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

        if (profile.UserStatus == UserStatus.InRide)
        {
            var hasActiveRide = await _context.Rides.AnyAsync(r =>
                r.DriverId == profile.Id && r.Status == RideStatus.InRide);

            if (hasActiveRide)
            {
                return BadRequest(new { message = "Неможливо змінити статус під час активної поїздки." });
            }

            profile.UserStatus = UserStatus.Online;
        }

        var settings = await _userSettingsService.GetOrCreateAsync(userId.Value);
        if (settings.IsAutoStatusEnabled)
        {
            if (request.Status == UserStatus.Offline)
            {
                return BadRequest(new { message = "Офлайн при увімкненому автостатусі задається автоматично." });
            }

            if (request.Status == UserStatus.Break)
            {
                if (profile.UserStatus == UserStatus.Break)
                {
                    return BadRequest(new { message = "Ви вже на перерві." });
                }

                var hasAssignedRide = await _context.Rides.AnyAsync(r =>
                    r.DriverId == profile.Id
                    && (r.Status == RideStatus.Accepted || r.Status == RideStatus.InRide));

                if (hasAssignedRide)
                {
                    return BadRequest(new { message = "Неможливо взяти перерву, поки є активне замовлення." });
                }

                var canTakeBreak =
                    profile.UserStatus == UserStatus.Online
                    || (profile.UserStatus == UserStatus.Offline
                        && PresenceHub.HasActiveConnections(userId.Value));

                if (!canTakeBreak)
                {
                    return BadRequest(new { message = "Перерву можна взяти лише у статусі «Онлайн»." });
                }
            }
            else if (request.Status == UserStatus.Online)
            {
                if (profile.UserStatus != UserStatus.Break)
                {
                    return BadRequest(new { message = "При автостатусі можна лише завершити перерву." });
                }
            }
            else
            {
                return BadRequest(new { message = "При автостатусі можна лише взяти або завершити перерву." });
            }
        }

        if (request.Status is not UserStatus.Online and not UserStatus.Offline and not UserStatus.Break)
        {
            return BadRequest(new { message = "Ручне перемикання дозволене лише між Online, Offline та Break." });
        }

        if (request.Status == UserStatus.Break)
        {
            var hasAssignedRide = await _context.Rides.AnyAsync(r =>
                r.DriverId == profile.Id
                && (r.Status == RideStatus.Accepted || r.Status == RideStatus.InRide));

            if (hasAssignedRide)
            {
                return BadRequest(new { message = "Неможливо взяти перерву, поки є активне замовлення." });
            }
        }

        profile.UserStatus = request.Status;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(userId.Value, profile.UserStatus);

        return Ok(MapPresenceDto(settings, profile));
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

        if (profile.UserStatus is UserStatus.Online or UserStatus.Break)
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

    private static PresenceSettingsDto MapPresenceDto(UserSettings settings, UserProfile profile)
        => new(
            settings.IsAutoStatusEnabled,
            profile.UserStatus,
            !settings.IsAutoStatusEnabled,
            profile.Id,
            settings.IsRouteOptimizationEnabled,
            settings.IsAutoAcceptOrdersEnabled);
}

public record PresenceSettingsDto(
    bool IsAutoStatusEnabled,
    UserStatus CurrentStatus,
    bool IsManualControlAllowed,
    int ProfileId,
    bool IsRouteOptimizationEnabled,
    bool IsAutoAcceptOrdersEnabled);

public record UpdatePresenceSettingsRequest(
    bool? IsAutoStatusEnabled = null,
    bool? IsRouteOptimizationEnabled = null,
    bool? IsAutoAcceptOrdersEnabled = null);
public record SetPresenceStatusRequest(UserStatus Status);
