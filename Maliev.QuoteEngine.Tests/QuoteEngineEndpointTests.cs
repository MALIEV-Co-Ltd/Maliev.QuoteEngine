using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Maliev.QuoteEngine.Shared.Account;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteEngineEndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task ReferenceData_exposes_customer_visible_processes_and_materials()
    {
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<QuoteReferenceDataResponse>("/quote/v1/reference-data");

        Assert.NotNull(response);
        Assert.Contains(response.Processes, process => process.Id == "fdm");
        Assert.Contains(response.Materials, material => material.ProcessId == "cnc");
        Assert.Contains("step", response.SupportedExtensions);
    }

    [Fact]
    public async Task DemoProject_is_non_mutating_sample_journey()
    {
        using var client = factory.CreateClient();

        var demo = await client.GetFromJsonAsync<QuoteEngineDemoProjectResponse>("/quote/v1/demo/project");

        Assert.NotNull(demo);
        Assert.Contains("sample", demo.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Single(demo.Parts);
        Assert.Contains("does not create", demo.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ResumableUpload_requires_signed_in_customer()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-unsigned",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ResumableUpload_requires_content_range_and_returns_analysis_metrics()
    {
        using var client = CreateSignedInClient();
        var initiation = await client.PostAsJsonAsync("/quote/v1/uploads/resumable", new InitiateQuoteUploadRequest
        {
            QuoteSessionId = "session-1",
            FileName = "part.stl",
            ContentType = "model/stl",
            FileSizeBytes = 12
        });
        initiation.EnsureSuccessStatusCode();
        var upload = await initiation.Content.ReadFromJsonAsync<InitiateQuoteUploadResponse>();
        Assert.NotNull(upload);

        using var badContent = new ByteArrayContent([1, 2, 3]);
        var badPut = await client.PutAsync(upload.ProxyUploadUrl, badContent);
        Assert.Equal(HttpStatusCode.BadRequest, badPut.StatusCode);

        using var goodContent = new ByteArrayContent([1, 2, 3, 4]);
        goodContent.Headers.ContentType = MediaTypeHeaderValue.Parse("model/stl");
        goodContent.Headers.ContentRange = new ContentRangeHeaderValue(0, 3, 4);
        var goodPut = await client.PutAsync(upload.ProxyUploadUrl, goodContent);
        Assert.Equal(HttpStatusCode.NoContent, goodPut.StatusCode);

        var complete = await client.PostAsJsonAsync($"/quote/v1/uploads/resumable/{upload.UploadId}/complete", new { });
        complete.EnsureSuccessStatusCode();

        var status = await client.GetFromJsonAsync<QuoteAnalysisStatusResponse>($"/quote/v1/uploads/{upload.UploadId}/analysis-status");
        Assert.NotNull(status);
        Assert.Equal("Analyzed", status.Status);
        Assert.True(status.VolumeCc > 0);
        Assert.NotEmpty(status.Findings);
    }

    [Fact]
    public async Task Estimate_uses_part_geometry_and_requires_sign_in_for_formal_quote()
    {
        using var client = factory.CreateClient();
        var estimate = await client.PostAsJsonAsync("/quote/v1/estimate", new QuoteEstimateRequest
        {
            QuoteSessionId = "session-2",
            LeadTimeCode = "STANDARD",
            Parts =
            [
                new QuotePartDraftDto
                {
                    FileId = Guid.NewGuid(),
                    UploadId = "upload-1",
                    FileName = "bracket.step",
                    ProcessId = "cnc",
                    MaterialId = "al6061",
                    Quantity = 2,
                    VolumeCc = 8.5m,
                    DfmAcknowledged = true
                }
            ]
        });
        estimate.EnsureSuccessStatusCode();

        var body = await estimate.Content.ReadFromJsonAsync<QuoteEstimateResponse>();
        Assert.NotNull(body);
        Assert.True(body.Total > 0);
        Assert.True(body.RequiresSignIn);
        Assert.Single(body.Lines);
    }

    [Fact]
    public async Task Account_profile_is_resolved_server_side_from_session_boundary()
    {
        using var client = CreateSignedInClient();

        var profile = await client.GetFromJsonAsync<CustomerProfileResponse>("/quote/v1/account/profile");

        Assert.NotNull(profile);
        Assert.NotEqual(Guid.Empty, profile.CustomerId);
        Assert.Contains("@", profile.Email, StringComparison.Ordinal);
    }

    private HttpClient CreateSignedInClient()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", "maliev_quote_customer=aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        return client;
    }
}
