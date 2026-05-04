using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Backend.Models.Enums;

namespace Backend.Models;

public class UserProfile
{
    [Key]
    public int Id { get; set; }

    public int UserId { get; set; }

    [Required]
    [MaxLength(20)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? CarMake { get; set; }

    [MaxLength(50)]
    public string? CarModel { get; set; }

    [MaxLength(50)]
    public string? CarColor { get; set; }

    [MaxLength(20)]
    public string? LicensePlate { get; set; }

    public UserRole Role { get; set; }

    public UserStatus UserStatus { get; set; } = UserStatus.Offline;

    /// <summary>Dashboard trip count; only SuperAdmin may change via drivers API.</summary>
    public int TripCount { get; set; }

    /// <summary>Dashboard average rating; only SuperAdmin may change via drivers API.</summary>
    [Column(TypeName = "decimal(3,2)")]
    public decimal? AverageRating { get; set; }
}
