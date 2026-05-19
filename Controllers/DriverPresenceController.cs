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
        var (whitelistId, profile, errorResult) = await ResolveCurrentProfileAsync();
        if (errorResult is not null)
            return errorResult;

        var settings = await _userSettingsService.GetOrCreateAsync(whitelistId!.Value);
        await TryApplyAutoOnlineAsync(settings, profile!, whitelistId.Value);

        return Ok(MapPresenceDto(settings, profile!));
    }

    [HttpPut("settings")]
    public async Task<ActionResult<PresenceSettingsDto>> UpdateSettings([FromBody] UpdatePresenceSettingsRequest request)
    {
        var (whitelistId, profile, errorResult) = await ResolveCurrentProfileAsync();
        if (errorResult is not null)
            return errorResult;

        var driverOnlyError = EnsureDriverProfile(profile!, "Лише водій може змінювати ці налаштування.");
        if (driverOnlyError is not null)
            return driverOnlyError;

        if (HasNoSettingsToUpdate(request))
            return BadRequest(new { message = "Не вказано налаштування для оновлення." });

        var settings = await _userSettingsService.GetOrCreateAsync(whitelistId!.Value);

        if (request.IsAutoStatusEnabled is bool autoStatusEnabled)
        {
            settings.IsAutoStatusEnabled = autoStatusEnabled;

            if (await TryApplyAutoOnlineAsync(settings, profile!, whitelistId.Value))
                return Ok(MapPresenceDto(settings, profile!));
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

        return Ok(MapPresenceDto(settings, profile!));
    }

    [HttpPost("status")]
    public async Task<ActionResult<PresenceSettingsDto>> SetManualStatus([FromBody] SetPresenceStatusRequest request)
    {
        var (whitelistId, profile, errorResult) = await ResolveCurrentProfileAsync();
        if (errorResult is not null)
            return errorResult;

        var driverOnlyError = EnsureDriverProfile(profile!, "Лише водій може керувати статусом вручну.");
        if (driverOnlyError is not null)
            return driverOnlyError;

        var inRideConflictError = await EnsureNoInRideConflictAsync(profile!);
        if (inRideConflictError is not null)
            return inRideConflictError;

        var settings = await _userSettingsService.GetOrCreateAsync(whitelistId!.Value);
        var statusValidationError = await ValidateManualStatusChangeAsync(profile!, settings, request.Status, whitelistId.Value);
        if (statusValidationError is not null)
            return BadRequest(new { message = statusValidationError });

        profile!.UserStatus = request.Status;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(whitelistId.Value, profile.UserStatus);

        return Ok(MapPresenceDto(settings, profile));
    }

    [HttpPost("logout")]
    public async Task<IActionResult> MarkOfflineOnLogout()
    {
        var (whitelistId, profile, errorResult) = await ResolveCurrentProfileAsync();
        if (errorResult is not null)
            return errorResult;

        if (profile!.UserStatus is UserStatus.Online or UserStatus.Break)
        {
            profile.UserStatus = UserStatus.Offline;
            await _context.SaveChangesAsync();
            await BroadcastStatusChanged(whitelistId!.Value, UserStatus.Offline);
        }

        return NoContent();
    }

    private async Task<(int? whitelistId, UserProfile? profile, ActionResult? errorResult)> ResolveCurrentProfileAsync()
    {
        var whitelistId = GetCurrentWhitelistId();
        if (whitelistId is null)
            return (null, null, Unauthorized(new { message = "Невалідний токен." }));

        var profile = await _context.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == whitelistId.Value);
        if (profile is null)
            return (null, null, NotFound(new { message = "Профіль не знайдено." }));

        return (whitelistId.Value, profile, null);
    }

    private async Task<bool> TryApplyAutoOnlineAsync(UserSettings settings, UserProfile profile, int whitelistId)
    {
        if (!ShouldAutoSwitchToOnline(settings, profile, whitelistId))
            return false;

        profile.UserStatus = UserStatus.Online;
        await _context.SaveChangesAsync();
        await BroadcastStatusChanged(whitelistId, UserStatus.Online);
        return true;
    }

    private static ActionResult? EnsureDriverProfile(UserProfile profile, string message)
    {
        if (profile.Role == UserRole.Driver)
            return null;

        return new ObjectResult(new { message })
        {
            StatusCode = StatusCodes.Status403Forbidden
        };
    }

    private async Task<ActionResult?> EnsureNoInRideConflictAsync(UserProfile profile)
    {
        if (profile.UserStatus != UserStatus.InRide)
            return null;

        var hasActiveRide = await _context.Rides.AnyAsync(r =>
            r.DriverId == profile.Id && r.Status == RideStatus.InRide);

        if (hasActiveRide)
            return BadRequest(new { message = "Неможливо змінити статус під час активної поїздки." });

        profile.UserStatus = UserStatus.Online;
        return null;
    }

    private async Task<string?> ValidateManualStatusChangeAsync(
        UserProfile profile,
        UserSettings settings,
        UserStatus requestedStatus,
        int whitelistId)
    {
        if (settings.IsAutoStatusEnabled)
        {
            var autoStatusError = await ValidateAutoStatusManualChangeAsync(profile, requestedStatus, whitelistId);
            if (autoStatusError is not null)
                return autoStatusError;
        }

        if (requestedStatus is not UserStatus.Online and not UserStatus.Offline and not UserStatus.Break)
            return "Ручне перемикання дозволене лише між Online, Offline та Break.";

        if (requestedStatus == UserStatus.Break && await HasAssignedRideAsync(profile.Id))
            return "Неможливо взяти перерву, поки є активне замовлення.";

        return null;
    }

    private async Task<string?> ValidateAutoStatusManualChangeAsync(UserProfile profile, UserStatus requestedStatus, int whitelistId)
    {
        if (requestedStatus == UserStatus.Offline)
            return "Офлайн при увімкненому автостатусі задається автоматично.";

        if (requestedStatus == UserStatus.Break)
        {
            if (profile.UserStatus == UserStatus.Break)
                return "Ви вже на перерві.";

            if (await HasAssignedRideAsync(profile.Id))
                return "Неможливо взяти перерву, поки є активне замовлення.";

            var canTakeBreak = profile.UserStatus == UserStatus.Online
                || (profile.UserStatus == UserStatus.Offline && PresenceHub.HasActiveConnections(whitelistId));

            if (!canTakeBreak)
                return "Перерву можна взяти лише у статусі «Онлайн».";

            return null;
        }

        if (requestedStatus == UserStatus.Online)
        {
            if (profile.UserStatus != UserStatus.Break)
                return "При автостатусі можна лише завершити перерву.";

            return null;
        }

        return "При автостатусі можна лише взяти або завершити перерву.";
    }

    private static bool HasNoSettingsToUpdate(UpdatePresenceSettingsRequest request)
    {
        return request.IsAutoStatusEnabled is null
            && request.IsRouteOptimizationEnabled is null
            && request.IsAutoAcceptOrdersEnabled is null;
    }

    private static bool ShouldAutoSwitchToOnline(UserSettings settings, UserProfile profile, int whitelistId)
    {
        return settings.IsAutoStatusEnabled
            && profile.UserStatus == UserStatus.Offline
            && PresenceHub.HasActiveConnections(whitelistId);
    }

    private Task<bool> HasAssignedRideAsync(int driverProfileId)
    {
        return _context.Rides.AnyAsync(r =>
            r.DriverId == driverProfileId
            && (r.Status == RideStatus.Accepted || r.Status == RideStatus.InRide));
    }

    private int? GetCurrentWhitelistId()
    {
        var nameIdentifier = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(nameIdentifier, out var whitelistId) ? whitelistId : null;
    }

    private Task BroadcastStatusChanged(int whitelistId, UserStatus status)
        => _presenceHub.Clients.All.SendAsync("PresenceChanged", new { userId = whitelistId, status = status.ToString() });

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
