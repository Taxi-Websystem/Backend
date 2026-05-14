using Backend.Data;
using Backend.Hubs;
using Backend.Models;
using Backend.Models.Enums;
using Backend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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
            .Select(r => new RideListItemDto
            {
                Id = r.Id,
                DriverId = r.DriverId,
                DriverName = r.Driver != null ? r.Driver.Name : null,
                DriverPhoneNumber = r.Driver != null ? r.Driver.PhoneNumber : null,
                Status = r.Status,
                Rating = r.Rating,
                FromAddress = r.FromAddress,
                ToAddress = r.ToAddress,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                CreatedAt = r.CreatedAt,
                DistanceKm = r.DistanceKm,
                Price = r.Price,
                DriverProfit = r.DriverProfit
            })
            .ToListAsync();

        return rides;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<RideListItemDto>> GetById(int id)
    {
        var ride = await _context.Rides
            .AsNoTracking()
            .Include(r => r.Driver)
            .Where(r => r.Id == id)
            .Select(r => new RideListItemDto
            {
                Id = r.Id,
                DriverId = r.DriverId,
                DriverName = r.Driver != null ? r.Driver.Name : null,
                DriverPhoneNumber = r.Driver != null ? r.Driver.PhoneNumber : null,
                Status = r.Status,
                Rating = r.Rating,
                FromAddress = r.FromAddress,
                ToAddress = r.ToAddress,
                StartTime = r.StartTime,
                EndTime = r.EndTime,
                CreatedAt = r.CreatedAt,
                DistanceKm = r.DistanceKm,
                Price = r.Price,
                DriverProfit = r.DriverProfit
            })
            .FirstOrDefaultAsync();
        if (ride is null) return NotFound();
        return ride;
    }

    [HttpPost]
    public async Task<ActionResult<RideListItemDto>> Create(RideUpsertDto dto)
    {
        if (GetActorRole() == UserRole.Manager)
        {
            dto.Rating = null;
        }

        var validationError = await ValidateRideDriverAndRatingMessageAsync(dto.DriverId, dto.Rating);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        if (dto.DistanceKm < 0)
            return BadRequest(new { message = "Відстань не може бути від’ємною." });

        var settings = await _ridePricing.GetSettingsAsync();

        var ride = new Ride
        {
            DriverId = dto.DriverId,
            Status = dto.Status,
            Rating = dto.Rating,
            FromAddress = dto.FromAddress.Trim(),
            ToAddress = dto.ToAddress.Trim(),
            StartTime = dto.StartTime,
            EndTime = dto.EndTime,
            CreatedAt = DateTime.UtcNow,
            Route = []
        };

        NormalizeCompletedRideTimestamps(ride);

        _ridePricing.ApplyFinancials(ride, settings, dto.DistanceKm, dto.Status);

        _context.Rides.Add(ride);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "create", ride.DriverId);
        return CreatedAtAction(nameof(GetById), new { id = ride.Id }, new RideListItemDto
        {
            Id = ride.Id,
            DriverId = ride.DriverId,
            Status = ride.Status,
            Rating = ride.Rating,
            FromAddress = ride.FromAddress,
            ToAddress = ride.ToAddress,
            StartTime = ride.StartTime,
            EndTime = ride.EndTime,
            CreatedAt = ride.CreatedAt,
            DistanceKm = ride.DistanceKm,
            Price = ride.Price,
            DriverProfit = ride.DriverProfit
        });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, RideUpsertDto dto)
    {
        var ride = await _context.Rides.FindAsync(id);
        if (ride is null)
            return NotFound();

        if (GetActorRole() == UserRole.Manager)
        {
            dto.Rating = ride.Rating;
        }

        var validationError = await ValidateRideDriverAndRatingMessageAsync(dto.DriverId, dto.Rating);
        if (validationError is not null)
            return BadRequest(new { message = validationError });

        if (dto.DistanceKm < 0)
            return BadRequest(new { message = "Відстань не може бути від’ємною." });

        var settings = await _ridePricing.GetSettingsAsync();

        ride.DriverId = dto.DriverId;
        ride.Status = dto.Status;
        ride.Rating = dto.Rating;
        ride.FromAddress = dto.FromAddress.Trim();
        ride.ToAddress = dto.ToAddress.Trim();
        ride.StartTime = dto.StartTime;
        ride.EndTime = dto.EndTime;

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
        if (ride is null) return NotFound();

        _context.Rides.Remove(ride);
        await _context.SaveChangesAsync();
        await BroadcastDashboardDataChanged("rides", "delete", ride.DriverId);
        return NoContent();
    }

    private Task BroadcastDashboardDataChanged(string entity, string action, int? userId)
        => _presenceHub.Clients.All.SendAsync("DashboardDataChanged", new { entity, action, userId });

    private UserRole GetActorRole()
    {
        var role = User.FindFirstValue(ClaimTypes.Role);
        return Enum.TryParse<UserRole>(role, out var parsed) ? parsed : UserRole.Driver;
    }

    private async Task<string?> ValidateRideDriverAndRatingMessageAsync(int? driverId, decimal? rating)
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

    /// <summary>
    /// Завершена поїздка без часу завершення не потрапляє в аналітику (фільтр по EndTime).
    /// Якщо кінець не вказано — ставимо UTC «зараз», початок — як кінець, якщо теж порожній.
    /// </summary>
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
