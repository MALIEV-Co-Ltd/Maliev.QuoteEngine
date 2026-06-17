using System.Net.Http.Json;
using System.Text.Json;

namespace Maliev.QuoteEngine.Bff.Clients;

public interface IPdfServiceClient
{
    Task<PdfGenerationResult?> GeneratePdfAsync(string documentType, string referenceId, object data, CancellationToken ct = default);
}

internal sealed class PdfServiceClient(HttpClient http, ILogger<PdfServiceClient> logger) : IPdfServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PdfGenerationResult?> GeneratePdfAsync(string documentType, string referenceId, object data, CancellationToken ct = default)
    {
        try
        {
            var request = new
            {
                templateCode = $"{documentType}-STD-01",
                referenceId,
                documentType,
                data
            };

            using var response = await http.PostAsJsonAsync("/pdf/v1/generations/generate", request, JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "PdfService returned {StatusCode} for {DocumentType}/{ReferenceId}: {Body}",
                    response.StatusCode,
                    documentType,
                    referenceId,
                    body);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PdfGenerationResult>(JsonOptions, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PdfService GeneratePdf failed for {DocumentType}/{ReferenceId}.", documentType, referenceId);
            return null;
        }
    }
}

public sealed class PdfGenerationResult
{
    public Guid RequestId { get; set; }
    public string StorageUrl { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
}
