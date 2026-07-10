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

    /// <summary>Resolves a material alias to its authoritative identifier and canonical service code.</summary>
    async Task<MaterialCatalogResolution> ResolveMaterialAsync(
        string processCode,
        string materialCode,
        CancellationToken ct = default)
    {
        var id = await ResolveMaterialIdAsync(processCode, materialCode, ct);
        return new MaterialCatalogResolution(id, MaterialCatalogResolution.CanonicalizeCode(materialCode));
    }
}

/// <summary>Identifies one authoritative MaterialService material.</summary>
public sealed record MaterialCatalogResolution(Guid Id, string Code)
{
    /// <summary>Maps legacy QuoteEngine aliases to canonical MaterialService codes.</summary>
    public static string CanonicalizeCode(string materialCode)
    {
        var normalized = materialCode.Trim().Replace('_', '-').ToUpperInvariant();
        return normalized switch
        {
            "PLA-BLACK" => "PLA",
            "PETG-CLEAR" => "PETG",
            "RESIN-GRAY" => "STANDARD_RESIN",
            _ => normalized.Replace('-', '_')
        };
    }
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
        return (await ResolveMaterialAsync(processCode, materialCode, ct)).Id;
    }

    public async Task<MaterialCatalogResolution> ResolveMaterialAsync(
        string processCode,
        string materialCode,
        CancellationToken ct = default)
    {
        var key = $"qe:material:{processCode.ToUpperInvariant()}:{materialCode.ToUpperInvariant()}";
        if (cache.TryGetValue(key, out MaterialCatalogResolution? cached) && cached is not null) return cached;

        var canonicalCode = MaterialCatalogResolution.CanonicalizeCode(materialCode);

        try
        {
            var materials = await http.GetFromJsonAsync<MaterialCatalogItem[]>(
                $"/material/v1/manufacturing/processes/{Uri.EscapeDataString(processCode.ToUpperInvariant())}/materials", ct);

            if (materials is not null)
            {
                foreach (var m in materials)
                {
                    var catalogResolution = new MaterialCatalogResolution(m.Id, m.Code.ToUpperInvariant());
                    cache.Set(
                        $"qe:material:{processCode.ToUpperInvariant()}:{m.Code.ToUpperInvariant()}",
                        catalogResolution,
                        CacheOptions);
                }

                var match = materials.FirstOrDefault(m =>
                    m.Code.Equals(canonicalCode, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    var resolution = new MaterialCatalogResolution(match.Id, canonicalCode);
                    cache.Set(key, resolution, CacheOptions);
                    return resolution;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MaterialService material lookup failed for code '{Code}', using deterministic fallback.", materialCode);
        }

        // Fallback: deterministic Guid derived from the string code
        var fallback = new MaterialCatalogResolution(
            DeterministicGuid($"qe:material:{canonicalCode}"),
            canonicalCode);
        cache.Set(key, fallback, CacheOptions);
        return fallback;
    }

    private static Guid DeterministicGuid(string seed)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(seed));
        return new Guid(hash);
    }

    private sealed record ProcessCatalogItem(Guid Id, string Code, string Name);
    private sealed record MaterialCatalogItem(Guid Id, string Code, string Name);
}
