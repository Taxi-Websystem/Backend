namespace Backend.Models;

public class FinancialSettingsDto
{
    public decimal BaseFare { get; set; }
    public decimal CostPerKm { get; set; }
    public decimal PlatformFixedFee { get; set; }
    /// <summary>Fraction, e.g. 0.10 for 10%.</summary>
    public decimal PlatformFeePercentage { get; set; }
}

public class UpdateFinancialSettingsDto
{
    public decimal BaseFare { get; set; }
    public decimal CostPerKm { get; set; }
    public decimal PlatformFixedFee { get; set; }
    public decimal PlatformFeePercentage { get; set; }
}

public class DriverAnalyticsSummaryDto
{
    public decimal TotalProfit { get; set; }
    public int TotalRides { get; set; }
    /// <summary>Середній рейтинг поїздок (1–5) за період; null, якщо немає оцінених поїздок.</summary>
    public double? AverageRideRating { get; set; }
}

public class DriverAnalyticsChartPointDto
{
    public string Label { get; set; } = string.Empty;
    /// <summary>Початок годинного бакета в UTC (для chartBucket hour); клієнт показує підпис у локальному часі.</summary>
    public DateTime? BucketStartUtc { get; set; }
    public decimal Profit { get; set; }
    public int RidesCount { get; set; }
    public double TransitSecondsTotal { get; set; }
    public decimal DistanceKmTotal { get; set; }
}

public class DriverAnalyticsResponseDto
{
    public DriverAnalyticsSummaryDto Summary { get; set; } = null!;
    public List<DriverAnalyticsChartPointDto> ChartData { get; set; } = [];
    /// <summary>"hour" for short ranges (24h, вчора), "day" for longer periods.</summary>
    public string ChartBucket { get; set; } = "day";
    public List<RideMapSummaryDto> RidesForMap { get; set; } = [];
}
