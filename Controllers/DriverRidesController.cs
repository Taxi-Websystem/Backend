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
[Route("api/driver/rides")]
[Authorize]
public class DriverRidesController : ControllerBase
{
    private const int CancelWindowMinutes = 3;

    private readonly ApplicationDbContext _context;
    private readonly IHubContext<PresenceHub> _presenceHub;
    private readonly IRidePricingService _ridePricing;

    public DriverRidesController(
        ApplicationDbContext context,
        IHubContext<PresenceHub> presenceHub,
        IRidePricingService ridePricing)
    {
        _context = context;
        _presenceHub = presenceHub;
        _ridePricing = ridePricing;
    }

    [HttpGet("pending")]
    public async Task<ActionResult<List<DriverPendingRideDto>>> GetPending()
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        var hasActiveRide = await HasActiveRideAsync(profile.Id);
        if (hasActiveRide)
            return Ok(new List<DriverPendingRideDto>());

        var settings = await _ridePricing.GetSettingsAsync();
        var rides = await _context.Rides
            .AsNoTracking()
            .Where(r =>
                r.Status == RideStatus.Created
                && (r.DriverId == null || r.DriverId == profile.Id))
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        return rides.Select(r => MapPending(r, settings)).ToList();
    }

    [HttpGet("active")]
    public async Task<ActionResult<DriverActiveRideDto?>> GetActive()
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        var ride = await _context.Rides
            .AsNoTracking()
            .Where(r =>
                r.DriverId == profile.Id
                && (r.Status == RideStatus.Accepted || r.Status == RideStatus.InRide))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();

        if (ride is null)
            return Ok(null);

        var settings = await _ridePricing.GetSettingsAsync();
        return Ok(MapActive(ride, settings));
    }

    [HttpPost("{id}/accept")]
    public async Task<ActionResult<DriverActiveRideDto>> Accept(int id)
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        if (await HasActiveRideAsync(profile.Id))
            return BadRequest(new { message = "Спочатку завершіть або скасуйте поточне замовлення." });

        var ride = await _context.Rides.FirstOrDefaultAsync(r =>
            r.Id == id && r.Status == RideStatus.Created);
        if (ride is null)
            return NotFound(new { message = "Замовлення не знайдено." });

        if (ride.DriverId.HasValue && ride.DriverId != profile.Id)
            return BadRequest(new { message = "Замовлення вже призначено іншому водію." });

        var settings = await _ridePricing.GetSettingsAsync();
        ride.DriverId = profile.Id;
        ride.Status = RideStatus.Accepted;
        ride.AcceptedAt = DateTime.UtcNow;
        if (!ride.DriverProfit.HasValue)
            _ridePricing.ApplyFinancials(ride, settings, ride.DistanceKm, RideStatus.Accepted);

        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "accept", profile.UserId);

        return Ok(MapActive(ride, settings));
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        var ride = await GetRideForDriverAsync(id, profile.Id);
        if (ride is null)
            return NotFound(new { message = "Замовлення не знайдено." });

        if (ride.Status != RideStatus.Accepted)
            return BadRequest(new { message = "Скасувати можна лише прийняте замовлення до початку поїздки." });

        if (!IsWithinCancelWindow(ride))
            return BadRequest(new { message = "Час для скасування замовлення минув." });

        var settings = await _ridePricing.GetSettingsAsync();
        ride.Status = RideStatus.Created;
        ride.AcceptedAt = null;
        ride.DriverId = null;
        _ridePricing.ApplyFinancials(ride, settings, ride.DistanceKm, RideStatus.Canceled);

        if (profile.UserStatus == UserStatus.InRide)
            profile.UserStatus = UserStatus.Online;

        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "release", profile.UserId);

        return NoContent();
    }

    [HttpPost("{id}/start")]
    public async Task<ActionResult<DriverActiveRideDto>> Start(int id)
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        if (profile.UserStatus == UserStatus.InRide)
            return BadRequest(new { message = "У вас уже є активна поїздка." });

        var ride = await GetRideForDriverAsync(id, profile.Id);
        if (ride is null)
            return NotFound(new { message = "Поїздку не знайдено." });

        if (ride.Status != RideStatus.Accepted)
            return BadRequest(new { message = "Поїздку можна розпочати лише зі статусу «Прийнята»." });

        ride.Status = RideStatus.InRide;
        ride.StartTime = DateTime.UtcNow;
        profile.UserStatus = UserStatus.InRide;

        await _context.SaveChangesAsync();
        await BroadcastPresenceChanged(profile.UserId, UserStatus.InRide);
        await BroadcastDashboardDataChanged("rides", "start", profile.UserId);

        var settings = await _ridePricing.GetSettingsAsync();
        return Ok(MapActive(ride, settings));
    }

    [HttpPost("{id}/complete")]
    public async Task<ActionResult> Complete(int id)
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        var ride = await GetRideForDriverAsync(id, profile.Id);
        if (ride is null)
            return NotFound(new { message = "Поїздку не знайдено." });

        if (ride.Status != RideStatus.InRide)
            return BadRequest(new { message = "Завершити можна лише поїздку «У дорозі»." });

        var settings = await _ridePricing.GetSettingsAsync();
        ride.Status = RideStatus.Completed;
        ride.EndTime = DateTime.UtcNow;
        if (!ride.StartTime.HasValue)
            ride.StartTime = ride.EndTime;

        if (!ride.DriverProfit.HasValue)
            _ridePricing.ApplyFinancials(ride, settings, ride.DistanceKm, RideStatus.Completed);
        profile.UserStatus = UserStatus.Online;

        await _context.SaveChangesAsync();
        await BroadcastPresenceChanged(profile.UserId, UserStatus.Online);
        await BroadcastDashboardDataChanged("rides", "complete", profile.UserId);

        return NoContent();
    }

    [HttpPost("{id}/route-points")]
    public async Task<IActionResult> AppendRoutePoints(int id, [FromBody] AppendRoutePointsRequest request)
    {
        var profile = await GetCurrentDriverProfileAsync();
        if (profile is null)
            return Forbid();

        var ride = await GetRideForDriverAsync(id, profile.Id);
        if (ride is null)
            return NotFound(new { message = "Поїздку не знайдено." });

        if (ride.Status != RideStatus.InRide)
            return BadRequest(new { message = "Трек записується лише під час поїздки." });

        if (request.Points is null || request.Points.Count == 0)
            return BadRequest(new { message = "Немає точок для збереження." });

        var now = DateTime.UtcNow;
        foreach (var point in request.Points)
        {
            if (!HasValidCoordinates(point.Latitude, point.Longitude))
                continue;

            _context.RideRoutePoints.Add(new RideRoutePoint
            {
                RideId = ride.Id,
                Latitude = point.Latitude,
                Longitude = point.Longitude,
                RecordedAt = point.RecordedAt?.ToUniversalTime() ?? now
            });
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    private Task<bool> HasActiveRideAsync(int driverProfileId) =>
        _context.Rides.AnyAsync(r =>
            r.DriverId == driverProfileId
            && (r.Status == RideStatus.Accepted || r.Status == RideStatus.InRide));

    private Task<Ride?> GetRideForDriverAsync(int rideId, int driverProfileId) =>
        _context.Rides.FirstOrDefaultAsync(r => r.Id == rideId && r.DriverId == driverProfileId);

    private static bool IsWithinCancelWindow(Ride ride)
    {
        var acceptedAt = ride.AcceptedAt ?? ride.CreatedAt;
        var cancelDeadline = acceptedAt.AddMinutes(CancelWindowMinutes);
        return DateTime.UtcNow <= cancelDeadline;
    }

    private static bool HasValidCoordinates(decimal latitude, decimal longitude) =>
        latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;

    private static DriverPendingRideDto MapPending(Ride ride, SystemSettings settings) =>
        new()
        {
            Id = ride.Id,
            FromAddress = ride.FromAddress,
            ToAddress = ride.ToAddress,
            DistanceKm = ride.DistanceKm,
            DriverProfit = ride.DriverProfit ?? EstimateDriverProfit(ride.DistanceKm, settings)
        };

    private static decimal? EstimateDriverProfit(decimal distanceKm, SystemSettings settings)
    {
        var price = RoundMoney(settings.BaseFare + distanceKm * settings.CostPerKm);
        var percentFee = RoundMoney(price * settings.PlatformFeePercentage);
        return RoundMoney(price - settings.PlatformFixedFee - percentFee);
    }

    private static DriverActiveRideDto MapActive(Ride ride, SystemSettings settings)
    {
        var driverProfit = ride.DriverProfit ?? EstimateDriverProfit(ride.DistanceKm, settings);
        var (acceptedAt, cancelSecondsRemaining, canCancel) = GetCancelWindowInfo(ride);

        return new DriverActiveRideDto
        {
            Id = ride.Id,
            Status = ride.Status,
            FromAddress = ride.FromAddress,
            ToAddress = ride.ToAddress,
            DistanceKm = ride.DistanceKm,
            DriverProfit = driverProfit,
            StartTime = ride.StartTime,
            AcceptedAt = acceptedAt,
            CancelSecondsRemaining = ride.Status == RideStatus.Accepted ? cancelSecondsRemaining : 0,
            CanCancel = ride.Status == RideStatus.Accepted && canCancel
        };
    }

    private static (DateTime? acceptedAt, int cancelSecondsRemaining, bool canCancel) GetCancelWindowInfo(Ride ride)
    {
        var acceptedAt = ride.AcceptedAt ?? (ride.Status == RideStatus.Accepted ? ride.CreatedAt : null);
        if (acceptedAt is null)
            return (null, 0, false);

        var cancelDeadlineUtc = acceptedAt.Value.AddMinutes(CancelWindowMinutes);
        var cancelSecondsRemaining = Math.Max(0, (int)Math.Floor((cancelDeadlineUtc - DateTime.UtcNow).TotalSeconds));
        return (acceptedAt, cancelSecondsRemaining, cancelSecondsRemaining > 0);
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private async Task<UserProfile?> GetCurrentDriverProfileAsync()
    {
        var whitelistId = GetCurrentWhitelistId();
        if (whitelistId is null)
            return null;

        var profile = await _context.UserProfiles.FirstOrDefaultAsync(p => p.UserId == whitelistId.Value);
        if (profile is null || profile.Role != UserRole.Driver)
            return null;

        return profile;
    }

    private int? GetCurrentWhitelistId()
    {
        var nameIdentifier = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(nameIdentifier, out var whitelistId) ? whitelistId : null;
    }

    private Task BroadcastPresenceChanged(int userId, UserStatus status)
        => _presenceHub.Clients.All.SendAsync("PresenceChanged", new { userId, status = status.ToString() });

    private Task BroadcastDashboardDataChanged(string entity, string action, int whitelistUserId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId = whitelistUserId });
}
