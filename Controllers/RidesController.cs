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
[Route("api/[controller]")]
[Authorize(Policy = "ManagerOrSuperAdmin")]
public class RidesController : ControllerBase
{
    private readonly ApplicationDbContext _context;
    private readonly IHubContext<PresenceHub> _presenceHub;
    private readonly IRidePricingService _ridePricing;

    public RidesController(
        ApplicationDbContext context,
        IHubContext<PresenceHub> presenceHub,
        IRidePricingService ridePricing)
    {
        _context = context;
        _presenceHub = presenceHub;
        _ridePricing = ridePricing;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<RideListItemDto>>> GetAll()
    {
        var rides = await _context.Rides
            .AsNoTracking()
            .Include(r => r.Driver)
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => MapToListItem(r))
            .ToListAsync();

        return rides;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RideListItemDto>> GetById(int id)
    {
        var ride = await FindRideWithDriverAsync(id);
        if (ride is null)
            return NotFound();

        return MapToListItem(ride);
    }

    [HttpGet("{id}/map")]
    public async Task<ActionResult<RideMapDto>> GetMap(int id)
    {
        var ride = await _context.Rides
            .AsNoTracking()
            .Include(r => r.RoutePoints)
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync();
        if (ride is null)
            return NotFound();

        return MapToRideMapDto(ride);
    }

    [HttpPost]
    public async Task<ActionResult<RideListItemDto>> Create(RideUpsertDto dto)
    {
        dto.Rating = GetManagerAdjustedRating(dto.Rating, null);

        var validationError = await ValidateRideAsync(dto);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var settings = await _ridePricing.GetSettingsAsync();

        var ride = new Ride
        {
            CreatedAt = DateTime.UtcNow
        };
        ApplyRideUpsertDto(ride, dto);
        NormalizeCompletedRideTimestamps(ride);
        _ridePricing.ApplyFinancials(ride, settings, dto.DistanceKm, dto.Status);

        _context.Rides.Add(ride);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "create", ride.DriverId);
        return CreatedAtAction(nameof(GetById), new { id = ride.Id }, MapToListItem(ride));
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, RideUpsertDto dto)
    {
        var ride = await _context.Rides.FindAsync(id);
        if (ride is null)
            return NotFound();

        dto.Rating = GetManagerAdjustedRating(dto.Rating, ride.Rating);

        var validationError = await ValidateRideAsync(dto);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        var settings = await _ridePricing.GetSettingsAsync();

        ApplyRideUpsertDto(ride, dto);
        NormalizeCompletedRideTimestamps(ride);
        _ridePricing.ApplyFinancials(ride, settings, dto.DistanceKm, dto.Status);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (!await _context.Rides.AnyAsync(r => r.Id == id))
                return NotFound();
            throw;
        }

        await BroadcastDashboardDataChanged("rides", "update", ride.DriverId);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ride = await _context.Rides.FindAsync(id);
        if (ride is null)
            return NotFound();

        _context.Rides.Remove(ride);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "delete", ride.DriverId);
        return NoContent();
    }

    private Task<Ride?> FindRideWithDriverAsync(int id) =>
        _context.Rides
            .AsNoTracking()
            .Include(r => r.Driver)
            .Where(r => r.Id == id)
            .FirstOrDefaultAsync();

    private static RideListItemDto MapToListItem(Ride r) => new()
    {
        Id = r.Id,
        DriverId = r.DriverId,
        DriverName = r.Driver?.Name,
        DriverPhoneNumber = r.Driver?.PhoneNumber,
        Status = r.Status,
        Rating = r.Rating,
        FromAddress = r.FromAddress,
        ToAddress = r.ToAddress,
        FromLatitude = r.FromLatitude,
        FromLongitude = r.FromLongitude,
        ToLatitude = r.ToLatitude,
        ToLongitude = r.ToLongitude,
        StartTime = r.StartTime,
        EndTime = r.EndTime,
        CreatedAt = r.CreatedAt,
        DistanceKm = r.DistanceKm,
        Price = r.Price,
        DriverProfit = r.DriverProfit
    };

    private static RideMapDto MapToRideMapDto(Ride ride) => new()
    {
        Id = ride.Id,
        FromAddress = ride.FromAddress,
        ToAddress = ride.ToAddress,
        FromLatitude = ride.FromLatitude,
        FromLongitude = ride.FromLongitude,
        ToLatitude = ride.ToLatitude,
        ToLongitude = ride.ToLongitude,
        DistanceKm = ride.DistanceKm,
        RoutePoints = MapRoutePoints(ride.RoutePoints)
    };

    private static List<RoutePointDto> MapRoutePoints(IEnumerable<RideRoutePoint> points) =>
        points
            .OrderBy(p => p.RecordedAt)
            .Select(p => new RoutePointDto
            {
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                RecordedAt = p.RecordedAt
            })
            .ToList();

    private Task BroadcastDashboardDataChanged(string entity, string action, int? userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private UserRole GetActorRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(role, out var parsed) ? parsed : UserRole.Driver;
    }

    private decimal? GetManagerAdjustedRating(decimal? requestedRating, decimal? existingRating)
    {
        if (GetActorRole() != UserRole.Manager)
            return requestedRating;

        return existingRating;
    }

    private static void ApplyRideUpsertDto(Ride ride, RideUpsertDto dto)
    {
        ride.DriverId = dto.DriverId;
        ride.Status = dto.Status;
        ride.Rating = dto.Rating;
        ride.FromAddress = dto.FromAddress.Trim();
        ride.ToAddress = dto.ToAddress.Trim();
        ride.FromLatitude = dto.FromLatitude;
        ride.FromLongitude = dto.FromLongitude;
        ride.ToLatitude = dto.ToLatitude;
        ride.ToLongitude = dto.ToLongitude;
        ride.StartTime = dto.StartTime;
        ride.EndTime = dto.EndTime;
    }

    private async Task<string?> ValidateRideAsync(RideUpsertDto dto)
    {
        var driverRatingError = await ValidateRideDriverAndRatingAsync(dto.DriverId, dto.Rating);
        if (driverRatingError is not null)
            return driverRatingError;

        if (dto.DistanceKm < 0)
            return "Відстань не може бути від’ємною.";

        if (string.IsNullOrWhiteSpace(dto.FromAddress) || string.IsNullOrWhiteSpace(dto.ToAddress))
            return "Вкажіть адреси «Звідки» та «Куди».";

        if (!HasValidCoordinates(dto.FromLatitude, dto.FromLongitude)
            || !HasValidCoordinates(dto.ToLatitude, dto.ToLongitude))
            return "Оберіть адреси зі списку (потрібні координати).";

        return null;
    }

    private static bool HasValidCoordinates(decimal? lat, decimal? lng) =>
        lat.HasValue && lng.HasValue
        && lat.Value is >= -90 and <= 90
        && lng.Value is >= -180 and <= 180;

    private async Task<string?> ValidateRideDriverAndRatingAsync(int? driverId, decimal? rating)
    {
        if (driverId.HasValue)
        {
            var profile = await _context.UserProfiles.FindAsync(driverId.Value);
            if (profile is null || profile.Role != UserRole.Driver)
                return "Профіль водія для поїздки не знайдено або не є водієм.";
        }

        if (rating.HasValue && (rating.Value < 1m || rating.Value > 5m))
            return "Оцінка поїздки має бути від 1 до 5.";

        return null;
    }

    private static void NormalizeCompletedRideTimestamps(Ride ride)
    {
        if (ride.Status != RideStatus.Completed)
            return;

        if (!ride.EndTime.HasValue)
            ride.EndTime = DateTime.UtcNow;

        if (!ride.StartTime.HasValue)
            ride.StartTime = ride.EndTime!.Value;
    }
}
