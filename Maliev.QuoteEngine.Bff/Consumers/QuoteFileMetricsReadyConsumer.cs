using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Bff.Consumers;

public sealed class QuoteFileMetricsReadyConsumer(
    IQuoteFileAnalysisStatusService status,
    ILogger<QuoteFileMetricsReadyConsumer> logger) : IConsumer<FileMetricsReadyEvent>
{
    public async Task Consume(ConsumeContext<FileMetricsReadyEvent> context)
    {
        var message = context.Message;
        var payload = message.Payload;
        if (payload is null || string.IsNullOrWhiteSpace(payload.StoragePath) || string.IsNullOrWhiteSpace(payload.FileId))
        {
            logger.LogWarning("FileMetricsReadyEvent received without a canonical file identity; skipping");
            return;
        }

        var metrics = payload.Metrics;
        if (metrics is null)
        {
            logger.LogWarning("FileMetricsReadyEvent received without metrics for {StoragePath}", payload.StoragePath);
            return;
        }

        await status.SetGeometryMetricsAsync(
            payload.StoragePath,
            payload.FileId,
            GeometryMetricMapper.Positive(metrics.VolumeCm3),
            GeometryMetricMapper.NonNegative(metrics.SupportVolumeCm3),
            GeometryMetricMapper.NonNegative(metrics.SurfaceAreaCm2),
            GeometryMetricMapper.Positive(metrics.BoundingBox?.X),
            GeometryMetricMapper.Positive(metrics.BoundingBox?.Y),
            GeometryMetricMapper.Positive(metrics.BoundingBox?.Z),
            GeometryMetricMapper.Positive(metrics.TriangleCount),
            GeometryMetricMapper.PositiveOrDefault(payload.BodyCount),
            metrics.IsManifold,
            metrics.IsManifold ? null : metrics.NonManifoldReason,
            message.MessageId,
            message.OccurredAtUtc,
            payload.ProcessedAt,
            context.CancellationToken);
    }
}
