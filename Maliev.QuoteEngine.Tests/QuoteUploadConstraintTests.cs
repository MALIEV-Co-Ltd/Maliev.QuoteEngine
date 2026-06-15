using System.Net;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteUploadConstraintTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    [Fact]
    public async Task ReferenceData_exposes_expanded_supported_cad_extensions()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(response);
        Assert.Contains("step", response.SupportedExtensions);
        Assert.Contains("glb", response.SupportedExtensions);
        Assert.Contains("gltf", response.SupportedExtensions);
        Assert.Contains("3mf", response.SupportedExtensions);
        Assert.Contains("obj", response.SupportedExtensions);
        Assert.Contains("x_t", response.SupportedExtensions);
        Assert.Contains("sldprt", response.SupportedExtensions);
        Assert.Contains("catpart", response.SupportedExtensions);
        Assert.Contains("jt", response.SupportedExtensions);
        Assert.DoesNotContain("fbx", response.SupportedExtensions);
        Assert.DoesNotContain("blend", response.SupportedExtensions);
        Assert.Contains(response.Processes, process => process.Id == "fdm" && process.SupportedFileTypes.Contains("glb"));
        Assert.Contains(response.Processes, process => process.Id == "cnc" && process.SupportedFileTypes.Contains("igs"));
        Assert.Contains(response.Processes, process => process.Id == "cnc" && process.SupportedFileTypes.Contains("x_t"));
        Assert.Contains(response.Processes, process => process.Id == "cnc" && process.SupportedFileTypes.Contains("sldprt"));
    }

    [Fact]
    public void Upload_constraints_accept_quote_context_without_satisfying_geometry()
    {
        Assert.True(QuoteUploadConstraints.IsSupportedCadFileName("fixture.step"));
        Assert.True(QuoteUploadConstraints.IsSupportedCadFileName("fixture.stl"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("fixture.step"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("fixture.stl"));

        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("bracket-sketch.jpg"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("workbench-photo.png"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("requirements.pdf"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("flat-pattern.dxf"));
        Assert.True(QuoteUploadConstraints.IsSupportedAttachmentFileName("project-bundle.zip"));

        Assert.False(QuoteUploadConstraints.IsSupportedCadFileName("bracket-sketch.jpg"));
        Assert.False(QuoteUploadConstraints.IsSupportedCadFileName("requirements.pdf"));
        Assert.False(QuoteUploadConstraints.IsSupportedAttachmentFileName("macro.xlsm"));
        Assert.Contains(".jpg", QuoteUploadConstraints.SupportedAttachmentAccept);
        Assert.Contains(".pdf", QuoteUploadConstraints.SupportedAttachmentAccept);
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
    public async Task InitiateUpload_rejects_unsupported_quote_attachment_extensions()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = "macro.xlsm",
            ContentType = "application/vnd.ms-excel.sheet.macroEnabled.12",
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
            FileName = "fixture.x_t",
            ContentType = "application/octet-stream",
            FileSizeBytes = QuoteUploadConstraints.MaxFileSizeBytes,
            QuoteSessionId = "upload-parasolid-test"
        });

        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();

        Assert.NotNull(upload);
        Assert.Equal(QuoteUploadConstraints.MaxFileSizeBytes, upload.ExpectedSizeBytes);
    }

    [Theory]
    [InlineData("drawing.pdf", "application/pdf")]
    [InlineData("bracket-sketch.jpg", "image/jpeg")]
    [InlineData("flat-pattern.dxf", "application/dxf")]
    public async Task InitiateUpload_accepts_supplemental_quote_context_files(string fileName, string contentType)
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            FileName = fileName,
            ContentType = contentType,
            FileSizeBytes = 1_024,
            QuoteSessionId = "upload-context-test"
        });

        response.EnsureSuccessStatusCode();
        var upload = await response.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();

        Assert.NotNull(upload);
        Assert.Contains(Path.GetFileNameWithoutExtension(fileName), upload.StoragePath, StringComparison.OrdinalIgnoreCase);
    }
}
