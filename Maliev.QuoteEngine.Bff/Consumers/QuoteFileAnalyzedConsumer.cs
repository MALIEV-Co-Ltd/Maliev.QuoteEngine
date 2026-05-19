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

        try
        {
            if (!string.IsNullOrEmpty(payload.GlbStoragePath))
                glbUrl = await uploadClient.GetDownloadUrlByPathAsync(payload.GlbStoragePath, ct: context.CancellationToken);

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
                context.CancellationToken);
        }

        var signalRPayload = new QeGlbReadyPayload(
            StoragePath: storagePath,
            GlbUrl: glbUrl,
            ThumbnailUrl: thumbnailUrl,
            BodyCount: payload.BodyCount ?? 1,
            IsManifold: payload.Metrics?.IsManifold ?? true,
            Failed: failed,
            ErrorCode: errorCode);

        await hub.Clients
            .Group(QuoteNotificationsHub.FileGroup(storagePath))
            .SendAsync("GlbReady", signalRPayload, context.CancellationToken);
    }
}
