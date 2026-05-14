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
    public DateTime? StartTime { get; set; }
    public DateTime? EndTime { get; set; }
    public decimal DistanceKm { get; set; }
}
