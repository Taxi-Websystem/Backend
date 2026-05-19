using System.Text.Json;
using System.Text.Json.Serialization;
using Backend.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Backend.Controllers;

[ApiController]
[Route("api/vehicle-catalog")]
[Authorize]
public class VehicleCatalogController : ControllerBase
{
    private const string VpicBaseUrl = "https://vpic.nhtsa.dot.gov/api/vehicles";
    private const int SuggestionLimit = 12;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;

    public VehicleCatalogController(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
    }

    [HttpGet("makes")]
    public async Task<ActionResult<List<string>>> GetMakes([FromQuery] string? query, CancellationToken cancellationToken)
    {
        var trimmed = (query ?? string.Empty).Trim();
        if (trimmed.Length < 2)
            return Ok(new List<string>());

        var makes = await GetCarMakesAsync(cancellationToken);
        return Ok(FilterSuggestions(makes, trimmed));
    }

    [HttpGet("models")]
    public async Task<ActionResult<List<string>>> GetModels(
        [FromQuery] string? make,
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var makeTrimmed = (make ?? string.Empty).Trim();
        var queryTrimmed = (query ?? string.Empty).Trim();

        if (makeTrimmed.Length < 1 || queryTrimmed.Length < 2)
            return Ok(new List<string>());

        var models = await GetModelsByMakeAsync(makeTrimmed, cancellationToken);
        return Ok(FilterSuggestions(models, queryTrimmed));
    }

    private async Task<List<string>> GetCarMakesAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "vpic:makes:car";
        if (_cache.TryGetValue(cacheKey, out List<string>? cached) && cached is not null)
            return cached;

        var client = _httpClientFactory.CreateClient();
        var response = await client.GetAsync(
            $"{VpicBaseUrl}/GetMakesForVehicleType/car?format=json",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<VpicResponse<VpicMakeRow>>(
            stream,
            cancellationToken: cancellationToken);

        var makes = payload?.Results?
            .Select(r => r.MakeName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        _cache.Set(cacheKey, makes, TimeSpan.FromHours(24));
        return makes;
    }

    private async Task<List<string>> GetModelsByMakeAsync(string make, CancellationToken cancellationToken)
    {
        var cacheKey = $"vpic:models:{make.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out List<string>? cached) && cached is not null)
            return cached;

        var client = _httpClientFactory.CreateClient();
        var encodedMake = Uri.EscapeDataString(make);
        var response = await client.GetAsync(
            $"{VpicBaseUrl}/GetModelsForMake/{encodedMake}?format=json",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<VpicResponse<VpicModelRow>>(
            stream,
            cancellationToken: cancellationToken);

        var models = payload?.Results?
            .Select(r => r.ModelName?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

        _cache.Set(cacheKey, models, TimeSpan.FromHours(12));
        return models;
    }

    private static List<string> FilterSuggestions(IEnumerable<string> source, string query)
    {
        var starts = source
            .Where(name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(SuggestionLimit)
            .ToList();

        if (starts.Count >= SuggestionLimit)
            return starts;

        var rest = source
            .Where(name =>
                !name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                && name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(SuggestionLimit - starts.Count);

        starts.AddRange(rest);
        return starts;
    }

    private sealed class VpicResponse<T>
    {
        [JsonPropertyName("Results")]
        public List<T> Results { get; set; } = [];
    }

    private sealed class VpicMakeRow
    {
        [JsonPropertyName("MakeName")]
        public string? MakeName { get; set; }
    }

    private sealed class VpicModelRow
    {
        [JsonPropertyName("Model_Name")]
        public string? ModelName { get; set; }
    }
}
