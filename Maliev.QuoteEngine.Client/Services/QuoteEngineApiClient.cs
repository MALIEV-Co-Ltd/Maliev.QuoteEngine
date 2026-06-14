using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Chatbot;
using Maliev.QuoteEngine.Shared.Quotes;
using Maliev.QuoteEngine.Shared.ReferenceData;
using Microsoft.AspNetCore.Components.Forms;

namespace Maliev.QuoteEngine.Client.Services;

public sealed class QuoteEngineApiClient(HttpClient httpClient)
{
    public async Task<QuoteReferenceDataResponse> GetReferenceDataAsync(CancellationToken cancellationToken = default)
    {
        return await GetFromJsonOrFallbackAsync(
            "quote/v1/reference-data",
            new QuoteReferenceDataResponse([], [], [], [], [], [], [], [], [], []),
            cancellationToken);
    }

    public async Task<IReadOnlyList<CurrencyOptionDto>> GetCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        return await GetFromJsonOrFallbackAsync<IReadOnlyList<CurrencyOptionDto>>(
            "quote/v1/reference-data/currencies",
            [],
            cancellationToken);
    }

    public async Task<QuoteEngineDemoProjectResponse?> GetDemoProjectAsync(CancellationToken cancellationToken = default)
    {
        return await GetFromJsonOrFallbackAsync<QuoteEngineDemoProjectResponse?>(
            "quote/v1/demo/project",
            null,
            cancellationToken);
    }

    public async Task<QuoteAuthStatusResponse> GetAuthStatusAsync(CancellationToken cancellationToken = default)
    {
        return await GetFromJsonOrFallbackAsync(
            "quote/v1/auth/session",
            new QuoteAuthStatusResponse(false, null, null),
            cancellationToken);
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

    public async Task<JsonDocument?> GetGeometryRuntimeManifestAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<JsonDocument>("quote/v1/geometry/runtime/manifest", cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    public Task<QuoteEstimateResponse> EstimateAsync(QuoteEstimateRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<QuoteEstimateRequest, QuoteEstimateResponse>("quote/v1/estimate", request, cancellationToken);
    }

    public Task<CreateDraftProjectResponse> CreateDraftProjectAsync(CreateDraftProjectRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CreateDraftProjectRequest, CreateDraftProjectResponse>("quote/v1/projects/draft", request, cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerProjectNavItemDto>> GetProjectNavigationAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerProjectNavItemDto>>(
            "quote/v1/projects/nav",
            cancellationToken) ?? [];
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

    public Task<ProjectManagementResponse> PinProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        return PostAsync<object, ProjectManagementResponse>($"quote/v1/projects/{projectId:D}/pin", new { }, cancellationToken);
    }

    public async Task<ProjectManagementResponse> UnpinProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var uri = $"quote/v1/projects/{projectId:D}/pin";
        using var response = await httpClient.DeleteAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(uri, response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<ProjectManagementResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("QuoteEngine returned an empty project management response.");
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

    public async Task<CustomerOrderDetailDto?> GetOrderDetailAsync(string orderNumber, CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<CustomerOrderDetailDto>(
            $"quote/v1/account/orders/{Uri.EscapeDataString(orderNumber)}",
            cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerNdaDto>> GetNdasAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerNdaDto>>("quote/v1/account/ndas", cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<CustomerDocumentDto>> GetDocumentsAsync(CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<IReadOnlyList<CustomerDocumentDto>>("quote/v1/account/documents", cancellationToken) ?? [];
    }

    public Task<CustomerDocumentDto> UploadDocumentAsync(CustomerDocumentUploadRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<CustomerDocumentUploadRequest, CustomerDocumentDto>("quote/v1/account/documents", request, cancellationToken);
    }

    public async Task<CustomerDocumentDto> UploadDocumentFileAsync(
        IBrowserFile file,
        string kind,
        string? orderNumber,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(string.IsNullOrWhiteSpace(kind) ? "PurchaseOrder" : kind), "Kind");
        if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            content.Add(new StringContent(orderNumber.Trim()), "OrderNumber");
        }

        var streamContent = new StreamContent(file.OpenReadStream(50_000_000, cancellationToken));
        streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        content.Add(streamContent, "File", file.Name);

        using var response = await httpClient.PostAsync("quote/v1/account/documents/upload", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CustomerDocumentDto>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("The QuoteEngine API returned an empty document upload response.");
    }

    public async Task<CustomerDocumentDownloadResponse> GetDocumentDownloadAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        return await httpClient.GetFromJsonAsync<CustomerDocumentDownloadResponse>(
            $"quote/v1/account/documents/{documentId:D}/download",
            cancellationToken)
            ?? throw new InvalidOperationException("The QuoteEngine API returned an empty document download response.");
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

    public Task<QuoteAgentTurnResponse> SendAgentMessageAsync(QuoteAgentMessageRequest request, CancellationToken cancellationToken = default)
    {
        return PostAsync<QuoteAgentMessageRequest, QuoteAgentTurnResponse>("quote/v1/agent/messages", request, cancellationToken);
    }

    public async Task<QuoteAgentStateResponse> GetAgentStateAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        return await GetFromJsonOrFallbackAsync(
            $"quote/v1/agent/sessions/{sessionId:D}",
            new QuoteAgentStateResponse { SessionId = sessionId },
            cancellationToken);
    }

    public Task<QuoteAgentActionResultResponse> ConfirmAgentActionAsync(
        Guid actionId,
        QuoteAgentConfirmActionRequest request,
        CancellationToken cancellationToken = default)
    {
        return PostAsync<QuoteAgentConfirmActionRequest, QuoteAgentActionResultResponse>(
            $"quote/v1/agent/actions/{actionId:D}/confirm",
            request,
            cancellationToken);
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string uri, TRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(uri, request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowApiExceptionAsync(uri, response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"The QuoteEngine API returned an empty response for {uri}.");
    }

    private static async Task ThrowApiExceptionAsync(
        string uri,
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var fallbackMessage = $"The QuoteEngine API returned {(int)response.StatusCode} for {uri}.";
        var detail = await TryReadProblemDetailAsync(response, cancellationToken);
        throw new QuoteEngineApiException(
            response.StatusCode,
            string.IsNullOrWhiteSpace(detail) ? fallbackMessage : detail,
            uri);
    }

    private static async Task<string?> TryReadProblemDetailAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var detail = ReadJsonString(root, "detail");
            return !string.IsNullOrWhiteSpace(detail)
                ? detail
                : ReadJsonString(root, "title");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string? ReadJsonString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private async Task<TResponse> GetFromJsonOrFallbackAsync<TResponse>(
        string uri,
        TResponse fallback,
        CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<TResponse>(uri, cancellationToken) ?? fallback;
        }
        catch (Exception ex) when (IsRecoverableStartupReadFailure(ex, cancellationToken))
        {
            return fallback;
        }
    }

    private static bool IsRecoverableStartupReadFailure(Exception exception, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested
            && exception is HttpRequestException or JsonException or NotSupportedException or TaskCanceledException;
    }
}

/// <summary>Represents a QuoteEngine API failure with a customer-safe message from ProblemDetails.</summary>
public sealed class QuoteEngineApiException(
    HttpStatusCode statusCode,
    string userMessage,
    string requestUri) : HttpRequestException(userMessage, null, statusCode)
{
    /// <summary>Gets the message that can be shown to the customer.</summary>
    public string UserMessage { get; } = userMessage;

    /// <summary>Gets the relative request URI that failed.</summary>
    public string RequestUri { get; } = requestUri;
}
