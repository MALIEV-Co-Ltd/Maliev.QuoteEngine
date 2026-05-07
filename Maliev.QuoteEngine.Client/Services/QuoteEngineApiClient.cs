using System.Net.Http.Headers;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Components.Forms;

namespace Maliev.QuoteEngine.Client.Services;

public sealed class QuoteEngineApiClient(HttpClient httpClient)
{
    private const long MaxUploadSize = 10_737_418_240L;

    public async Task<QuoteReferenceDataResponse> GetReferenceDataAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<QuoteReferenceDataResponse>("quote/v1/reference-data", cancellationToken)
            ?? new QuoteReferenceDataResponse([], [], [], []);
    }

    public async Task<CompleteQuoteUploadResponse> UploadAsync(string quoteSessionId, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        var initiation = await PostAsync<InitiateQuoteUploadRequest, InitiateQuoteUploadResponse>(
            "quote/v1/uploads/resumable",
            new InitiateQuoteUploadRequest
            {
                QuoteSessionId = quoteSessionId,
                FileName = file.Name,
                ContentType = file.ContentType,
                FileSizeBytes = file.Size
            },
            cancellationToken);

        using var content = new StreamContent(file.OpenReadStream(MaxUploadSize, cancellationToken));
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        content.Headers.ContentLength = file.Size;
        content.Headers.ContentRange = new ContentRangeHeaderValue(0, file.Size - 1, file.Size);

        using var put = await httpClient.PutAsync(initiation.ProxyUploadUrl.TrimStart('/'), content, cancellationToken);
        put.EnsureSuccessStatusCode();

        return await PostAsync<object, CompleteQuoteUploadResponse>(
            $"quote/v1/uploads/resumable/{Uri.EscapeDataString(initiation.UploadId)}/complete",
            new { },
            cancellationToken);
    }

    public async Task<QuoteAnalysisStatusResponse> GetAnalysisStatusAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<QuoteAnalysisStatusResponse>($"quote/v1/uploads/{Uri.EscapeDataString(uploadId)}/analysis-status", cancellationToken)
            ?? new QuoteAnalysisStatusResponse { UploadId = uploadId };
    }

    public Task<QuoteEstimateResponse> EstimateAsync(QuoteEstimateRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<QuoteEstimateRequest, QuoteEstimateResponse>("quote/v1/estimate", request, cancellationToken);
    }

    public Task<CreateDraftProjectResponse> CreateDraftProjectAsync(CreateDraftProjectRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateDraftProjectRequest, CreateDraftProjectResponse>("quote/v1/projects/draft", request, cancellationToken);
    }

    public Task<GenerateFormalQuoteResponse> GenerateFormalQuoteAsync(GenerateFormalQuoteRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<GenerateFormalQuoteRequest, GenerateFormalQuoteResponse>("quote/v1/quotes/formal", request, cancellationToken);
    }

    public Task<CreateManufacturingOrderResponse> CreateOrderAsync(CreateManufacturingOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateManufacturingOrderRequest, CreateManufacturingOrderResponse>("quote/v1/orders", request, cancellationToken);
    }

    public async Task<CustomerProfileResponse?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<CustomerProfileResponse>("quote/v1/account/profile", cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerQuoteSummaryDto>> GetQuotesAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerQuoteSummaryDto>>("quote/v1/account/quotes", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CustomerOrderSummaryDto>> GetOrdersAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerOrderSummaryDto>>("quote/v1/account/orders", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CustomerNdaDto>> GetNdasAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerNdaDto>>("quote/v1/account/ndas", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CustomerDocumentDto>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerDocumentDto>>("quote/v1/account/documents", cancellationToken) ?? [];
    }

    public Task<AuthSessionResponse> SignInAsync(SignInRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SignInRequest, AuthSessionResponse>("quote/v1/auth/sign-in", request, cancellationToken);
    }

    public Task<AuthSessionResponse> SignUpAsync(SignUpRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<SignUpRequest, AuthSessionResponse>("quote/v1/auth/sign-up", request, cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string uri, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(uri, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"The QuoteEngine API returned an empty response for {uri}.");
    }
}
