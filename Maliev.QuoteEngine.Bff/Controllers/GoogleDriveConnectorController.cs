using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maliev.QuoteEngine.Bff.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Handles the trusted Google Drive connector browser flow for Make Studio.
/// </summary>
[ApiController]
public sealed class GoogleDriveConnectorController(
    CustomerSessionResolver sessionResolver,
    IGoogleDriveConnectorStore connectorStore,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    private readonly IDataProtector _stateProtector = dataProtectionProvider.CreateProtector("quote-engine.google-drive.state.v1");

    /// <summary>
    /// Starts Google Drive incremental authorization for the signed-in customer.
    /// </summary>
    [HttpGet("quote/v1/connectors/google-drive/start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Start([FromQuery] string? returnUrl = null)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var clientId = GoogleClientId();
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(GoogleClientSecret()))
        {
            return Problem(
                title: "Google Drive connector is not configured.",
                detail: "Set Authentication:Google:ClientId and Authentication:Google:ClientSecret for Make Studio.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var state = _stateProtector.Protect(System.Text.Json.JsonSerializer.Serialize(new GoogleDriveOAuthState(
            customerId,
            NormalizeLocalReturnUrl(returnUrl),
            DateTimeOffset.UtcNow.AddMinutes(15))));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = ResolveRedirectUri(),
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["prompt"] = "consent",
            ["state"] = state
        };

        return Redirect(QueryHelpers.AddQueryString(
            "https://accounts.google.com/o/oauth2/v2/auth",
            query));
    }

    /// <summary>
    /// Completes Google Drive incremental authorization after Google returns an authorization code.
    /// </summary>
    [HttpGet("auth/google/drive/callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect("/quotes?connector=google-drive&status=cancelled");
        }

        if (string.IsNullOrWhiteSpace(code) || !TryReadState(state, out var connectorState))
        {
            return Problem(
                title: "Invalid Google Drive connector callback.",
                detail: "The connector authorization response could not be verified.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (connectorState.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return Problem(
                title: "Expired Google Drive connector callback.",
                detail: "Please start the Google Drive connection again.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var tokenResponse = await ExchangeCodeAsync(code, cancellationToken);
        connectorStore.Save(new GoogleDriveConnection(
            connectorState.CustomerId,
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokenResponse.ExpiresIn)),
            null,
            DateTimeOffset.UtcNow));

        return Redirect($"{connectorState.ReturnUrl}{(connectorState.ReturnUrl.Contains('?') ? '&' : '?')}connector=google-drive&status=connected");
    }

    /// <summary>
    /// Returns the signed-in customer's Google Drive connector status.
    /// </summary>
    [HttpGet("quote/v1/connectors/google-drive/status")]
    [ProducesResponseType(typeof(GoogleDriveConnectorStatusResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<GoogleDriveConnectorStatusResponse> Status()
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        return Ok(new GoogleDriveConnectorStatusResponse
        {
            ConnectorId = "google-drive",
            IsConnected = connectorStore.IsConnected(customerId)
        });
    }

    private async Task<GoogleDriveTokenResponse> ExchangeCodeAsync(string code, CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["client_id"] = GoogleClientId(),
            ["client_secret"] = GoogleClientSecret(),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = ResolveRedirectUri()
        });
        using var response = await httpClientFactory.CreateClient().PostAsync(
            "https://oauth2.googleapis.com/token",
            form,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GoogleDriveTokenResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Google did not return an OAuth token response.");
    }

    private bool TryReadState(string? state, out GoogleDriveOAuthState value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(state))
        {
            return false;
        }

        try
        {
            value = System.Text.Json.JsonSerializer.Deserialize<GoogleDriveOAuthState>(_stateProtector.Unprotect(state));
            return value.CustomerId != Guid.Empty;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private string? GoogleClientId()
    {
        return configuration["GoogleDrive:ClientId"] ?? configuration["Authentication:Google:ClientId"];
    }

    private string? GoogleClientSecret()
    {
        return configuration["GoogleDrive:ClientSecret"] ?? configuration["Authentication:Google:ClientSecret"];
    }

    private string ResolveRedirectUri()
    {
        if (!string.IsNullOrWhiteSpace(configuration["GoogleDrive:RedirectUri"]))
        {
            return configuration["GoogleDrive:RedirectUri"]!;
        }

        return $"{Request.Scheme}://{Request.Host}/auth/google/drive/callback";
    }

    private static string NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) || !returnUrl.StartsWith('/'))
        {
            return "/quotes";
        }

        return returnUrl.StartsWith("//", StringComparison.Ordinal) ? "/quotes" : returnUrl;
    }

    private readonly record struct GoogleDriveOAuthState(
        Guid CustomerId,
        string ReturnUrl,
        DateTimeOffset ExpiresAt);

    private sealed class GoogleDriveTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; set; }
    }
}

/// <summary>
/// Customer-safe Google Drive connector status.
/// </summary>
public sealed class GoogleDriveConnectorStatusResponse
{
    /// <summary>Gets or sets the connector ID.</summary>
    public string ConnectorId { get; set; } = "google-drive";

    /// <summary>Gets or sets whether the signed-in customer has connected Google Drive.</summary>
    public bool IsConnected { get; set; }
}
