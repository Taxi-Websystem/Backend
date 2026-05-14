using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Backend.Models.Enums;

namespace Backend.Models;

public class Ride
{
    [Key]
    public int Id { get; set; }

    public int? DriverId { get; set; }

    public UserProfile? Driver { get; set; }

    public RideStatus Status { get; set; } = RideStatus.Created;

    [Column(TypeName = "decimal(5,2)")]
    public decimal? Rating { get; set; }

    [Required]
    [MaxLength(300)]
    public string FromAddress { get; set; } = string.Empty;

    [Required]
    [MaxLength(300)]
    public string ToAddress { get; set; } = string.Empty;

    public DateTime? StartTime { get; set; }

    public DateTime? EndTime { get; set; }

    public decimal DistanceKm { get; set; }

    public decimal Price { get; set; }

    public decimal? DriverProfit { get; set; }

    public List<RideRoutePoint> Route { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RideRoutePoint
{
    public decimal Lat { get; set; }
    public decimal Lng { get; set; }
}
