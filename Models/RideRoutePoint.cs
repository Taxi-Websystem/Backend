using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Backend.Models;

public class RideRoutePoint
{
    [Key]
    public int Id { get; set; }

    public int RideId { get; set; }

    public Ride Ride { get; set; } = null!;

    [Column(TypeName = "decimal(9,6)")]
    public decimal Latitude { get; set; }

    [Column(TypeName = "decimal(9,6)")]
    public decimal Longitude { get; set; }

    public DateTime RecordedAt { get; set; }
}
