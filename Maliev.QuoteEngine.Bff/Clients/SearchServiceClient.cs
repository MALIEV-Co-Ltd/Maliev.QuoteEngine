using System.Net.Http.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

/// <summary>Bounded candidate project identifiers returned by SearchService.</summary>
public sealed class ProjectSearchIndexResult
{
    /// <summary>Whether SearchService returned a readable authoritative response.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Candidate ProjectService identifiers in relevance order.</summary>
    public IReadOnlyList<Guid> ProjectIds { get; init; } = [];

    public static ProjectSearchIndexResult Available(IReadOnlyList<Guid> projectIds) =>
        new() { IsAvailable = true, ProjectIds = projectIds };

    public static ProjectSearchIndexResult Unavailable() => new();
}

public interface ISearchServiceClient
{
    /// <summary>Searches SearchService for ProjectService project candidates.</summary>
    Task<ProjectSearchIndexResult> SearchProjectsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default);
}

internal sealed class SearchServiceClient(HttpClient http, ILogger<SearchServiceClient> logger)
    : ISearchServiceClient
{
    public async Task<ProjectSearchIndexResult> SearchProjectsAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = query.Trim();
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var path = string.Concat(
            "/search/v1/search?query=",
            Uri.EscapeDataString(normalizedQuery),
            "&limit=",
            normalizedLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "&type=project&area=ProjectService");

        try
        {
            using var response = await http.GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "SearchService returned {StatusCode} for QuoteEngine project search.",
                    response.StatusCode);
                return ProjectSearchIndexResult.Unavailable();
            }

            var payload = await response.Content.ReadFromJsonAsync<SearchServiceResponse>(
                cancellationToken: cancellationToken);
            if (payload is null)
            {
                return ProjectSearchIndexResult.Unavailable();
            }

            var projectIds = payload.Results
                .Where(result =>
                    string.Equals(result.SourceService, "ProjectService", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(result.ResourceType, "project", StringComparison.OrdinalIgnoreCase))
                .Select(result => Guid.TryParse(result.ResourceId, out var projectId) ? projectId : Guid.Empty)
                .Where(projectId => projectId != Guid.Empty)
                .Distinct()
                .ToArray();
            return ProjectSearchIndexResult.Available(projectIds);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SearchService timed out during QuoteEngine project search.");
            return ProjectSearchIndexResult.Unavailable();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SearchService project search failed.");
            return ProjectSearchIndexResult.Unavailable();
        }
    }

    private sealed class SearchServiceResponse
    {
        public List<SearchServiceResult> Results { get; set; } = [];
    }

    private sealed class SearchServiceResult
    {
        public string SourceService { get; set; } = string.Empty;

        public string ResourceType { get; set; } = string.Empty;

        public string ResourceId { get; set; } = string.Empty;
    }
}
