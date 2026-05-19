using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly TimeSpan MakesCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan ModelsCacheDuration = TimeSpan.FromHours(12);

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
        var trimmedQuery = (query ?? string.Empty).Trim();
        if (trimmedQuery.Length < 2)
            return Ok(new List<string>());

        var makes = await GetCarMakesAsync(cancellationToken);
        return Ok(FilterSuggestions(makes, trimmedQuery));
    }

    [HttpGet("models")]
    public async Task<ActionResult<List<string>>> GetModels(
        [FromQuery] string? make,
        [FromQuery] string? query,
        CancellationToken cancellationToken)
    {
        var trimmedMake = (make ?? string.Empty).Trim();
        var trimmedQuery = (query ?? string.Empty).Trim();

        if (trimmedMake.Length < 1 || trimmedQuery.Length < 2)
            return Ok(new List<string>());

        var models = await GetModelsByMakeAsync(trimmedMake, cancellationToken);
        return Ok(FilterSuggestions(models, trimmedQuery));
    }

    private async Task<List<string>> GetCarMakesAsync(CancellationToken cancellationToken)
    {
        const string cacheKey = "vpic:makes:car";
        return await GetOrCreateCachedListAsync(
            cacheKey,
            MakesCacheDuration,
            async () =>
            {
                var payload = await FetchVpicAsync<VpicMakeRow>(
                    $"{VpicBaseUrl}/GetMakesForVehicleType/car?format=json",
                    cancellationToken);

                return payload?.Results?
                    .Select(row => row.MakeName?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? [];
            });
    }

    private async Task<List<string>> GetModelsByMakeAsync(string make, CancellationToken cancellationToken)
    {
        var cacheKey = $"vpic:models:{make.ToLowerInvariant()}";
        return await GetOrCreateCachedListAsync(
            cacheKey,
            ModelsCacheDuration,
            async () =>
            {
                var encodedMake = Uri.EscapeDataString(make);
                var payload = await FetchVpicAsync<VpicModelRow>(
                    $"{VpicBaseUrl}/GetModelsForMake/{encodedMake}?format=json",
                    cancellationToken);

                return payload?.Results?
                    .Select(row => row.ModelName?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    ?? [];
            });
    }

    private async Task<List<string>> GetOrCreateCachedListAsync(
        string cacheKey,
        TimeSpan cacheDuration,
        Func<Task<List<string>>> factory)
    {
        if (_cache.TryGetValue(cacheKey, out List<string>? cachedList) && cachedList is not null)
            return cachedList;

        var loadedList = await factory();
        _cache.Set(cacheKey, loadedList, cacheDuration);
        return loadedList;
    }

    private async Task<VpicResponse<T>?> FetchVpicAsync<T>(string url, CancellationToken cancellationToken)
    {
        var httpClient = _httpClientFactory.CreateClient();
        var response = await httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<VpicResponse<T>>(stream, cancellationToken: cancellationToken);
    }

    private static List<string> FilterSuggestions(IEnumerable<string> source, string query)
    {
        var startsWithQuery = source
            .Where(name => name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .Take(SuggestionLimit)
            .ToList();

        if (startsWithQuery.Count >= SuggestionLimit)
            return startsWithQuery;

        var containsQuery = source
            .Where(name =>
                !name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                && name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(SuggestionLimit - startsWithQuery.Count);

        startsWithQuery.AddRange(containsQuery);
        return startsWithQuery;
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
