using Backend.Models.Enums;

namespace Backend.Models;

public class RideListItemDto
{
    public int Id { get; set; }
    public int? DriverId { get; set; }
    public string? DriverName { get; set; }
    public string? DriverPhoneNumber { get; set; }
    public RideStatus Status { get; set; }
    public decimal? Rating { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public decimal? FromLatitude { get; set; }
    public decimal? FromLongitude { get; set; }
    public decimal? ToLatitude { get; set; }
    public decimal? ToLongitude { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public DateTime CreatedAt { get; set; }
    public decimal DistanceKm { get; set; }
    public decimal Price { get; set; }
    public decimal? DriverProfit { get; set; }
}

public class RideUpsertDto
{
    public int? DriverId { get; set; }
    public RideStatus Status { get; set; } = RideStatus.Created;
    public decimal? Rating { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public decimal? FromLatitude { get; set; }
    public decimal? FromLongitude { get; set; }
    public decimal? ToLatitude { get; set; }
    public decimal? ToLongitude { get; set; }
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public decimal DistanceKm { get; set; }
}

public class RoutePointDto
{
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public DateTime RecordedAt { get; set; }
}

public class RideMapDto
{
    public int Id { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    public decimal? FromLatitude { get; set; }
    public decimal? FromLongitude { get; set; }
    public decimal? ToLatitude { get; set; }
    public decimal? ToLongitude { get; set; }
    public decimal DistanceKm { get; set; }
    public List<RoutePointDto> RoutePoints { get; set; } = [];
}

public class RideMapSummaryDto
{
    public int RideId { get; set; }
    public string FromAddress { get; set; } = string.Empty;
    public string ToAddress { get; set; } = string.Empty;
    /// <summary>UTC; клієнт форматує в локальному часовому поясі.</summary>
    public DateTime EndTime { get; set; }
}
