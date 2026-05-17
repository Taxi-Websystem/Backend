using Backend.Models;

namespace Backend.Validation;

public static class SystemSettingsValidation
{
    public static string? ValidateUpdate(UpdateFinancialSettingsDto dto)
    {
        if (dto.BaseFare < 0 || dto.CostPerKm < 0 || dto.PlatformFixedFee < 0)
            return "Тарифи не можуть бути від’ємними.";

        if (dto.PlatformFeePercentage < 0 || dto.PlatformFeePercentage > 1)
            return "Комісія має бути від 0 до 1 (наприклад 0.10 для 10%).";

        return null;
    }

    public static void ApplyRoundedValues(SystemSettings row, UpdateFinancialSettingsDto dto)
    {
        row.BaseFare = decimal.Round(dto.BaseFare, 2, MidpointRounding.AwayFromZero);
        row.CostPerKm = decimal.Round(dto.CostPerKm, 2, MidpointRounding.AwayFromZero);
        row.PlatformFixedFee = decimal.Round(dto.PlatformFixedFee, 2, MidpointRounding.AwayFromZero);
        row.PlatformFeePercentage = decimal.Round(dto.PlatformFeePercentage, 4, MidpointRounding.AwayFromZero);
    }
}
