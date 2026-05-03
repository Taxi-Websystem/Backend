using System.Text.RegularExpressions;

namespace Backend.Validation;

/// <summary>Валідація марки/моделі (латиниця) та кольору (кирилиця), узгоджено з фронтендом.</summary>
public static class CarFieldValidation
{
    private static readonly Regex LatinBrandModel = new(@"^[A-Za-z0-9\s\-]+$", RegexOptions.Compiled);
    private static readonly Regex CyrillicColor = new(@"^[\p{IsCyrillic}\s]+$", RegexOptions.Compiled);

    public static bool IsValidCarBrandOrModel(string? s) =>
        !string.IsNullOrWhiteSpace(s) && LatinBrandModel.IsMatch(s.Trim());

    public static bool IsValidCarColorUa(string? s) =>
        !string.IsNullOrWhiteSpace(s) && CyrillicColor.IsMatch(s.Trim());
}
