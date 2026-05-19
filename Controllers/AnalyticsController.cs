using System.Globalization;
using System.Security.Claims;

using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

        var startUtc = ToUtc(startDate);
        var endUtc = ToUtc(endDate);

        var authError = await EnsureCanAccessDriverAnalyticsAsync(driverId);
        if (authError is not null)
            return authError;

        var completedRides = await QueryCompletedRidesForDriverAsync(driverId, startUtc, endUtc);

        var rides = completedRides
            .Select(r => new RideForAnalytics
            {
                EndTime = r.EndTime,
                StartTime = r.StartTime,
                DriverProfit = r.DriverProfit,
                Rating = r.Rating,
                DistanceKm = r.DistanceKm
            })
            .ToList();

        var spanHours = (endUtc - startUtc).TotalHours;
        var useHourlyBuckets = spanHours <= HourlyBucketMaxSpanHours;
        var chartGroups = useHourlyBuckets
            ? BuildHourlyChartPoints(rides, startUtc, endUtc)
            : BuildDailyChartPoints(rides);

        return Ok(new DriverAnalyticsResponseDto
        {
            Summary = BuildAnalyticsSummary(rides),
            ChartData = chartGroups,
            ChartBucket = useHourlyBuckets ? "hour" : "day",
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

        return Ok(MapToRideMapDto(ride));
    }

    private async Task<List<CompletedRideRow>> QueryCompletedRidesForDriverAsync(
        int driverId,
        DateTime startUtc,
        DateTime endUtc) =>
        await _context.Rides
            .AsNoTracking()
            .Where(r =>
                r.DriverId == driverId
                && r.Status == RideStatus.Completed
                && r.EndTime.HasValue
                && r.EndTime >= startUtc
                && r.EndTime <= endUtc)
            .OrderByDescending(r => r.EndTime)
            .Select(r => new CompletedRideRow
            {
                Id = r.Id,
                FromAddress = r.FromAddress,
                ToAddress = r.ToAddress,
                EndTime = r.EndTime!.Value,
                StartTime = r.StartTime,
                DriverProfit = r.DriverProfit,
                Rating = r.Rating,
                DistanceKm = r.DistanceKm
            })
            .ToListAsync();

    private static List<DriverAnalyticsChartPointDto> BuildDailyChartPoints(List<RideForAnalytics> rides) =>
        rides
            .GroupBy(r => DateOnly.FromDateTime(r.EndTime))
            .OrderBy(g => g.Key)
            .Select(g => new DriverAnalyticsChartPointDto
            {
                Label = g.Key.ToString("dd.MM", CultureInfo.InvariantCulture),
                Profit = g.Sum(x => x.DriverProfit ?? 0m),
                RidesCount = g.Count(),
                TransitSecondsTotal = g.Sum(GetTransitSeconds),
                DistanceKmTotal = g.Sum(x => x.DistanceKm)
            })
            .ToList();

    private static List<DriverAnalyticsChartPointDto> BuildHourlyChartPoints(
        List<RideForAnalytics> rides,
        DateTime startUtc,
        DateTime endUtc)
    {
        var byHour = rides
            .GroupBy(r => TruncateToUtcHour(r.EndTime))
            .ToDictionary(
                g => g.Key,
                g => new HourBucketAggregate(
                    g.Sum(x => x.DriverProfit ?? 0m),
                    g.Count(),
                    g.Sum(GetTransitSeconds),
                    g.Sum(x => x.DistanceKm)));

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
                TransitSecondsTotal = agg?.TransitSeconds ?? 0d,
                DistanceKmTotal = agg?.DistanceKm ?? 0m
            });
        }

        return result;
    }

    private static double GetTransitSeconds(RideForAnalytics ride) =>
        ride.StartTime.HasValue
            ? (ride.EndTime - ride.StartTime!.Value).TotalSeconds
            : 0d;

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
        RoutePoints = ride.RoutePoints
            .OrderBy(p => p.RecordedAt)
            .Select(p => new RoutePointDto
            {
                Latitude = p.Latitude,
                Longitude = p.Longitude,
                RecordedAt = p.RecordedAt
            })
            .ToList()
    };

    private static DateTime TruncateToUtcHour(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);

    private static DateTime ToUtc(DateTime dateTime) =>
        dateTime.Kind == DateTimeKind.Utc ? dateTime : dateTime.ToUniversalTime();

    private static string FormatHourlyAxisLabel(DateTime hourUtc, bool sameUtcCalendarDay) =>
        sameUtcCalendarDay
            ? hourUtc.ToString("HH:mm", CultureInfo.InvariantCulture)
            : hourUtc.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    private async Task<IActionResult?> EnsureCanAccessDriverAnalyticsAsync(int driverId)
    {
        var roleClaim = User.FindFirstValue(ClaimTypes.Role);
        if (!Enum.TryParse<UserRole>(roleClaim, out var role))
            return Unauthorized(new { message = "Невідома роль." });

        if (role is UserRole.Manager or UserRole.SuperAdmin)
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

    private static DriverAnalyticsSummaryDto BuildAnalyticsSummary(List<RideForAnalytics> rides)
    {
        var totalProfit = rides.Sum(r => r.DriverProfit ?? 0m);
        var ratedRides = rides.Where(r => r.Rating.HasValue).ToList();
        double? averageRideRating = ratedRides.Count == 0
            ? null
            : Math.Round(ratedRides.Average(r => (double)r.Rating!.Value), 2, MidpointRounding.AwayFromZero);

        return new DriverAnalyticsSummaryDto
        {
            TotalProfit = RoundMoney(totalProfit),
            TotalRides = rides.Count,
            AverageRideRating = averageRideRating
        };
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class RideForAnalytics
    {
        public DateTime EndTime { get; set; }
        public DateTime? StartTime { get; set; }
        public decimal? DriverProfit { get; set; }
        public decimal? Rating { get; set; }
        public decimal DistanceKm { get; set; }
    }

    private sealed class CompletedRideRow
    {
        public int Id { get; set; }
        public string FromAddress { get; set; } = string.Empty;
        public string ToAddress { get; set; } = string.Empty;
        public DateTime EndTime { get; set; }
        public DateTime? StartTime { get; set; }
        public decimal? DriverProfit { get; set; }
        public decimal? Rating { get; set; }
        public decimal DistanceKm { get; set; }
    }

    private sealed record HourBucketAggregate(
        decimal Profit,
        int Count,
        double TransitSeconds,
        decimal DistanceKm);
}
