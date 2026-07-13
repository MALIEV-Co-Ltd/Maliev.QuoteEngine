// Maliev.QuoteEngine.Bff/Consumers/QuoteFileAnalyzedConsumer.cs
using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

public sealed class QuoteFileAnalyzedConsumer(
    IQuoteFileAnalysisStatusService status,
    QuoteUploadServiceClient uploadClient,
    IHubContext<QuoteNotificationsHub> hub,
    ILogger<QuoteFileAnalyzedConsumer> logger) : IConsumer<FileAnalyzedEvent>
{
    public async Task Consume(ConsumeContext<FileAnalyzedEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("FileAnalyzedEvent received with null payload — skipping");
            return;
        }

        var storagePath = payload.StoragePath;
        if (!QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(storagePath) ||
            string.IsNullOrWhiteSpace(payload.FileId))
        {
            logger.LogWarning("FileAnalyzedEvent received without a canonical file identity; skipping");
            return;
        }

        var claim = await status.ClaimGeometryCompletionAsync(
            storagePath,
            payload.FileId,
            context.Message.MessageId,
            context.Message.OccurredAtUtc,
            payload.ProcessedAt,
            context.CancellationToken);
        if (claim.Disposition == QuoteAnalysisClaimDisposition.DuplicateOrStale)
            return;

        try
        {
            if (claim.Disposition == QuoteAnalysisClaimDisposition.PendingNotification)
            {
                await SendPendingNotificationAsync(status, claim, hub, context.CancellationToken);
                return;
            }

            await EnsureClaimAsync(status, claim, context.CancellationToken);
            string glbUrl = "";
            string? thumbnailUrl = null;
            var thumbnailStoragePath = string.IsNullOrEmpty(payload.ThumbnailStoragePath)
                ? null
                : payload.ThumbnailStoragePath;
            var viewerStoragePath = string.IsNullOrEmpty(payload.ViewerStoragePath)
                ? payload.GlbStoragePath
                : payload.ViewerStoragePath;
            if (!QuoteAnalysisArtifactPathValidator.AreCanonicalGeometryPaths(
                    storagePath,
                    viewerStoragePath,
                    thumbnailStoragePath))
            {
                logger.LogWarning(
                    "FileAnalyzedEvent contained preview paths outside {StoragePath}; skipping",
                    storagePath);
                return;
            }
            var viewerFileExtension = NormalizeViewerFileExtension(
                payload.ViewerFileExtension,
                viewerStoragePath);
            var metrics = payload.Metrics;
            var volumeCc = GeometryMetricMapper.Positive(metrics?.VolumeCm3);
            var supportVolumeCc = GeometryMetricMapper.NonNegative(metrics?.SupportVolumeCm3);
            var surfaceAreaCm2 = GeometryMetricMapper.NonNegative(metrics?.SurfaceAreaCm2);
            var boundingBoxXmm = GeometryMetricMapper.Positive(metrics?.BoundingBox?.X);
            var boundingBoxYmm = GeometryMetricMapper.Positive(metrics?.BoundingBox?.Y);
            var boundingBoxZmm = GeometryMetricMapper.Positive(metrics?.BoundingBox?.Z);
            var triangleCount = GeometryMetricMapper.Positive(metrics?.TriangleCount);
            var hasCompleteMetrics = volumeCc.HasValue &&
                boundingBoxXmm.HasValue && boundingBoxYmm.HasValue && boundingBoxZmm.HasValue &&
                triangleCount.HasValue;
            if (!hasCompleteMetrics)
            {
                supportVolumeCc = null;
                surfaceAreaCm2 = null;
                boundingBoxXmm = null;
                boundingBoxYmm = null;
                boundingBoxZmm = null;
                triangleCount = null;
            }

            try
            {
                if (!string.IsNullOrEmpty(viewerStoragePath))
                {
                    glbUrl = await uploadClient.GetDownloadUrlByPathAsync(
                        viewerStoragePath,
                        ct: context.CancellationToken);
                    if (!QuoteAnalysisArtifactPathValidator.IsHttpsCapability(glbUrl))
                        throw new InvalidOperationException("UploadService returned an invalid viewer URL.");
                    await EnsureClaimAsync(status, claim, context.CancellationToken);
                }

                if (thumbnailStoragePath is not null)
                {
                    thumbnailUrl = await uploadClient.GetDownloadUrlByPathAsync(
                        thumbnailStoragePath,
                        ct: context.CancellationToken);
                    if (!QuoteAnalysisArtifactPathValidator.IsHttpsCapability(thumbnailUrl))
                        throw new InvalidOperationException("UploadService returned an invalid thumbnail URL.");
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to get signed URLs for {StoragePath}", storagePath);
                throw new QuoteAnalysisPreviewSigningException(storagePath, ex);
            }
            await EnsureClaimAsync(status, claim, context.CancellationToken);
            var finalized = await status.FinalizeGeometryCompletionAsync(
                claim,
                new QuoteGeometryCompletionUpdate(
                    glbUrl,
                    thumbnailUrl,
                    payload.BodyCount ?? 1,
                    payload.Metrics?.IsManifold ?? true,
                    viewerStoragePath,
                    viewerFileExtension,
                    volumeCc,
                    surfaceAreaCm2,
                    payload.FileId,
                    supportVolumeCc,
                    boundingBoxXmm,
                    boundingBoxYmm,
                    boundingBoxZmm,
                    triangleCount,
                    metrics?.NonManifoldReason,
                    context.Message.MessageId,
                    context.Message.OccurredAtUtc,
                    payload.ProcessedAt,
                    thumbnailStoragePath),
                context.CancellationToken);
            if (!finalized.Applied || finalized.Snapshot is null)
                return;

            await SendNotificationAsync(
                status,
                claim,
                finalized.Revision,
                finalized.Snapshot,
                hub,
                context.CancellationToken);
        }
        finally
        {
            try
            {
                await status.ReleaseAnalysisClaimAsync(claim, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to release the analysis claim for {StoragePath}", storagePath);
            }
        }
    }

    private static Task SendPendingNotificationAsync(
        IQuoteFileAnalysisStatusService status,
        QuoteAnalysisEventClaim claim,
        IHubContext<QuoteNotificationsHub> hub,
        CancellationToken ct) =>
        claim.Snapshot is null || claim.Revision is null
            ? throw new InvalidOperationException("The pending geometry notification is incomplete.")
            : SendNotificationAsync(status, claim, claim.Revision.Value, claim.Snapshot, hub, ct);

    private static async Task SendNotificationAsync(
        IQuoteFileAnalysisStatusService status,
        QuoteAnalysisEventClaim claim,
        long revision,
        QuoteFileAnalysisStatus snapshot,
        IHubContext<QuoteNotificationsHub> hub,
        CancellationToken ct)
    {
        await EnsureClaimAsync(status, claim, ct);
        var signalRPayload = new QeGlbReadyPayload(
            StoragePath: claim.StoragePath,
            GlbUrl: snapshot.GlbUrl ?? "",
            ThumbnailUrl: snapshot.ThumbnailUrl,
            BodyCount: snapshot.BodyCount,
            IsManifold: snapshot.IsManifold,
            Failed: false,
            ErrorCode: null,
            ViewerStoragePath: snapshot.ViewerStoragePath,
            ViewerFileExtension: snapshot.ViewerFileExtension,
            EventId: claim.EventId,
            Revision: revision);
        try
        {
            await hub.Clients
                .Group(QuoteNotificationsHub.FileGroup(claim.StoragePath))
                .SendAsync("GlbReady", signalRPayload, ct);
            await status.MarkAnalysisNotificationDispatchedAsync(claim, revision, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new QuoteAnalysisNotificationDeliveryException(
                claim.StoragePath,
                claim.EventId,
                ex);
        }
    }

    private static async Task EnsureClaimAsync(
        IQuoteFileAnalysisStatusService status,
        QuoteAnalysisEventClaim claim,
        CancellationToken ct)
    {
        if (!await status.RenewAnalysisClaimAsync(claim, ct))
            throw new QuoteAnalysisClaimLostException(claim.StoragePath, claim.EventId);
    }

    private static string? NormalizeViewerFileExtension(string? fileExtension, string? storagePath)
    {
        var ext = !string.IsNullOrWhiteSpace(fileExtension)
            ? fileExtension.Trim()
            : Path.GetExtension(storagePath);

        if (string.IsNullOrWhiteSpace(ext))
            return null;

        return ext.StartsWith('.') ? ext.ToLowerInvariant() : "." + ext.ToLowerInvariant();
    }

}
