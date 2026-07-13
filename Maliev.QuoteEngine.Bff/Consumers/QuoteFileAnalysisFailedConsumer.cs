using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Bff.Consumers;

public sealed class QuoteFileAnalysisFailedConsumer(
    IQuoteFileAnalysisStatusService status,
    ILogger<QuoteFileAnalysisFailedConsumer> logger) : IConsumer<FileAnalysisFailedEvent>
{
    public async Task Consume(ConsumeContext<FileAnalysisFailedEvent> context)
    {
        var message = context.Message;
        var payload = message.Payload;
        if (payload is null || string.IsNullOrWhiteSpace(payload.StoragePath) || string.IsNullOrWhiteSpace(payload.FileId))
        {
            logger.LogWarning("FileAnalysisFailedEvent received without a canonical file identity; skipping");
            return;
        }

        await status.SetFailedAsync(
            payload.StoragePath,
            payload.FileId,
            SanitizeErrorCode(payload.ErrorCode),
            message.MessageId,
            message.OccurredAtUtc,
            context.CancellationToken);
    }

    private static string SanitizeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
            return "analysis_failed";

        var safe = new string(errorCode.Trim()
            .Take(64)
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .ToArray());
        return safe.Length == 0 ? "analysis_failed" : safe;
    }
}
