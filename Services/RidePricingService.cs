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
        var systemSettings = await _context.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == 1, cancellationToken);

        if (systemSettings is null)
            throw new InvalidOperationException("SystemSettings row Id=1 is missing.");

        return systemSettings;
    }

    public void ApplyFinancials(Ride ride, SystemSettings settings, decimal distanceKm, RideStatus status)
    {
        ride.DistanceKm = RoundMoney(distanceKm);
        ride.Price = RoundMoney(settings.BaseFare + ride.DistanceKm * settings.CostPerKm);

        if (status == RideStatus.Canceled)
        {
            ride.DriverProfit = null;
            return;
        }

        var percentFee = RoundMoney(ride.Price * settings.PlatformFeePercentage);
        ride.DriverProfit = RoundMoney(ride.Price - settings.PlatformFixedFee - percentFee);
    }

    private static decimal RoundMoney(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
