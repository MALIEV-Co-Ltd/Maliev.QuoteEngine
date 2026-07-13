using Maliev.QuoteEngine.Bff.Security;
using Maliev.QuoteEngine.Shared.Agent;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Bff.Services;

/// <summary>
/// Replaces browser-supplied attachment metadata with the authoritative QuoteEngine upload record.
/// A storage path or upload id is a locator only and cannot manufacture session membership.
/// </summary>
internal sealed class QuoteAgentAttachmentAccess(
    IQuoteUploadStateStore uploadStore,
    QuoteAgentSessionAccess sessionAccess)
{
    public Task<List<QuoteAgentAttachmentDto>?> AuthorizeAndCanonicalizeAsync(
        Guid sessionId,
        IReadOnlyCollection<QuoteAgentAttachmentDto> requestedAttachments,
        CancellationToken cancellationToken)
    {
        return AuthorizeAndCanonicalizeAsync(
            sessionId,
            requestedAttachments,
            sessionAccess.GetCurrentCaller(),
            cancellationToken);
    }

    public Task<List<QuoteAgentAttachmentDto>?> AuthorizeAndCanonicalizeForOwnerAsync(
        Guid sessionId,
        Guid? customerId,
        Guid? visitorId,
        IReadOnlyCollection<QuoteAgentAttachmentDto> requestedAttachments,
        CancellationToken cancellationToken)
    {
        var owner = new QuoteAgentCallerIdentity(
            customerId,
            visitorId,
            AnonymousVisitorCredentialStatus.Valid);
        return AuthorizeAndCanonicalizeAsync(
            sessionId,
            requestedAttachments,
            owner,
            cancellationToken);
    }

    private async Task<List<QuoteAgentAttachmentDto>?> AuthorizeAndCanonicalizeAsync(
        Guid sessionId,
        IReadOnlyCollection<QuoteAgentAttachmentDto> requestedAttachments,
        QuoteAgentCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var authorizedAttachments = new List<QuoteAgentAttachmentDto>(requestedAttachments.Count);
        if (requestedAttachments.Count == 0)
        {
            return authorizedAttachments;
        }

        foreach (var requested in requestedAttachments)
        {
            var authorized = await AuthorizeAndCanonicalizeAsync(
                requested,
                sessionId,
                caller,
                cancellationToken);
            if (authorized is null)
            {
                return null;
            }

            authorizedAttachments.Add(authorized);
        }

        return authorizedAttachments;
    }

    private async Task<QuoteAgentAttachmentDto?> AuthorizeAndCanonicalizeAsync(
        QuoteAgentAttachmentDto requested,
        Guid sessionId,
        QuoteAgentCallerIdentity caller,
        CancellationToken cancellationToken)
    {
        var storagePath = NormalizeStoragePath(requested.StoragePath);
        if (storagePath is null)
        {
            return TryAuthorizeInlineData(requested, out var inline) ? inline : null;
        }

        if (string.IsNullOrWhiteSpace(requested.UploadId))
        {
            return null;
        }

        var upload = await uploadStore.GetAsync(requested.UploadId.Trim(), cancellationToken);
        if (upload is null ||
            !StoragePathMatches(upload.StoragePath, storagePath) ||
            !BelongsToSession(upload, sessionId) ||
            !HasReceivedAuthoritativeBytes(upload) ||
            !OwnerMatches(upload, caller))
        {
            return null;
        }

        var isCad = QuoteUploadConstraints.IsSupportedCadFileName(upload.FileName);
        return new QuoteAgentAttachmentDto
        {
            AttachmentId = upload.FileId,
            FileName = upload.FileName,
            ContentType = upload.ContentType,
            FileSizeBytes = upload.ExpectedSizeBytes,
            Kind = isCad ? "cad" : requested.Kind,
            UploadId = upload.UploadId,
            StoragePath = upload.StoragePath,
            Url = null,
            SatisfiesGeometryGate = isCad
        };
    }

    private static bool TryAuthorizeInlineData(
        QuoteAgentAttachmentDto requested,
        out QuoteAgentAttachmentDto authorized)
    {
        authorized = default!;
        var url = requested.Url?.Trim();
        if (!string.IsNullOrWhiteSpace(requested.UploadId) ||
            string.IsNullOrWhiteSpace(url) ||
            !url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) ||
            url.Length > 10_000)
        {
            return false;
        }

        authorized = new QuoteAgentAttachmentDto
        {
            AttachmentId = requested.AttachmentId,
            FileName = requested.FileName,
            ContentType = requested.ContentType,
            FileSizeBytes = requested.FileSizeBytes,
            Kind = "sketch",
            Url = url,
            SatisfiesGeometryGate = false
        };
        return true;
    }

    private static bool OwnerMatches(UploadState upload, QuoteAgentCallerIdentity caller)
    {
        if (upload.CustomerId.HasValue)
        {
            return caller.CustomerId == upload.CustomerId;
        }

        return caller.VisitorCredentialStatus != AnonymousVisitorCredentialStatus.Invalid &&
               upload.VisitorId.HasValue &&
               caller.VisitorId == upload.VisitorId;
    }

    private static bool BelongsToSession(UploadState upload, Guid sessionId)
    {
        return sessionId != Guid.Empty &&
               Guid.TryParse(upload.QuoteSessionId, out var uploadSessionId) &&
               uploadSessionId == sessionId;
    }

    private static bool HasReceivedAuthoritativeBytes(UploadState upload)
    {
        return upload.ExpectedSizeBytes > 0 &&
               upload.ReceivedBytes == upload.ExpectedSizeBytes &&
               (upload.Status.Equals("Uploaded", StringComparison.OrdinalIgnoreCase) ||
                upload.Status.Equals("Processing", StringComparison.OrdinalIgnoreCase) ||
                upload.Status.Equals("Analyzed", StringComparison.OrdinalIgnoreCase));
    }

    private static bool StoragePathMatches(string candidate, string expected)
    {
        return string.Equals(
            NormalizeStoragePath(candidate),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeStoragePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim().Replace('\\', '/');
        if (normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Equals("..", StringComparison.Ordinal) ||
            normalized.StartsWith("../", StringComparison.Ordinal) ||
            normalized.Contains("../", StringComparison.Ordinal) ||
            normalized.Contains("/..", StringComparison.Ordinal) ||
            normalized.Contains('%', StringComparison.Ordinal) ||
            Uri.TryCreate(normalized, UriKind.Absolute, out _))
        {
            return null;
        }

        return normalized;
    }
}
