using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maliev.QuoteEngine.Shared.Agent;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Tests;

[Collection(QuoteEngineEndpointTestCollection.Name)]
public sealed class QuoteAgentPreviewBuildEndpointTests(QuoteEngineWebApplicationFactory factory)
    : IClassFixture<QuoteEngineWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Preview_build_endpoint_marks_generated_viewer_failed_and_restores_ready_on_success()
    {
        using var client = factory.CreateClient();
        var sessionId = Guid.NewGuid();
        var commands = new[]
        {
            new
            {
                op = "box",
                id = "base",
                Params = new[] { 30.0, 20.0, 4.0 }
            }
        };

        await ExecuteToolAsync(client, sessionId, "quote_generate_3d_preview",
            new Dictionary<string, JsonElement>
            {
                ["description"] = JsonSerializer.SerializeToElement("Preview build endpoint plate", JsonOptions),
                ["cad_commands"] = JsonSerializer.SerializeToElement(commands, JsonOptions)
            });

        var initialState = await ExecuteToolForStateAsync(client, sessionId, "quote_get_state");
        var artifact = Assert.Single(initialState.Artifacts, IsGeneratedViewerArtifact);

        var failure = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/preview-build",
            new QuoteAgentPreviewBuildRequest { Success = false, ErrorClass = "invalid_geometry" },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        var failureBody = await failure.Content.ReadFromJsonAsync<QuoteAgentPreviewBuildResponse>(JsonOptions);
        Assert.NotNull(failureBody);
        Assert.NotNull(failureBody.State);
        var failedArtifact = Assert.Single(failureBody.State.Artifacts, item => item.ArtifactId == artifact.ArtifactId);
        Assert.Equal("build_failed", failedArtifact.Status);
        Assert.Equal("failed", failedArtifact.Metadata["previewBuildStatus"]);
        Assert.Equal("invalid_geometry", failedArtifact.Metadata["previewBuildErrorClass"]);
        Assert.True(failedArtifact.Metadata.ContainsKey("previewBuildFailedAt"));

        var success = await client.PostAsJsonAsync(
            $"/quote/v1/agent/sessions/{sessionId:D}/artifacts/{artifact.ArtifactId:D}/preview-build",
            new QuoteAgentPreviewBuildRequest { Success = true },
            JsonOptions);

        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var successBody = await success.Content.ReadFromJsonAsync<QuoteAgentPreviewBuildResponse>(JsonOptions);
        Assert.NotNull(successBody?.State);
        var restoredArtifact = Assert.Single(successBody.State.Artifacts, item => item.ArtifactId == artifact.ArtifactId);
        Assert.Equal("ready", restoredArtifact.Status);
        Assert.Equal("success", restoredArtifact.Metadata["previewBuildStatus"]);
        Assert.True(restoredArtifact.Metadata.ContainsKey("previewBuildSucceededAt"));
        Assert.False(restoredArtifact.Metadata.ContainsKey("previewBuildErrorClass"));
        Assert.False(restoredArtifact.Metadata.ContainsKey("previewBuildFailedAt"));
    }

    private static bool IsGeneratedViewerArtifact(QuoteAgentArtifactDto artifact)
    {
        return artifact.ArtifactType.Equals("viewer", StringComparison.OrdinalIgnoreCase) &&
            artifact.Metadata.TryGetValue("generated", out var generated) &&
            generated.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<QuoteAgentStateResponse> ExecuteToolForStateAsync(
        HttpClient client,
        Guid sessionId,
        string toolName,
        Dictionary<string, JsonElement>? arguments = null)
    {
        var json = await ExecuteToolAsync(client, sessionId, toolName, arguments);
        var state = JsonSerializer.Deserialize<QuoteAgentStateResponse>(json, JsonOptions);
        Assert.NotNull(state);
        return state;
    }

    private static async Task<string> ExecuteToolAsync(
        HttpClient client,
        Guid sessionId,
        string toolName,
        Dictionary<string, JsonElement>? arguments = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/quote/v1/agent/tools/{toolName}")
        {
            Content = JsonContent.Create(new QuoteAgentToolRequest
            {
                Arguments = arguments ?? []
            }, options: JsonOptions)
        };
        request.Headers.TryAddWithoutValidation(
            "X-Maliev-Agent-Context",
            CreateSignedAgentContextToken(sessionId, Guid.NewGuid()));

        using var response = await client.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return json;
    }

    private static string CreateSignedAgentContextToken(Guid quoteSessionId, Guid chatbotSessionId)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new
        {
            quoteSessionId,
            chatbotSessionId,
            customerId = (Guid?)null,
            issuedAt = now,
            expiresAt = now.AddMinutes(15)
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var encodedPayload = Base64UrlTextEncoder.Encode(Encoding.UTF8.GetBytes(json));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("maliev-local-development-quote-agent-context-key"));
        var signature = Base64UrlTextEncoder.Encode(hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload)));
        return $"{encodedPayload}.{signature}";
    }
}
