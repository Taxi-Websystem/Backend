using Backend.Models;
using Backend.Models.Enums;

namespace Backend.Services;

public interface IRidePricingService
{
    Task<SystemSettings> GetSettingsAsync(CancellationToken cancellationToken = default);

    void ApplyFinancials(Ride ride, SystemSettings settings, decimal distanceKm, RideStatus status);
}
