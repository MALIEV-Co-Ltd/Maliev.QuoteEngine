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
        if (string.IsNullOrEmpty(storagePath))
        {
            logger.LogWarning("FileAnalyzedEvent received with empty StoragePath — skipping");
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
        var volumeCc = ToMetric(payload.Metrics?.VolumeCm3, requirePositive: true);
        var surfaceAreaCm2 = ToMetric(payload.Metrics?.SurfaceAreaCm2, requirePositive: false);

        try
        {
            if (!string.IsNullOrEmpty(viewerStoragePath))
                glbUrl = await uploadClient.GetDownloadUrlByPathAsync(viewerStoragePath, ct: context.CancellationToken);

            if (!string.IsNullOrEmpty(payload.ThumbnailStoragePath))
                thumbnailUrl = await uploadClient.GetDownloadUrlByPathAsync(payload.ThumbnailStoragePath, ct: context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get signed URLs for {StoragePath}", storagePath);
            failed = true;
            errorCode = "GlbSigningFailed";
            await status.SetFailedAsync(storagePath, errorCode, context.CancellationToken);
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
                surfaceAreaCm2);
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

    private static decimal? ToMetric(double? value, bool requirePositive)
    {
        if (value is not { } number ||
            double.IsNaN(number) ||
            double.IsInfinity(number) ||
            (requirePositive ? number <= 0 : number < 0))
        {
            return null;
        }

        try
        {
            return (decimal)number;
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
