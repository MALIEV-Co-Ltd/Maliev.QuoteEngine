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
        if (string.IsNullOrEmpty(storagePath))
        {
            logger.LogWarning("DfmAnalysisReadyEvent received with empty StoragePath — skipping");
            return;
        }

        // Map MessagingContracts object fields → typed QE report records
        var fdmReport = MapFdm(payload.FdmReport);
        var slaReport = MapSla(payload.SlaReport);
        var cncReport = MapCnc(payload.CncReport);

        // Sign overlay GLB paths in parallel; silently skip on error (analysis still usable)
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
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to sign overlay URLs for {StoragePath}", storagePath);
            }
        }

        // Merge into status cache — prior reports from other process events survive
        await status.SetDfmReportsAsync(
            storagePath, fdmReport, slaReport, cncReport,
            overlayUrls, payload.NonManifoldReason, null,
            context.CancellationToken);

        // Read back the merged state so the SignalR payload reflects all events received so far
        var merged = await status.GetStatusAsync(storagePath, context.CancellationToken);

        var signalRPayload = new QeDfmAnalysisReadyPayload(
            StoragePath: storagePath,
            IsManifold: merged?.IsManifold ?? true,
            NonManifoldReason: payload.NonManifoldReason,
            FdmReport: merged?.FdmReport,
            SlaReport: merged?.SlaReport,
            CncReport: merged?.CncReport,
            OverlayGlbUrls: merged?.OverlayGlbUrls ?? [],
            AnalysisErrorCode: null);

        await hub.Clients
            .Group(QuoteNotificationsHub.FileGroup(storagePath))
            .SendAsync("DfmAnalysisReady", signalRPayload, context.CancellationToken);
    }

    // ── Mapping helpers ──────────────────────────────────────────────────────

    private static QeFdmDfmReport? MapFdm(object? raw)
    {
        var src = Deserialize<FdmDfmReportPayload>(raw);
        if (src is null) return null;
        return new QeFdmDfmReport(
            src.ThinWallCount,
            src.OverhangFaceCount,
            (decimal)src.OverhangAreaCm2,
            src.SupportRequired,
            src.SmallDetailCount,
            src.Issues.Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    private static QeSlaDfmReport? MapSla(object? raw)
    {
        var src = Deserialize<SlaDfmReportPayload>(raw);
        if (src is null) return null;
        // HollowRegions in the contract is a list of centroid coordinates, not a bool flag;
        // treat as "has hollow regions" when the list is non-empty.
        return new QeSlaDfmReport(
            src.ThinWallCount,
            src.ResinTrappingRisk,
            src.SuctionRisk,
            src.HollowRegions.Count > 0,
            src.Issues.Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    private static QeCncDfmReport? MapCnc(object? raw)
    {
        var src = Deserialize<CncDfmReportPayload>(raw);
        if (src is null) return null;
        return new QeCncDfmReport(
            src.SharpCornerCount,
            src.HasUndercuts,
            src.HasDrillHoles,
            src.DrillHoleCount,
            src.RequiresEdm,
            src.RequiresGrinding,
            src.IsTurnable,
            src.Issues.Select(i => new QeDfmIssueItem(i.Severity, i.Category, i.Description)).ToList());
    }

    /// <summary>
    /// Handles the three states for payload fields typed as <c>object</c> in the contract:
    /// already the right CLR type, a JsonElement from MassTransit deserialization, or null.
    /// </summary>
    private static T? Deserialize<T>(object? raw) where T : class
    {
        return raw switch
        {
            null => null,
            T typed => typed,
            JsonElement el => el.Deserialize<T>(JsonOpts),
            _ => null
        };
    }

    /// <summary>
    /// Extracts a list of strings from the <c>object</c>-typed OverlayPaths field,
    /// handling both direct IEnumerable&lt;string&gt; and JsonElement array forms.
    /// </summary>
    private static IReadOnlyList<string> ExtractStringList(object? raw)
    {
        return raw switch
        {
            null => [],
            IEnumerable<string> list => list.ToList(),
            JsonElement el when el.ValueKind == JsonValueKind.Array =>
                el.EnumerateArray()
                  .Select(e => e.GetString() ?? "")
                  .Where(s => s.Length > 0)
                  .ToList(),
            _ => []
        };
    }
}
