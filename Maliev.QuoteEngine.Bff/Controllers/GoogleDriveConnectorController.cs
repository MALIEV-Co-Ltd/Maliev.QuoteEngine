using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace Maliev.QuoteEngine.Bff.Controllers;

/// <summary>
/// Handles the trusted Google Drive connector browser flow for Make Studio.
/// </summary>
[ApiController]
public sealed class GoogleDriveConnectorController(
    CustomerSessionResolver sessionResolver,
    IQuoteAgentSessionRequestAuthorizer agentSessionAccess,
    IGoogleDriveConnectorStore connectorStore,
    IGoogleDriveOAuthStateStore oauthStateStore,
    QuoteEnginePrototypeStore store,
    QuoteUploadServiceClient uploadClient,
    IDataProtectionProvider dataProtectionProvider,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    IHostEnvironment environment) : ControllerBase
{
    private const string DriveScope = "https://www.googleapis.com/auth/drive.file";
    private static readonly TimeSpan FlowLifetime = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaxPickerFiles = 8;
    private readonly IDataProtector _browserFlowProtector = dataProtectionProvider.CreateProtector(
        "quote-engine.google-drive.browser-flow.v1");

    /// <summary>
    /// Starts Google Drive incremental authorization for the signed-in customer.
    /// </summary>
    [HttpGet("quote/v1/connectors/google-drive/start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Start(
        [FromQuery] string? returnUrl = null,
        CancellationToken cancellationToken = default)
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

        var principalId = CurrentPrincipalId();
        if (string.IsNullOrWhiteSpace(principalId))
        {
            return Unauthorized();
        }

        if (!TryResolveRedirectUri(out var redirectUri))
        {
            return Problem(
                title: "Google Drive connector redirect is not configured safely.",
                detail: "Configure the exact HTTPS Google Drive callback URI for Make Studio.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var stateNonce = RandomToken(32);
        var browserSecret = TryReadBrowserSecret(out var existingBrowserSecret)
            ? existingBrowserSecret
            : RandomToken(32);
        var codeVerifier = RandomToken(32);
        var expiresAt = timeProvider.GetUtcNow().Add(FlowLifetime);
        await oauthStateStore.SaveAsync(new GoogleDriveOAuthState(
            stateNonce,
            customerId,
            principalId,
            HashToken(browserSecret),
            codeVerifier,
            redirectUri,
            NormalizeLocalReturnUrl(returnUrl),
            expiresAt), cancellationToken);
        Response.Cookies.Append(
            GoogleDriveOAuthFlow.CookieName,
            _browserFlowProtector.Protect(JsonSerializer.Serialize(
                new GoogleDriveBrowserBinding(browserSecret),
                JsonOptions)),
            FlowCookieOptions(expiresAt));
        var query = new Dictionary<string, string?>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = DriveScope,
            ["access_type"] = "offline",
            ["include_granted_scopes"] = "true",
            ["prompt"] = "consent",
            ["state"] = stateNonce,
            ["code_challenge"] = WebEncoders.Base64UrlEncode(
                SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))),
            ["code_challenge_method"] = "S256"
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
        if (string.IsNullOrWhiteSpace(state) ||
            state.Length > 128 ||
            !TryReadBrowserSecret(out var browserSecret))
        {
            return InvalidCallback();
        }

        var currentPrincipalId = CurrentPrincipalId();
        if (!sessionResolver.TryResolveCustomerId(out var currentCustomerId) ||
            string.IsNullOrWhiteSpace(currentPrincipalId))
        {
            return InvalidCallback();
        }

        var browserSecretHash = HashToken(browserSecret);
        var connectorState = await oauthStateStore.ConsumeAsync(
            state,
            currentCustomerId,
            currentPrincipalId,
            browserSecretHash,
            cancellationToken);
        if (connectorState is null)
        {
            return InvalidCallback();
        }

        if (!TryResolveRedirectUri(out var currentRedirectUri) ||
            connectorState.ExpiresAt <= timeProvider.GetUtcNow() ||
            currentCustomerId != connectorState.CustomerId ||
            !FixedTimeEquals(currentPrincipalId, connectorState.PrincipalId) ||
            !FixedTimeEquals(browserSecretHash, connectorState.BrowserSecretHash) ||
            !string.Equals(connectorState.RedirectUri, currentRedirectUri, StringComparison.Ordinal))
        {
            return InvalidCallback();
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect(AppendConnectorStatus(connectorState.ReturnUrl, "cancelled"));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return InvalidCallback();
        }

        var tokenResponse = await ExchangeCodeAsync(
            code,
            connectorState.RedirectUri,
            connectorState.CodeVerifier,
            cancellationToken);
        connectorStore.Save(new GoogleDriveConnection(
            currentCustomerId,
            tokenResponse.AccessToken,
            tokenResponse.RefreshToken,
            timeProvider.GetUtcNow().AddSeconds(Math.Max(60, tokenResponse.ExpiresIn)),
            null,
            timeProvider.GetUtcNow()));

        return Redirect(AppendConnectorStatus(connectorState.ReturnUrl, "connected"));
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

        if (!Guid.TryParse(request.QuoteSessionId, out var quoteSessionId) ||
            quoteSessionId == Guid.Empty ||
            request.SessionId != quoteSessionId)
        {
            return EmptyNotFound();
        }

        var access = await agentSessionAccess.AuthorizeAsync(
            request.SessionId,
            allowCreate: false,
            cancellationToken);
        if (access == QuoteAgentSessionAccessDecision.Unauthorized)
        {
            return Unauthorized();
        }

        if (access != QuoteAgentSessionAccessDecision.Authorized)
        {
            return EmptyNotFound();
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

    private async Task<GoogleDriveTokenResponse> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string codeVerifier,
        CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string?>
        {
            ["client_id"] = GoogleClientId(),
            ["client_secret"] = GoogleClientSecret(),
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = codeVerifier
        });
        using var response = await httpClientFactory.CreateClient().PostAsync(
            "https://oauth2.googleapis.com/token",
            form,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<GoogleDriveTokenResponse>(cancellationToken);
        if (token is null || string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new InvalidOperationException("Google did not return a usable OAuth token response.");
        }

        return token;
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

        if (imported.Bytes.LongLength != upload.ExpectedSizeBytes ||
            store.TryAdvanceUpload(
                upload.UploadId,
                expectedReceivedBytes: 0,
                receivedBytes: imported.Bytes.LongLength) is null)
        {
            throw new InvalidOperationException("The imported Google Drive upload state could not be finalized.");
        }

        return store.MarkProcessing(upload.UploadId);
    }

    private void DisableStatusCodeBody()
    {
        var statusCodePages = HttpContext.Features.Get<IStatusCodePagesFeature>();
        if (statusCodePages is not null)
        {
            statusCodePages.Enabled = false;
        }
    }

    private EmptyResult EmptyNotFound()
    {
        DisableStatusCodeBody();
        Response.StatusCode = StatusCodes.Status404NotFound;
        return new EmptyResult();
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

    private bool TryReadBrowserSecret(out string browserSecret)
    {
        browserSecret = string.Empty;
        if (!Request.Cookies.TryGetValue(GoogleDriveOAuthFlow.CookieName, out var protectedFlow) ||
            string.IsNullOrWhiteSpace(protectedFlow))
        {
            return false;
        }

        try
        {
            var binding = JsonSerializer.Deserialize<GoogleDriveBrowserBinding>(
                _browserFlowProtector.Unprotect(protectedFlow),
                JsonOptions);
            if (string.IsNullOrWhiteSpace(binding.BrowserSecret))
            {
                return false;
            }

            browserSecret = binding.BrowserSecret;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException)
        {
            return false;
        }
    }

    private string? CurrentPrincipalId()
    {
        return User.FindFirstValue("principal_id") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
    }

    private IActionResult InvalidCallback()
    {
        return Problem(
            title: "Invalid Google Drive connector callback.",
            detail: "The connector authorization response could not be verified. Please start the connection again.",
            statusCode: StatusCodes.Status400BadRequest);
    }

    private CookieOptions FlowCookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = GoogleDriveOAuthFlow.CookiePath,
        Expires = expiresAt
    };

    private static string AppendConnectorStatus(string returnUrl, string status)
    {
        return QueryHelpers.AddQueryString(returnUrl, new Dictionary<string, string?>
        {
            ["connector"] = "google-drive",
            ["status"] = status
        });
    }

    private static string RandomToken(int byteLength)
    {
        return WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));
    }

    private static string HashToken(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(left)),
            SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    }

    private string? GoogleClientId()
    {
        return GoogleDriveOAuthConfiguration.ClientId(configuration);
    }

    private string? GoogleClientSecret()
    {
        return GoogleDriveOAuthConfiguration.ClientSecret(configuration);
    }

    private bool TryResolveRedirectUri(out string redirectUri)
    {
        var configured = configuration["GoogleDrive:RedirectUri"]?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                redirectUri = string.Empty;
                return false;
            }

            configured = $"{Request.Scheme}://{Request.Host}/auth/google/drive/callback";
        }

        var isLocalEnvironment = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.AbsolutePath, "/auth/google/drive/callback", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Scheme != Uri.UriSchemeHttps &&
             !(isLocalEnvironment && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
        {
            redirectUri = string.Empty;
            return false;
        }

        var isLocalLoopback = isLocalEnvironment && uri.IsLoopback;
        if (!isLocalLoopback)
        {
            var expectedOrigin = configuration["QuoteEngine:BaseUrl"]?.Trim();
            if (!Uri.TryCreate(expectedOrigin, UriKind.Absolute, out var expectedUri) ||
                !string.Equals(uri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) ||
                uri.Port != expectedUri.Port)
            {
                redirectUri = string.Empty;
                return false;
            }
        }

        redirectUri = uri.AbsoluteUri;
        return true;
    }

    private static string NormalizeLocalReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl) ||
            returnUrl.Length > 2048 ||
            !returnUrl.StartsWith('/') ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) ||
            returnUrl.Contains('\\') ||
            returnUrl.Contains('#') ||
            returnUrl.Any(char.IsControl))
        {
            return "/quotes";
        }

        return returnUrl;
    }

    private readonly record struct GoogleDriveBrowserBinding(string BrowserSecret);

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
