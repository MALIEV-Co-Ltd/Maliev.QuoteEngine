using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
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
    QuoteEnginePrototypeStore store,
    QuoteUploadServiceClient uploadClient,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) : ControllerBase
{
    private const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    private const int MaxPickerFiles = 8;
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
    /// Returns client-safe Google Picker configuration for the signed-in customer.
    /// </summary>
    [HttpGet("quote/v1/connectors/google-drive/picker-config")]
    [ProducesResponseType(typeof(GoogleDrivePickerConfigResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public ActionResult<GoogleDrivePickerConfigResponse> PickerConfig()
    {
        if (!sessionResolver.TryResolveCustomerId(out _))
        {
            return Unauthorized();
        }

        var clientId = GoogleClientId();
        var developerKey = GoogleDriveOAuthConfiguration.PickerApiKey(configuration);
        var appId = GoogleDriveOAuthConfiguration.PickerAppId(configuration);
        if (string.IsNullOrWhiteSpace(clientId) ||
            string.IsNullOrWhiteSpace(developerKey) ||
            string.IsNullOrWhiteSpace(appId))
        {
            return Problem(
                title: "Google Drive Picker is not configured.",
                detail: "Set GoogleDrive:PickerApiKey and GoogleDrive:PickerAppId for Make Studio.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Ok(new GoogleDrivePickerConfigResponse
        {
            ConnectorId = "google-drive",
            ClientId = clientId,
            DeveloperKey = developerKey,
            AppId = appId,
            Scope = DriveScope,
            MaxSelectableFiles = MaxPickerFiles,
            AcceptedExtensions = QuoteUploadConstraints.SupportedAttachmentExtensions
                .Select(extension => $".{extension}")
                .ToList()
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

    /// <summary>
    /// Imports Google Picker selections into QuoteEngine storage and returns composer-ready attachments.
    /// </summary>
    [HttpPost("quote/v1/connectors/google-drive/imports")]
    [ProducesResponseType(typeof(GoogleDriveImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<GoogleDriveImportResponse>> Import(
        [FromBody] GoogleDriveImportRequest request,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!sessionResolver.TryResolveCustomerId(out var customerId))
        {
            return Unauthorized();
        }

        if (request.Files.Count == 0)
        {
            ModelState.AddModelError(nameof(request.Files), "Select at least one Google Drive file.");
            return ValidationProblem(ModelState);
        }

        if (request.Files.Count > MaxPickerFiles)
        {
            ModelState.AddModelError(nameof(request.Files), $"Select at most {MaxPickerFiles} Google Drive files.");
            return ValidationProblem(ModelState);
        }

        var connection = connectorStore.Get(customerId);
        if (connection is null)
        {
            return DriveConnectionConflict("Google Drive is not connected.", "Connect Google Drive before importing Drive files.");
        }

        var accessToken = await GetUsableAccessTokenAsync(connection, cancellationToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            connectorStore.Remove(customerId);
            return DriveConnectionConflict(
                "Google Drive needs to be reconnected.",
                "The previous Google Drive authorization expired. Please connect Google Drive again.");
        }

        var attachments = new List<QuoteAgentAttachmentDto>(request.Files.Count);
        try
        {
            foreach (var selectedFile in request.Files)
            {
                var metadata = await GetDriveFileMetadataAsync(accessToken, selectedFile, cancellationToken);
                if (metadata.Capabilities?.CanDownload == false)
                {
                    ModelState.AddModelError(nameof(request.Files), $"{metadata.DisplayName(selectedFile)} cannot be downloaded from Google Drive.");
                    return ValidationProblem(ModelState);
                }

                var imported = await DownloadDriveFileAsync(accessToken, metadata, selectedFile, cancellationToken);
                if (!QuoteUploadConstraints.IsSupportedAttachmentFileName(imported.FileName))
                {
                    ModelState.AddModelError(nameof(request.Files), $"{imported.FileName} is not a supported quote attachment.");
                    return ValidationProblem(ModelState);
                }

                if (imported.Bytes.Length == 0 ||
                    imported.Bytes.LongLength > QuoteUploadConstraints.MaxFileSizeBytes)
                {
                    ModelState.AddModelError(
                        nameof(request.Files),
                        $"{imported.FileName} must be between 1 byte and {QuoteUploadConstraints.MaxFileSizeMegabytes} MB.");
                    return ValidationProblem(ModelState);
                }

                var upload = await ImportDriveBytesAsync(request.QuoteSessionId, customerId, imported, metadata, cancellationToken);
                attachments.Add(new QuoteAgentAttachmentDto
                {
                    AttachmentId = upload.FileId,
                    FileName = upload.FileName,
                    ContentType = upload.ContentType,
                    FileSizeBytes = upload.ExpectedSizeBytes,
                    Kind = InferAgentAttachmentKind(upload.FileName, upload.ContentType),
                    UploadId = upload.UploadId,
                    StoragePath = upload.StoragePath,
                    SatisfiesGeometryGate = QuoteUploadConstraints.IsSupportedCadFileName(upload.FileName)
                });
            }
        }
        catch (GoogleDriveAuthorizationException)
        {
            connectorStore.Remove(customerId);
            return DriveConnectionConflict(
                "Google Drive needs to be reconnected.",
                "Google rejected the stored Drive authorization. Please connect Google Drive again.");
        }
        catch (InvalidOperationException ex)
        {
            ModelState.AddModelError(nameof(request.Files), ex.Message);
            return ValidationProblem(ModelState);
        }

        return Ok(new GoogleDriveImportResponse
        {
            QuoteSessionId = request.QuoteSessionId,
            SessionId = request.SessionId,
            Attachments = attachments
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

    private async Task<GoogleDriveFileMetadataResponse> GetDriveFileMetadataAsync(
        string accessToken,
        GoogleDriveSelectedFileDto selectedFile,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            QueryHelpers.AddQueryString(
                $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(selectedFile.Id)}",
                new Dictionary<string, string?>
                {
                    ["fields"] = "id,name,mimeType,size,capabilities/canDownload,webViewLink"
                }));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new GoogleDriveAuthorizationException();
        }

        response.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<GoogleDriveFileMetadataResponse>(cancellationToken)
            ?? new GoogleDriveFileMetadataResponse();
        metadata.Id = string.IsNullOrWhiteSpace(metadata.Id) ? selectedFile.Id : metadata.Id;
        metadata.Name = string.IsNullOrWhiteSpace(metadata.Name) ? selectedFile.Name : metadata.Name;
        metadata.MimeType = string.IsNullOrWhiteSpace(metadata.MimeType) ? selectedFile.MimeType : metadata.MimeType;
        metadata.WebViewLink = string.IsNullOrWhiteSpace(metadata.WebViewLink) ? selectedFile.WebViewLink : metadata.WebViewLink;
        return metadata;
    }

    private async Task<DriveImportedContent> DownloadDriveFileAsync(
        string accessToken,
        GoogleDriveFileMetadataResponse metadata,
        GoogleDriveSelectedFileDto selectedFile,
        CancellationToken cancellationToken)
    {
        var isGoogleWorkspaceFile = metadata.MimeType?.StartsWith("application/vnd.google-apps.", StringComparison.OrdinalIgnoreCase) == true;
        var fileName = metadata.DisplayName(selectedFile);
        var contentType = string.IsNullOrWhiteSpace(metadata.MimeType) ? selectedFile.MimeType : metadata.MimeType;
        string url;
        if (isGoogleWorkspaceFile)
        {
            contentType = "application/pdf";
            fileName = EnsureExtension(fileName, ".pdf");
            url = QueryHelpers.AddQueryString(
                $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(metadata.Id)}/export",
                new Dictionary<string, string?> { ["mimeType"] = contentType });
        }
        else
        {
            contentType = string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType;
            url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(metadata.Id)}?alt=media";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await httpClientFactory.CreateClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            throw new GoogleDriveAuthorizationException();
        }

        response.EnsureSuccessStatusCode();
        var bytes = await ReadContentWithinLimitAsync(response.Content, QuoteUploadConstraints.MaxFileSizeBytes, cancellationToken);
        return new DriveImportedContent(fileName, contentType!, bytes);
    }

    private async Task<UploadState> ImportDriveBytesAsync(
        string quoteSessionId,
        Guid customerId,
        DriveImportedContent imported,
        GoogleDriveFileMetadataResponse metadata,
        CancellationToken cancellationToken)
    {
        var upload = store.InitiateUpload(new InitiateQuoteUploadRequest
        {
            QuoteSessionId = quoteSessionId,
            FileName = imported.FileName,
            ContentType = imported.ContentType,
            FileSizeBytes = imported.Bytes.LongLength
        }, customerId);

        var downstreamUploadId = await uploadClient.InitiateResumableUploadAsync(
            upload.FileName,
            upload.ContentType,
            upload.ExpectedSizeBytes,
            upload.StoragePath,
            BuildDriveImportMetadata(metadata),
            cancellationToken);
        upload = store.AttachDownstreamUpload(upload.UploadId, downstreamUploadId);

        await using var stream = new MemoryStream(imported.Bytes, writable: false);
        await uploadClient.StreamUploadAsync(
            stream,
            upload.ContentType,
            upload.ExpectedSizeBytes,
            $"bytes 0-{upload.ExpectedSizeBytes - 1}/{upload.ExpectedSizeBytes}",
            downstreamUploadId,
            upload.StoragePath,
            cancellationToken);

        return store.MarkProcessing(upload.UploadId);
    }

    private ActionResult DriveConnectionConflict(string title, string detail)
    {
        return Conflict(new ProblemDetails
        {
            Title = title,
            Detail = detail,
            Status = StatusCodes.Status409Conflict
        });
    }

    private static async Task<byte[]> ReadContentWithinLimitAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength.Value > maxBytes)
        {
            throw new InvalidOperationException("Google Drive file exceeds the QuoteEngine attachment size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            if (memory.Length + read > maxBytes)
            {
                throw new InvalidOperationException("Google Drive file exceeds the QuoteEngine attachment size limit.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    private static IReadOnlyDictionary<string, string> BuildDriveImportMetadata(GoogleDriveFileMetadataResponse metadata)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["source"] = "google-drive",
            ["googleDrive.fileId"] = metadata.Id
        };
        if (!string.IsNullOrWhiteSpace(metadata.WebViewLink))
        {
            values["googleDrive.webViewLink"] = metadata.WebViewLink!;
        }

        return values;
    }

    private static string InferAgentAttachmentKind(string fileName, string contentType)
    {
        if (QuoteUploadConstraints.IsSupportedCadFileName(fileName))
        {
            return "cad";
        }

        var extension = Path.GetExtension(fileName).TrimStart('.');
        if (QuoteUploadConstraints.SupplementalDocumentExtensions.Any(item =>
                item.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return "drawing";
        }

        if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
            QuoteUploadConstraints.SupplementalImageExtensions.Any(item =>
                item.Equals(extension, StringComparison.OrdinalIgnoreCase)))
        {
            return fileName.Contains("sketch", StringComparison.OrdinalIgnoreCase) ? "sketch" : "photo";
        }

        return "supplemental";
    }

    private static string EnsureExtension(string fileName, string extension)
    {
        return Path.HasExtension(fileName) ? fileName : $"{fileName}{extension}";
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

    private sealed class GoogleDriveAuthorizationException : Exception;

    private sealed record DriveImportedContent(string FileName, string ContentType, byte[] Bytes);

    private sealed class GoogleDriveFileMetadataResponse
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("mimeType")]
        public string? MimeType { get; set; }

        [JsonPropertyName("size")]
        public string? Size { get; set; }

        [JsonPropertyName("capabilities")]
        public GoogleDriveCapabilitiesResponse? Capabilities { get; set; }

        [JsonPropertyName("webViewLink")]
        public string? WebViewLink { get; set; }

        public string DisplayName(GoogleDriveSelectedFileDto selectedFile)
        {
            return !string.IsNullOrWhiteSpace(Name)
                ? Name!
                : !string.IsNullOrWhiteSpace(selectedFile.Name)
                    ? selectedFile.Name!
                    : "drive-file";
        }
    }

    private sealed class GoogleDriveCapabilitiesResponse
    {
        [JsonPropertyName("canDownload")]
        public bool CanDownload { get; set; } = true;
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
