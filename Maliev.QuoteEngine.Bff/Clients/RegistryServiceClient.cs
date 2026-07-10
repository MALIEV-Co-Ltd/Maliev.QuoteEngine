using System.Text.Json;
using Maliev.QuoteEngine.Shared.Account;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>Result of a Thai address registry lookup.</summary>
public sealed class ThaiAddressRegistryLookupResult
{
    /// <summary>Whether RegistryService returned a readable authoritative response.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Registry locations returned for the supplied lookup fields.</summary>
    public IReadOnlyList<ThaiAddressRegistryLocationDto> Locations { get; init; } = [];

    internal static ThaiAddressRegistryLookupResult Available(IReadOnlyList<ThaiAddressRegistryLocationDto> locations) =>
        new() { IsAvailable = true, Locations = locations };

    internal static ThaiAddressRegistryLookupResult Unavailable() => new();
}

public interface IRegistryServiceClient
{
    Task<HttpResponseMessage> SearchThaiLocationsAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>Searches the Thai address registry using a complete administrative hierarchy.</summary>
    Task<ThaiAddressRegistryLookupResult> SearchThaiAddressHierarchyAsync(
        string postalCode,
        string district,
        string city,
        string province,
        int limit,
        CancellationToken cancellationToken);
}

internal sealed class RegistryServiceClient(HttpClient httpClient) : IRegistryServiceClient
{
    public Task<HttpResponseMessage> SearchThaiLocationsAsync(string query, int limit, CancellationToken cancellationToken) =>
        httpClient.GetAsync($"/registry/v1/thai/addresses/autocomplete?query={Uri.EscapeDataString(query)}&limit={limit}", cancellationToken);

    public async Task<ThaiAddressRegistryLookupResult> SearchThaiAddressHierarchyAsync(
        string postalCode,
        string district,
        string city,
        string province,
        int limit,
        CancellationToken cancellationToken)
    {
        var requestUri = string.Concat(
            "/registry/v1/thai/addresses/autocomplete-multi?postalCode=",
            Uri.EscapeDataString(postalCode),
            "&district=",
            Uri.EscapeDataString(district),
            "&city=",
            Uri.EscapeDataString(city),
            "&province=",
            Uri.EscapeDataString(province),
            "&limit=",
            limit.ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return ThaiAddressRegistryLookupResult.Unavailable();
            }

            return await ThaiAddressRegistryResponseMapper.ParseAsync(response.Content, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return ThaiAddressRegistryLookupResult.Unavailable();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ThaiAddressRegistryLookupResult.Unavailable();
        }
        catch (IOException)
        {
            return ThaiAddressRegistryLookupResult.Unavailable();
        }
    }
}

internal static class ThaiAddressRegistryResponseMapper
{
    public static async Task<ThaiAddressRegistryLookupResult> ParseAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, out var successElement, "success", "Success") &&
            successElement.ValueKind == JsonValueKind.False)
        {
            return ThaiAddressRegistryLookupResult.Unavailable();
        }

        var data = root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, out var dataElement, "data", "Data")
                ? dataElement
                : root;
        if (data.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Thai address registry response did not contain an array payload.");
        }

        var locations = data
            .EnumerateArray()
            .Select(MapLocation)
            .OfType<ThaiAddressRegistryLocationDto>()
            .ToList();
        return ThaiAddressRegistryLookupResult.Available(locations);
    }

    private static ThaiAddressRegistryLocationDto? MapLocation(JsonElement root)
    {
        var location = new ThaiAddressRegistryLocationDto
        {
            Id = GetGuid(root, "id", "Id") ?? Guid.Empty,
            PostalCode = GetString(root, "postalCode", "PostalCode") ?? string.Empty,
            SubDistrictTh = GetString(root, "subDistrictTh", "SubDistrictTh") ?? string.Empty,
            DistrictTh = GetString(root, "districtTh", "DistrictTh") ?? string.Empty,
            ProvinceTh = GetString(root, "provinceTh", "ProvinceTh") ?? string.Empty,
            SubDistrictEn = GetString(root, "subDistrictEn", "SubDistrictEn") ?? string.Empty,
            DistrictEn = GetString(root, "districtEn", "DistrictEn") ?? string.Empty,
            ProvinceEn = GetString(root, "provinceEn", "ProvinceEn") ?? string.Empty
        };

        return string.IsNullOrWhiteSpace(location.PostalCode)
            && string.IsNullOrWhiteSpace(location.SubDistrictTh)
            && string.IsNullOrWhiteSpace(location.SubDistrictEn)
            && string.IsNullOrWhiteSpace(location.DistrictTh)
            && string.IsNullOrWhiteSpace(location.DistrictEn)
            && string.IsNullOrWhiteSpace(location.ProvinceTh)
            && string.IsNullOrWhiteSpace(location.ProvinceEn)
                ? null
                : location;
    }

    private static string? GetString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static Guid? GetGuid(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                Guid.TryParse(value.GetString(), out var guid))
            {
                return guid;
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement root, out JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out value))
            {
                return true;
            }
        }

        value = default;
        return false;
    }
}
