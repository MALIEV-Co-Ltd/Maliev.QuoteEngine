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
        if (string.IsNullOrWhiteSpace(storagePath) || string.IsNullOrWhiteSpace(payload.FileId))
        {
            logger.LogWarning("FileAnalyzedEvent received without a canonical file identity; skipping");
            return;
        }

        if (!await status.CanApplyGeometryEventAsync(
                storagePath,
                payload.FileId,
                context.Message.MessageId,
                context.Message.OccurredAtUtc,
                payload.ProcessedAt,
                QuoteGeometryEventPhase.Completion,
                context.CancellationToken))
        {
            return;
        }

        string glbUrl = "";
        string? thumbnailUrl = null;
        bool failed = false;
        string? errorCode = null;
        var viewerStoragePath = string.IsNullOrWhiteSpace(payload.ViewerStoragePath)
            ? payload.GlbStoragePath
            : payload.ViewerStoragePath;
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
                glbUrl = await uploadClient.GetDownloadUrlByPathAsync(viewerStoragePath, ct: context.CancellationToken);

            if (!string.IsNullOrEmpty(payload.ThumbnailStoragePath))
                thumbnailUrl = await uploadClient.GetDownloadUrlByPathAsync(payload.ThumbnailStoragePath, ct: context.CancellationToken);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get signed URLs for {StoragePath}", storagePath);
            failed = true;
            errorCode = "GlbSigningFailed";
            await status.SetFailedAsync(
                storagePath,
                payload.FileId,
                errorCode,
                context.Message.MessageId,
                context.Message.OccurredAtUtc,
                context.CancellationToken);
        }

        if (!failed)
        {
            await status.SetGlbReadyAsync(
                storagePath,
                glbUrl,
                thumbnailUrl,
                payload.BodyCount ?? 1,
                payload.Metrics?.IsManifold ?? true,
                context.CancellationToken,
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
                payload.ProcessedAt);
        }

        var signalRPayload = new QeGlbReadyPayload(
            StoragePath: storagePath,
            GlbUrl: glbUrl,
            ThumbnailUrl: thumbnailUrl,
            BodyCount: payload.BodyCount ?? 1,
            IsManifold: payload.Metrics?.IsManifold ?? true,
            Failed: failed,
            ErrorCode: errorCode,
            ViewerStoragePath: viewerStoragePath,
            ViewerFileExtension: viewerFileExtension);

        await hub.Clients
            .Group(QuoteNotificationsHub.FileGroup(storagePath))
            .SendAsync("GlbReady", signalRPayload, context.CancellationToken);
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
