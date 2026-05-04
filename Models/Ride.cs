using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Models;

public class Ride
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(20)]
    public string ClientPhone { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string ClientName { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string FromAddress { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string ToAddress { get; set; } = string.Empty;

    public DateTime StartTime { get; set; } = DateTime.UtcNow;

    public DateTime? EndTime { get; set; }

    public string? RouteJson { get; set; }

    [Column(TypeName = "decimal(10,2)")]
    public decimal Price { get; set; }

    /// <summary>FK to <see cref="UserProfile.Id"/> when the ride is assigned to a driver profile.</summary>
    public int? DriverProfileId { get; set; }

    public UserProfile? DriverProfile { get; set; }

    /// <summary>Passenger/client rating for this ride (1–5).</summary>
    [Column(TypeName = "decimal(3,2)")]
    public decimal? Rating { get; set; }
}
