using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteUploadConstraintTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task ReferenceData_exposes_expanded_supported_cad_extensions()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(response);
        Assert.Contains("glb", response.SupportedExtensions);
        Assert.Contains("gltf", response.SupportedExtensions);
        Assert.Contains("fbx", response.SupportedExtensions);
        Assert.Contains("blend", response.SupportedExtensions);
        Assert.Contains(response.Processes, process => process.Id == "fdm" && process.SupportedFileTypes.Contains("glb"));
        Assert.Contains(response.Processes, process => process.Id == "cnc" && process.SupportedFileTypes.Contains("igs"));
    }

    [Fact]
    public async Task InitiateUpload_rejects_files_above_quote_engine_limit()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "oversized-part.stl",
            ContentType = "model/stl",
            FileSizeBytes = QuoteUploadConstraints.MaxFileSizeBytes + 1,
            QuoteSessionId = "upload-limit-test"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitiateUpload_rejects_unsupported_cad_extensions()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "drawing.pdf",
            ContentType = "application/pdf",
            FileSizeBytes = 1_024,
            QuoteSessionId = "upload-extension-test"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task InitiateUpload_accepts_supported_cad_extensions_within_limit()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "fixture.glb",
            ContentType = "model/gltf-binary",
            FileSizeBytes = QuoteUploadConstraints.MaxFileSizeBytes,
            QuoteSessionId = "upload-glb-test"
        });

        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();

        Assert.NotNull(upload);
        Assert.Equal(QuoteUploadConstraints.MaxFileSizeBytes, upload.ExpectedSizeBytes);
    }
}
