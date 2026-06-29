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

    /// <summary>
    /// Disconnects Google Drive for the signed-in customer.
    /// </summary>
    [HttpPost("quote/v1/connectors/google-drive/disconnect")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Disconnect()
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        connectorStore.Remove(customerId);
        return NoContent();
    }

    /// <summary>
    /// Lists Google Drive files available to attach to the current Make Studio message.
    /// </summary>
    [HttpGet("quote/v1/connectors/google-drive/files")]
    [ProducesResponseType(typeof(GoogleDriveFileListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GoogleDriveFileListResponse>> ListFiles(
        [FromQuery] string? query = null,
        [FromQuery] int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        var connection = connectorStore.Get(customerId);
        if (connection is null)
        {
            return Conflict(new ProblemDetails
            {
                Title = "Google Drive is not connected.",
                Detail = "Connect Google Drive before browsing Drive files.",
                Status = StatusCodes.Status409Conflict
            });
        }

        var accessToken = await GetUsableAccessTokenAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            connectorStore.Remove(customerId);
            return Conflict(new ProblemDetails
            {
                Title = "Google Drive needs to be reconnected.",
                Detail = "The previous Google Drive authorization expired. Please connect Google Drive again.",
                Status = StatusCodes.Status409Conflict
            });
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildDriveFilesUrl(query, limit));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            connectorStore.Remove(customerId);
            return Conflict(new ProblemDetails
            {
                Title = "Google Drive needs to be reconnected.",
                Detail = "Google rejected the stored Drive authorization. Please connect Google Drive again.",
                Status = StatusCodes.Status409Conflict
            });
        }

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<GoogleDriveFileListResponse>(cancellationToken)
            ?? new GoogleDriveFileListResponse();
        result.Files = result.Files
            .Where(file => !string.IsNullOrWhiteSpace(file.Id) && !string.IsNullOrWhiteSpace(file.Name))
            .ToList();
        return Ok(result);
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

    private async Task<string?> GetUsableAccessTokenAsync(GoogleDriveConnection connection, CancellationToken cancellationToken)
    {
        if (connection.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1))
        {
            return connection.AccessToken;
        }

        if (string.IsNullOrWhiteSpace(connection.RefreshToken))
        {
            return null;
        }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["client_id"] = GoogleClientId(),
            ["client_secret"] = GoogleClientSecret(),
            ["refresh_token"] = connection.RefreshToken,
            ["grant_type"] = "refresh_token"
        });
        using var response = await httpClientFactory.CreateClient().PostAsync(
            "https://oauth2.googleapis.com/token",
            form,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<GoogleDriveTokenResponse>(cancellationToken);
        if (tokenResponse is null || string.IsNullOrWhiteSpace(tokenResponse.AccessToken))
        {
            return null;
        }

        var refreshed = connection with
        {
            AccessToken = tokenResponse.AccessToken,
            RefreshToken = tokenResponse.RefreshToken ?? connection.RefreshToken,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, tokenResponse.ExpiresIn))
        };
        connectorStore.Save(refreshed);
        return refreshed.AccessToken;
    }

    private static string BuildDriveFilesUrl(string? query, int limit)
    {
        var pageSize = Math.Clamp(limit, 1, 50);
        var driveQuery = "trashed = false";
        if (!string.IsNullOrWhiteSpace(query))
        {
            var escaped = query.Trim().Replace("'", "\\'", StringComparison.Ordinal);
            driveQuery = $"{driveQuery} and name contains '{escaped}'";
        }

        return QueryHelpers.AddQueryString("https://www.googleapis.com/drive/v3/files", new Dictionary<string, string?>
        {
            ["pageSize"] = pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["q"] = driveQuery,
            ["fields"] = "files(id,name,mimeType,size,modifiedTime,iconLink,thumbnailLink,webViewLink)"
        });
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
        return GoogleDriveOAuthConfiguration.ClientId(configuration);
    }

    private string? GoogleClientSecret()
    {
        return GoogleDriveOAuthConfiguration.ClientSecret(configuration);
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

/// <summary>
/// Customer-safe Google Drive file list response.
/// </summary>
public sealed class GoogleDriveFileListResponse
{
    /// <summary>Gets or sets the Drive files.</summary>
    [JsonPropertyName("files")]
    public List<GoogleDriveFileResponse> Files { get; set; } = [];
}

/// <summary>
/// Customer-safe Google Drive file metadata.
/// </summary>
public sealed class GoogleDriveFileResponse
{
    /// <summary>Gets or sets the Drive file ID.</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the file name.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the MIME type.</summary>
    [JsonPropertyName("mimeType")]
    public string MimeType { get; set; } = string.Empty;

    /// <summary>Gets or sets the file size in bytes when Google provides it.</summary>
    [JsonPropertyName("size")]
    public string? Size { get; set; }

    /// <summary>Gets or sets the modified time when Google provides it.</summary>
    [JsonPropertyName("modifiedTime")]
    public DateTimeOffset? ModifiedTime { get; set; }

    /// <summary>Gets or sets the Google Drive icon link.</summary>
    [JsonPropertyName("iconLink")]
    public string? IconLink { get; set; }

    /// <summary>Gets or sets the thumbnail link when available.</summary>
    [JsonPropertyName("thumbnailLink")]
    public string? ThumbnailLink { get; set; }

    /// <summary>Gets or sets the browser view link when available.</summary>
    [JsonPropertyName("webViewLink")]
    public string? WebViewLink { get; set; }
}
