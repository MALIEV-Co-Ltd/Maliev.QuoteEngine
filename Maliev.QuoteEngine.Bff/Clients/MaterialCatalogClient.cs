// Maliev.QuoteEngine.Bff/Clients/MaterialCatalogClient.cs
using Microsoft.Extensions.Caching.Memory;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>
/// Resolves QuoteEngine string material/process codes to MaterialService Guids.
/// Results are cached in-process for 1 hour to minimise round-trips.
/// </summary>
public interface IMaterialCatalogClient
{
    /// <summary>Resolves a process code (e.g. "fdm") to its MaterialService Guid.</summary>
    Task<Guid> ResolveProcessIdAsync(string processCode, CancellationToken ct = default);

    /// <summary>Resolves a material code (e.g. "pla-black") to its MaterialService Guid.</summary>
    Task<Guid> ResolveMaterialIdAsync(string processCode, string materialCode, CancellationToken ct = default);
}

internal sealed class MaterialCatalogClient(
    HttpClient http,
    IMemoryCache cache,
    ILogger<MaterialCatalogClient> logger) : IMaterialCatalogClient
{
    private static readonly MemoryCacheEntryOptions CacheOptions =
        new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromHours(1));

    public async Task<Guid> ResolveProcessIdAsync(string processCode, CancellationToken ct = default)
    {
        var key = $"qe:process:{processCode.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out Guid cached)) return cached;

        try
        {
            var processes = await http.GetFromJsonAsync<ProcessCatalogItem[]>(
                "/material/v1/manufacturing/processes", ct);

            if (processes is not null)
            {
                foreach (var p in processes)
                    cache.Set($"qe:process:{p.Code.ToUpperInvariant()}", p.Id, CacheOptions);

                var match = processes.FirstOrDefault(p =>
                    p.Code.Equals(processCode.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match.Id;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MaterialService process lookup failed for code '{Code}', using deterministic fallback.", processCode);
        }

        // Fallback: deterministic Guid derived from the string code
        return DeterministicGuid($"qe:process:{processCode.ToUpperInvariant()}");
    }

    public async Task<Guid> ResolveMaterialIdAsync(string processCode, string materialCode, CancellationToken ct = default)
    {
        var key = $"qe:material:{processCode.ToUpperInvariant()}:{materialCode.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out Guid cached)) return cached;

        try
        {
            var materials = await http.GetFromJsonAsync<MaterialCatalogItem[]>(
                $"/material/v1/manufacturing/processes/{Uri.EscapeDataString(processCode.ToUpperInvariant())}/materials", ct);

            if (materials is not null)
            {
                foreach (var m in materials)
                    cache.Set($"qe:material:{processCode.ToUpperInvariant()}:{m.Code.ToUpperInvariant()}", m.Id, CacheOptions);

                var match = materials.FirstOrDefault(m =>
                    m.Code.Equals(materialCode.Replace("-", string.Empty, StringComparison.Ordinal).Trim(), StringComparison.OrdinalIgnoreCase)
                    || m.Code.Equals(materialCode.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match.Id;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MaterialService material lookup failed for code '{Code}', using deterministic fallback.", materialCode);
        }

        // Fallback: deterministic Guid derived from the string code
        return DeterministicGuid($"qe:material:{materialCode.ToUpperInvariant()}");
    }

    private static Guid DeterministicGuid(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash);
    }

    private sealed record ProcessCatalogItem(Guid Id, string Code, string Name);
    private sealed record MaterialCatalogItem(Guid Id, string Code, string Name);
}
