using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Claims;

namespace Backend.Controllers;

[ApiController]
[Route("api/analytics")]
[Authorize]
public class AnalyticsController : ControllerBase
{
    private const double HourlyBucketMaxSpanHours = 52d;

    private readonly ApplicationDbContext _context;

    public AnalyticsController(ApplicationDbContext context)
    {
        _context = context;
    }

    /// <summary>Date boundaries are UTC. Short ranges bucket by UTC hour; longer by UTC calendar day. Hour labels use BucketStartUtc on the client in local time.</summary>
    [HttpGet("driver/{driverId:int}")]
    public async Task<IActionResult> GetDriverAnalytics(
        int driverId,
        [FromQuery] DateTime startDate,
        [FromQuery] DateTime endDate)
    {
        if (startDate > endDate)
            return BadRequest(new { message = "startDate не може бути пізніше за endDate." });

        var startUtc = startDate.Kind == DateTimeKind.Utc ? startDate : startDate.ToUniversalTime();
        var endUtc = endDate.Kind == DateTimeKind.Utc ? endDate : endDate.ToUniversalTime();

        var authError = await EnsureCanAccessDriverAnalyticsAsync(driverId);
        if (authError is not null)
            return authError;

        var completedRides = await _context.Rides
            .AsNoTracking()
            .Where(r =>
                r.DriverId == driverId
                && r.Status == RideStatus.Completed
                && r.EndTime.HasValue
                && r.EndTime >= startUtc
                && r.EndTime <= endUtc)
            .OrderByDescending(r => r.EndTime)
            .Select(r => new
            {
                r.Id,
                r.FromAddress,
                r.ToAddress,
                EndTime = r.EndTime!.Value,
                r.StartTime,
                r.DriverProfit,
                r.Rating,
                r.DistanceKm
            })
            .ToListAsync();

        var rides = completedRides.Select(r => new RideForAnalytics
        {
            EndTime = r.EndTime,
            StartTime = r.StartTime,
            DriverProfit = r.DriverProfit,
            Rating = r.Rating,
            DistanceKm = r.DistanceKm
        }).ToList();

        var totalProfit = rides.Sum(r => r.DriverProfit ?? 0m);
        var totalRides = rides.Count;
        var rated = rides.Where(r => r.Rating.HasValue).ToList();
        double? averageRideRating = rated.Count == 0
            ? null
            : Math.Round(rated.Average(r => (double)r.Rating!.Value), 2, MidpointRounding.AwayFromZero);

        var spanHours = (endUtc - startUtc).TotalHours;
        var useHourlyBuckets = spanHours <= HourlyBucketMaxSpanHours;
        var chartGroups = useHourlyBuckets
            ? BuildHourlyChartPoints(rides, startUtc, endUtc)
            : BuildDailyChartPoints(rides);
        var chartBucket = useHourlyBuckets ? "hour" : "day";

        return Ok(new DriverAnalyticsResponseDto
        {
            Summary = new DriverAnalyticsSummaryDto
            {
                TotalProfit = decimal.Round(totalProfit, 2, MidpointRounding.AwayFromZero),
                TotalRides = totalRides,
                AverageRideRating = averageRideRating
            },
            ChartData = chartGroups,
            ChartBucket = chartBucket,
            RidesForMap = completedRides.Select(r => new RideMapSummaryDto
            {
                RideId = r.Id,
                FromAddress = r.FromAddress,
                ToAddress = r.ToAddress,
                EndTime = DateTime.SpecifyKind(r.EndTime, DateTimeKind.Utc)
            }).ToList()
        });
    }

    [HttpGet("driver/{driverId:int}/rides/{rideId:int}/map")]
    public async Task<IActionResult> GetDriverRideMap(int driverId, int rideId)
    {
        var authError = await EnsureCanAccessDriverAnalyticsAsync(driverId);
        if (authError is not null)
            return authError;

        var ride = await _context.Rides
            .AsNoTracking()
            .Include(r => r.RoutePoints)
            .FirstOrDefaultAsync(r => r.Id == rideId && r.DriverId == driverId);

        if (ride is null)
            return NotFound(new { message = "Поїздку не знайдено." });

        return Ok(new RideMapDto
        {
            Id = ride.Id,
            FromAddress = ride.FromAddress,
            ToAddress = ride.ToAddress,
            FromLatitude = ride.FromLatitude,
            FromLongitude = ride.FromLongitude,
            ToLatitude = ride.ToLatitude,
            ToLongitude = ride.ToLongitude,
            DistanceKm = ride.DistanceKm,
            RoutePoints = ride.RoutePoints
                .OrderBy(p => p.RecordedAt)
                .Select(p => new RoutePointDto
                {
                    Latitude = p.Latitude,
                    Longitude = p.Longitude,
                    RecordedAt = p.RecordedAt
                })
                .ToList()
        });
    }

    private static List<DriverAnalyticsChartPointDto> BuildDailyChartPoints(List<RideForAnalytics> rides)
    {
        return rides
            .GroupBy(r => DateOnly.FromDateTime(r.EndTime))
            .OrderBy(g => g.Key)
            .Select(g => new DriverAnalyticsChartPointDto
            {
                Label = g.Key.ToString("dd.MM", CultureInfo.InvariantCulture),
                Profit = g.Sum(x => x.DriverProfit ?? 0m),
                RidesCount = g.Count(),
                TransitSecondsTotal = g.Sum(x =>
                    x.StartTime.HasValue
                        ? (x.EndTime - x.StartTime!.Value).TotalSeconds
                        : 0d),
                DistanceKmTotal = g.Sum(x => x.DistanceKm)
            })
            .ToList();
    }

    private static List<DriverAnalyticsChartPointDto> BuildHourlyChartPoints(
        List<RideForAnalytics> rides,
        DateTime startUtc,
        DateTime endUtc)
    {
        var byHour = rides
            .GroupBy(r => TruncateToUtcHour(r.EndTime))
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Profit = g.Sum(x => x.DriverProfit ?? 0m),
                    Count = g.Count(),
                    Transit = g.Sum(x =>
                        x.StartTime.HasValue
                            ? (x.EndTime - x.StartTime!.Value).TotalSeconds
                            : 0d),
                    Dkm = g.Sum(x => x.DistanceKm)
                });

        var firstHour = TruncateToUtcHour(startUtc);
        var lastHour = TruncateToUtcHour(endUtc);
        var sameUtcCalendarDay = firstHour.Date == lastHour.Date;
        var result = new List<DriverAnalyticsChartPointDto>();

        for (var t = firstHour; t <= lastHour; t = t.AddHours(1))
        {
            byHour.TryGetValue(t, out var agg);
            result.Add(new DriverAnalyticsChartPointDto
            {
                Label = FormatHourlyAxisLabel(t, sameUtcCalendarDay),
                BucketStartUtc = t,
                Profit = agg?.Profit ?? 0m,
                RidesCount = agg?.Count ?? 0,
                TransitSecondsTotal = agg?.Transit ?? 0d,
                DistanceKmTotal = agg?.Dkm ?? 0m
            });
        }

        return result;
    }

    private static DateTime TruncateToUtcHour(DateTime utc) =>
        new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private static string FormatHourlyAxisLabel(DateTime hourUtc, bool sameUtcCalendarDay) =>
        sameUtcCalendarDay
            ? hourUtc.ToString("HH:mm", CultureInfo.InvariantCulture)
            : hourUtc.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    private async Task<IActionResult?> EnsureCanAccessDriverAnalyticsAsync(int driverId)
    {
        var roleClaim = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(roleClaim, out var role))
            return Unauthorized(new { message = "Невідома роль." });

        if (role == UserRole.Manager || role == UserRole.SuperAdmin)
            return null;

        if (role != UserRole.Driver)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Доступ заборонено." });

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!int.TryParse(userIdClaim, out var whitelistUserId))
            return Unauthorized(new { message = "Невалідний токен." });

        var profile = await _context.UserProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == whitelistUserId);

        if (profile is null || profile.Role != UserRole.Driver)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Профіль водія не знайдено." });

        if (profile.Id != driverId)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Можна переглядати лише власну аналітику." });

        return null;
    }

    private sealed class RideForAnalytics
    {
        public DateTime EndTime { get; set; }
        public DateTime? StartTime { get; set; }
        public decimal? DriverProfit { get; set; }
        public decimal? Rating { get; set; }
        public decimal DistanceKm { get; set; }
    }
}
