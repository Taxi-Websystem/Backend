using System.ComponentModel.DataAnnotations;

namespace Backend.Models;

public class SystemSettings
{
    [Key]
    public int Id { get; set; }

    public decimal BaseFare { get; set; }

    public decimal CostPerKm { get; set; }

    public decimal PlatformFixedFee { get; set; }

    /// <summary>Fraction, e.g. 0.10 for 10%.</summary>
    public decimal PlatformFeePercentage { get; set; }

    public bool IsRouteOptimizationEnabled { get; set; }
}
