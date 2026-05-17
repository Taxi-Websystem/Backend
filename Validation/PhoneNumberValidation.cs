using Backend.Data;
using Microsoft.EntityFrameworkCore;

namespace Backend.Validation;

public static class PhoneNumberValidation
{
    public const string DuplicateMessage = "Цей номер телефону вже зареєстровано в системі.";
    public const string InvalidFormatMessage = "Некоректний формат телефону. Використовуйте +380XXXXXXXXX.";
    public const string PhoneTakenCode = "PHONE_TAKEN";

    public static string? Normalize(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var normalized = phone.Trim();
        if (!normalized.StartsWith("+380"))
            return null;

        return normalized.Length == 13 && normalized.Skip(1).All(char.IsDigit)
            ? normalized
            : null;
    }

    public static Task<bool> IsPhoneTakenAsync(
        ApplicationDbContext context,
        string normalizedPhone,
        int? excludeWhitelistId = null,
        CancellationToken cancellationToken = default) =>
        context.UserWhitelists.AnyAsync(
            w => w.PhoneNumber == normalizedPhone
                 && (!excludeWhitelistId.HasValue || w.Id != excludeWhitelistId.Value),
            cancellationToken);
}
