using Backend.Data;
using Backend.Models;
using Backend.Models.Enums;
using Microsoft.EntityFrameworkCore;

namespace Backend.Services;

public class RidePricingService : IRidePricingService
{
    private readonly ApplicationDbContext _context;

    public RidePricingService(ApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<SystemSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var row = await _context.SystemSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);
        if (row is null)
            throw new InvalidOperationException("SystemSettings row Id=1 is missing.");

        return row;
    }

    public void ApplyFinancials(Ride ride, SystemSettings settings, decimal distanceKm, RideStatus status)
    {
        ride.DistanceKm = decimal.Round(distanceKm, 2, MidpointRounding.AwayFromZero);
        ride.Price = decimal.Round(settings.BaseFare + ride.DistanceKm * settings.CostPerKm, 2,
            MidpointRounding.AwayFromZero);

        if (status == RideStatus.Canceled)
        {
            ride.DriverProfit = null;
            return;
        }

        var percentFee = decimal.Round(ride.Price * settings.PlatformFeePercentage, 2, MidpointRounding.AwayFromZero);
        ride.DriverProfit = decimal.Round(ride.Price - settings.PlatformFixedFee - percentFee, 2,
            MidpointRounding.AwayFromZero);
    }
}
