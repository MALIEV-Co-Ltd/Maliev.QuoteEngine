// Maliev.QuoteEngine.Bff/Consumers/QuoteDfmAnalysisReadyConsumer.cs
using System.Text.Json;
using MassTransit;
using Maliev.MessagingContracts.Contracts.Geometry;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Hubs;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using Microsoft.AspNetCore.SignalR;

namespace Maliev.QuoteEngine.Bff.Consumers;

public sealed class QuoteDfmAnalysisReadyConsumer(
    IQuoteFileAnalysisStatusService status,
    QuoteUploadServiceClient uploadClient,
    IHubContext<QuoteNotificationsHub> hub,
    BffMetrics bffMetrics,
    ILogger<QuoteDfmAnalysisReadyConsumer> logger) : IConsumer<DfmAnalysisReadyEvent>
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    public async Task Consume(ConsumeContext<DfmAnalysisReadyEvent> context)
    {
        var payload = context.Message.Payload;
        if (payload is null)
        {
            logger.LogWarning("DfmAnalysisReadyEvent received with null payload — skipping");
            return;
        }

        var storagePath = payload.StoragePath;
        if (string.IsNullOrWhiteSpace(storagePath) || string.IsNullOrWhiteSpace(payload.FileId))
        {
            logger.LogWarning("DfmAnalysisReadyEvent received without a canonical file identity; skipping");
            return;
        }

        // Map MessagingContracts object fields → typed QE report records
        var fdmReport = MapFdm(payload.FdmReport);
        var slaReport = MapSla(payload.SlaReport);
        var cncReport = MapCnc(payload.CncReport);
        if (fdmReport is null && slaReport is null && cncReport is null)
        {
            logger.LogWarning("DfmAnalysisReadyEvent contained no valid reports for {StoragePath}; skipping", storagePath);
            return;
        }
        var claim = await status.ClaimDfmEventAsync(
            storagePath,
            payload.FileId,
            context.Message.MessageId,
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
            var rawOverlayPaths = ExtractStringList(payload.OverlayPaths);
            IReadOnlyList<string> overlayUrls = [];
            if (rawOverlayPaths.Count > 0)
            {
                try
                {
                    overlayUrls = await Task.WhenAll(
                        rawOverlayPaths.Select(p =>
                            uploadClient.GetDownloadUrlByPathAsync(p, ct: context.CancellationToken)));
                }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to sign overlay URLs for {StoragePath}", storagePath);
                    throw new QuoteAnalysisPreviewSigningException(storagePath, ex);
                }
            }

            await EnsureClaimAsync(status, claim, context.CancellationToken);
            var finalized = await status.FinalizeDfmAnalysisAsync(
                claim,
                new QuoteDfmAnalysisUpdate(
                    fdmReport,
                    slaReport,
                    cncReport,
                    overlayUrls,
                    payload.NonManifoldReason,
                    null,
                    payload.FileId,
                    context.Message.MessageId,
                    context.Message.OccurredAtUtc,
                    payload.AnalyzedAt,
                    payload.BodyCount),
                context.CancellationToken);
            if (!finalized.Applied || finalized.Snapshot is null)
                return;

            RecordServerDfmReports(fdmReport, slaReport, cncReport, bffMetrics);
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
                logger.LogWarning(ex, "Failed to release the DFM analysis claim for {StoragePath}", storagePath);
            }
        }
    }

    private static Task SendPendingNotificationAsync(
        IQuoteFileAnalysisStatusService status,
        QuoteAnalysisEventClaim claim,
        IHubContext<QuoteNotificationsHub> hub,
        CancellationToken ct) =>
        claim.Snapshot is null || claim.Revision is null
            ? throw new InvalidOperationException("The pending DFM notification is incomplete.")
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
        var signalRPayload = new QeDfmAnalysisReadyPayload(
            StoragePath: claim.StoragePath,
            IsManifold: snapshot.IsManifold,
            NonManifoldReason: snapshot.NonManifoldReason,
            FdmReport: snapshot.FdmReport,
            SlaReport: snapshot.SlaReport,
            CncReport: snapshot.CncReport,
            OverlayGlbUrls: snapshot.OverlayGlbUrls,
            AnalysisErrorCode: snapshot.AnalysisErrorCode,
            EventId: claim.EventId,
            Revision: revision);
        try
        {
            await hub.Clients
                .Group(QuoteNotificationsHub.FileGroup(claim.StoragePath))
                .SendAsync("DfmAnalysisReady", signalRPayload, ct);
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

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static void RecordServerDfmReports(
        QeFdmDfmReport? fdmReport,
        QeSlaDfmReport? slaReport,
        QeCncDfmReport? cncReport,
        BffMetrics bffMetrics)
    {
        if (fdmReport is not null)
        {
            bffMetrics.RecordServerDfmAnalysisReady("FDM");
        }

        if (slaReport is not null)
        {
            bffMetrics.RecordServerDfmAnalysisReady("SLA");
        }

        if (cncReport is not null)
        {
            bffMetrics.RecordServerDfmAnalysisReady("CNC_MILL");
        }
    }

    private static QeFdmDfmReport? MapFdm(object? raw)
    {
        var src = Deserialize<FdmDfmReportPayload>(raw);
        if (src is null) return null;
        var overhangAreaCm2 = GeometryMetricMapper.NonNegative(src.OverhangAreaCm2);
        if (overhangAreaCm2 is null) return null;
        return new QeFdmDfmReport(
            Math.Max(0, src.ThinWallCount),
            Math.Max(0, src.OverhangFaceCount),
            overhangAreaCm2.Value,
            src.SupportRequired,
            Math.Max(0, src.SmallDetailCount),
            (src.Issues ?? []).Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    private static QeSlaDfmReport? MapSla(object? raw)
    {
        var src = Deserialize<SlaDfmReportPayload>(raw);
        if (src is null) return null;
        // HollowRegions in the contract is a list of centroid coordinates, not a bool flag;
        // treat as "has hollow regions" when the list is non-empty.
        return new QeSlaDfmReport(
            Math.Max(0, src.ThinWallCount),
            src.ResinTrappingRisk,
            src.SuctionRisk,
            (src.HollowRegions?.Count ?? 0) > 0,
            (src.Issues ?? []).Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    private static QeCncDfmReport? MapCnc(object? raw)
    {
        var src = Deserialize<CncDfmReportPayload>(raw);
        if (src is null) return null;
        return new QeCncDfmReport(
            Math.Max(0, src.SharpCornerCount),
            src.HasUndercuts,
            src.HasDrillHoles,
            Math.Max(0, src.DrillHoleCount),
            src.RequiresEdm,
            src.RequiresGrinding,
            src.IsTurnable,
            (src.Issues ?? []).Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    /// <summary>
    /// Handles the three states for payload fields typed as <c>object</c> in the contract:
    /// already the right CLR type, a JsonElement from MassTransit deserialization, or null.
    /// </summary>
    private static T? Deserialize<T>(object? raw) where T : class
    {
        try
        {
            return raw switch
            {
                null => null,
                T typed => typed,
                JsonElement el => el.Deserialize<T>(JsonOpts),
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts storage-path values from the object-typed OverlayPaths dictionary.
    /// </summary>
    private static IReadOnlyList<string> ExtractStringList(object? raw)
    {
        return raw switch
        {
            null => [],
            IReadOnlyDictionary<string, string> dictionary => NormalizePaths(dictionary.Values),
            IDictionary<string, string> dictionary => NormalizePaths(dictionary.Values),
            IEnumerable<KeyValuePair<string, string>> dictionary => NormalizePaths(dictionary.Select(pair => pair.Value)),
            IEnumerable<string> legacyList => NormalizePaths(legacyList),
            JsonElement el when el.ValueKind == JsonValueKind.Array =>
                NormalizePaths(el.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString())),
            JsonElement el when el.ValueKind == JsonValueKind.Object =>
                NormalizePaths(el.EnumerateObject()
                    .Where(property => property.Value.ValueKind == JsonValueKind.String)
                    .Select(property => property.Value.GetString())),
            _ => []
        };
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string?> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path!.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();
}
