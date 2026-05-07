using Backend.Models.Enums;

namespace Backend.Models;

/// <summary>Driver row for manager list API.</summary>
public class DriverListItemDto
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string PhoneNumber { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? CarMake { get; set; }
    public string? CarModel { get; set; }
    public string? CarColor { get; set; }
    public string? LicensePlate { get; set; }
    public UserRole Role { get; set; }
    public UserStatus UserStatus { get; set; }
    public int TripCount { get; set; }
    public decimal? AverageRating { get; set; }
}
