using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Chatbot;
using Maliev.QuoteEngine.Shared.Quotes;
using Maliev.QuoteEngine.Shared.ReferenceData;
using Microsoft.AspNetCore.Components.Forms;

namespace Maliev.QuoteEngine.Client.Services;

public sealed class QuoteEngineApiClient(HttpClient httpClient)
{
    public async Task<QuoteReferenceDataResponse> GetReferenceDataAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<QuoteReferenceDataResponse>("quote/v1/reference-data", cancellationToken)
            ?? new QuoteReferenceDataResponse([], [], [], [], [], [], [], [], []);
    }

    public async Task<IReadOnlyList<CurrencyOptionDto>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CurrencyOptionDto>>("quote/v1/reference-data/currencies", cancellationToken) ?? [];
    }

    public async Task<QuoteEngineDemoProjectResponse?> GetDemoProjectAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<QuoteEngineDemoProjectResponse>("quote/v1/demo/project", cancellationToken);
    }

    public async Task<QuoteAuthStatusResponse> GetAuthStatusAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<QuoteAuthStatusResponse>("quote/v1/auth/session", cancellationToken)
            ?? new QuoteAuthStatusResponse(false, null, null);
    }

    public Task<InitiateQuoteUploadResponse> InitiateUploadAsync(string quoteSessionId, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        return InitiateUploadAsync(
            quoteSessionId,
            file.Name,
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            file.Size,
            cancellationToken);
    }

    public Task<InitiateQuoteUploadResponse> InitiateUploadAsync(
        string quoteSessionId,
        string fileName,
        string contentType,
        long fileSizeBytes,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<InitiateQuoteUploadRequest, InitiateQuoteUploadResponse>(
            "quote/v1/uploads/resumable",
            new InitiateQuoteUploadRequest
            {
                QuoteSessionId = quoteSessionId,
                FileName = fileName,
                ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType,
                FileSizeBytes = fileSizeBytes
            },
            cancellationToken);
    }

    public Task<QuoteUploadHandoffResponse> ImportHandoffAsync(QuoteUploadHandoffRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<QuoteUploadHandoffRequest, QuoteUploadHandoffResponse>("quote/v1/uploads/handoff", request, cancellationToken);
    }

    public async Task<CompleteQuoteUploadResponse> CompleteUploadAsync(string uploadId, CancellationToken cancellationToken = default)
    {
        return await PostAsync<object, CompleteQuoteUploadResponse>(
            $"quote/v1/uploads/resumable/{Uri.EscapeDataString(uploadId)}/complete",
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

    public Task<DuplicateDraftProjectResponse> DuplicateProjectAsync(
        Guid projectId,
        DuplicateDraftProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<DuplicateDraftProjectRequest, DuplicateDraftProjectResponse>(
            $"quote/v1/projects/{projectId:D}/duplicate",
            request,
            cancellationToken);
    }

    public Task<GenerateFormalQuoteResponse> GenerateFormalQuoteAsync(GenerateFormalQuoteRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<GenerateFormalQuoteRequest, GenerateFormalQuoteResponse>("quote/v1/quotes/formal", request, cancellationToken);
    }

    public Task<GenerateFormalQuoteResponse> ApproveQuoteAsync(Guid quoteId, CancellationToken cancellationToken = default)
    {
        return PostAsync<object, GenerateFormalQuoteResponse>($"quote/v1/quotes/{quoteId:D}/approve", new { }, cancellationToken);
    }

    public Task<CreateManufacturingOrderResponse> CreateOrderAsync(CreateManufacturingOrderRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateManufacturingOrderRequest, CreateManufacturingOrderResponse>("quote/v1/orders", request, cancellationToken);
    }

    public Task<InitiatePaymentResponse> InitiatePaymentAsync(InitiatePaymentRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<InitiatePaymentRequest, InitiatePaymentResponse>("quote/v1/payments", request, cancellationToken);
    }

    public async Task<CustomerProfileResponse?> GetProfileAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<CustomerProfileResponse>("quote/v1/account/profile", cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerAddressDto>> GetAddressesAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerAddressDto>>("quote/v1/account/addresses", cancellationToken) ?? [];
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

    public Task<CustomerChatbotResponse> SendChatbotMessageAsync(CustomerChatbotRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CustomerChatbotRequest, CustomerChatbotResponse>("quote/v1/chatbot/messages", request, cancellationToken);
    }

    public Task<CustomerChatbotHydrateResponse> HydrateChatbotAsync(CustomerChatbotHydrateRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CustomerChatbotHydrateRequest, CustomerChatbotHydrateResponse>("quote/v1/chatbot/hydrate", request, cancellationToken);
    }

    public async Task<CustomerChatbotSessionResponse> GetChatbotSessionAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<CustomerChatbotSessionResponse>("quote/v1/chatbot/session", cancellationToken)
            ?? new CustomerChatbotSessionResponse();
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string uri, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(uri, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"The QuoteEngine API returned an empty response for {uri}.");
    }
}
