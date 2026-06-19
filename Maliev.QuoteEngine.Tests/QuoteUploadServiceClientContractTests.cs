using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.QuoteEngine.Bff.Clients;
using Microsoft.Extensions.Logging.Abstractions;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteUploadServiceClientContractTests
{
    [Fact]
    public async Task GetDownloadUrlByPathAsync_UsesUploadServiceByPathSignedUrlContract()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                signedUrl = "https://mock-storage.local/signed/customer-document",
                expiresAt = DateTime.UtcNow.AddMinutes(15),
                storagePath = "customer-documents/customer-id/document.pdf"
            })
        });
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://upload-service.test")
        };
        var client = new QuoteUploadServiceClient(http, NullLogger<QuoteUploadServiceClient>.Instance);

        var url = await client.GetDownloadUrlByPathAsync(
            "customer-documents/customer-id/document.pdf",
            expirationMinutes: 15);

        Assert.Equal("https://mock-storage.local/signed/customer-document", url);
        Assert.Equal(HttpMethod.Post, handler.Request?.Method);
        Assert.Equal("/upload/v1/files/by-path/signed-url", handler.Request?.RequestUri?.AbsolutePath);
        using var payload = JsonDocument.Parse(handler.RequestBody);
        Assert.Equal("customer-documents/customer-id/document.pdf", payload.RootElement.GetProperty("storagePath").GetString());
        Assert.Equal(15, payload.RootElement.GetProperty("expirationMinutes").GetInt32());
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }
}
