using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteFileAnalysisStatusTransitionsTests
{
    private const string StoragePath = "quotes/temp/session/part.step";
    private const string FileId = "file-123";
    private static readonly DateTimeOffset BaseTime = DateTimeOffset.Parse("2026-07-14T01:02:03Z");

    [Fact]
    public void ApplyDfmReports_GlbAlreadyReady_PreservesGeometryPreview()
    {
        var glb = QuoteFileAnalysisStatusTransitions.ApplyGlbReady(
            null,
            Completion(eventId: Guid.NewGuid()));

        var result = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            glb,
            Dfm(
                fdmReport: Fdm(),
                overlays: ["https://cdn/overhang.glb"],
                overlayStoragePaths:
                [
                    StoragePath + "_overhang_overlay.glb",
                    StoragePath + "_overhang_overlay.glb"
                ]));

        Assert.Equal("DfmAnalysisReady", result.Status);
        Assert.Equal("https://cdn/part.glb", result.GlbUrl);
        Assert.Equal(StoragePath + "_viewer.glb", result.ViewerStoragePath);
        Assert.Equal(StoragePath + "_thumbnail_small.webp", result.ThumbnailStoragePath);
        Assert.Equal("https://cdn/part.png", result.ThumbnailUrl);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.NotNull(result.FdmReport);
        Assert.Equal(["https://cdn/overhang.glb"], result.OverlayGlbUrls);
        Assert.Equal([StoragePath + "_overhang_overlay.glb"], result.AuthoritativeOverlayStoragePaths);
    }

    [Fact]
    public void ApplyGlbReady_DfmAlreadyReady_PreservesDfmReportsAndOverlays()
    {
        var dfm = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm(), overlays: ["https://cdn/thin-wall.glb"]));

        var result = QuoteFileAnalysisStatusTransitions.ApplyGlbReady(
            dfm,
            Completion(eventId: Guid.NewGuid()));

        Assert.Equal("DfmAnalysisReady", result.Status);
        Assert.Equal("https://cdn/part.glb", result.GlbUrl);
        Assert.NotNull(result.FdmReport);
        Assert.Equal(["https://cdn/thin-wall.glb"], result.OverlayGlbUrls);
        Assert.True(result.HasAuthoritativeDfm);
    }

    [Fact]
    public void ApplyDfmReports_SequentialFdmThenCnc_AccumulatesReportsAndDistinctOverlays()
    {
        var fdm = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm(), overlays: ["https://cdn/shared.glb"]));

        var result = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            fdm,
            Dfm(
                cncReport: Cnc(),
                overlays: ["https://cdn/shared.glb", "https://cdn/cnc.glb"],
                eventId: Guid.NewGuid()));

        Assert.NotNull(result.FdmReport);
        Assert.NotNull(result.CncReport);
        Assert.Equal(["https://cdn/shared.glb", "https://cdn/cnc.glb"], result.OverlayGlbUrls);
    }

    [Fact]
    public async Task InMemoryAdapter_ConcurrentFdmAndCncEvents_MergesBothReportsAndOverlays()
    {
        var service = new QuoteFileAnalysisStatusService();
        using var gate = new ManualResetEventSlim(false);
        var fdmTask = Task.Run(async () =>
        {
            gate.Wait();
            await service.SetDfmReportsAsync(
                StoragePath, Fdm(), null, null, ["https://cdn/fdm.glb"], null, null,
                fileId: FileId, eventId: Guid.NewGuid());
        });
        var cncTask = Task.Run(async () =>
        {
            gate.Wait();
            await service.SetDfmReportsAsync(
                StoragePath, null, null, Cnc(), ["https://cdn/cnc.glb"], null, null,
                fileId: FileId, eventId: Guid.NewGuid());
        });

        gate.Set();
        await Task.WhenAll(fdmTask, cncTask);

        var result = await service.GetStatusAsync(StoragePath);
        Assert.NotNull(result);
        Assert.NotNull(result.FdmReport);
        Assert.NotNull(result.CncReport);
        Assert.Equal(2, result.OverlayGlbUrls.Count);
        Assert.Contains("https://cdn/fdm.glb", result.OverlayGlbUrls);
        Assert.Contains("https://cdn/cnc.glb", result.OverlayGlbUrls);
    }

    [Fact]
    public void ApplyProcessing_AuthoritativeMetricsAlreadyStored_PreservesMetricsState()
    {
        var metrics = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            null,
            Metrics(Guid.NewGuid(), BaseTime));

        var result = QuoteFileAnalysisStatusTransitions.ApplyProcessing(metrics, StoragePath);

        Assert.Same(metrics, result);
        Assert.Equal("Processing", result.Status);
        Assert.True(result.IsAuthoritative);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.True(result.HasAuthoritativeGeometry);
    }

    [Fact]
    public void ApplyProcessing_AuthoritativeFailureAlreadyStored_PreservesFailure()
    {
        var failure = QuoteFileAnalysisStatusTransitions.ApplyOrderedFailure(
            null,
            new GeometryFailureTransition(
                StoragePath, FileId, "worker_failed", Guid.NewGuid(), BaseTime));

        var result = QuoteFileAnalysisStatusTransitions.ApplyProcessing(failure, StoragePath);

        Assert.Same(failure, result);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("worker_failed", result.AnalysisErrorCode);
    }

    [Fact]
    public void GeometryReplayAndOrdering_OlderDuplicateAndWrongFileEventsAreRejected()
    {
        var eventId = Guid.NewGuid();
        var current = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            null,
            Metrics(eventId, BaseTime));

        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, eventId, BaseTime.AddMinutes(1), BaseTime.AddMinutes(1),
            QuoteGeometryEventPhase.Completion));
        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, Guid.NewGuid(), BaseTime.AddSeconds(-1), BaseTime.AddSeconds(-1),
            QuoteGeometryEventPhase.Completion));
        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, "different-file", Guid.NewGuid(), BaseTime.AddMinutes(1), BaseTime.AddMinutes(1),
            QuoteGeometryEventPhase.Completion));
        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, Guid.NewGuid(), BaseTime, BaseTime,
            QuoteGeometryEventPhase.Completion));
    }

    [Fact]
    public void DfmReplayAndIdentity_DuplicateAndWrongFileEventsAreRejected()
    {
        var eventId = Guid.NewGuid();
        var current = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm(), eventId: eventId));

        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyDfm(current, FileId, eventId));
        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyDfm(current, "different-file", Guid.NewGuid()));
        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyDfm(current, FileId, Guid.NewGuid()));
    }

    [Fact]
    public void ApplyDfmReports_GuidEmptyEventsRemainMergeableAndUntracked()
    {
        var first = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm(), eventId: Guid.Empty));

        var result = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            first,
            Dfm(cncReport: Cnc(), eventId: Guid.Empty));

        Assert.NotNull(result.FdmReport);
        Assert.NotNull(result.CncReport);
        Assert.Empty(result.ProcessedDfmEventIds);
        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyDfm(result, FileId, Guid.Empty));
    }

    [Fact]
    public void ApplyDfmReports_ExistingNonManifoldStateStaysStickyWithoutAReplacementReason()
    {
        var nonManifold = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm()) with { NonManifoldReason = "open shell" });

        var result = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            nonManifold,
            Dfm(cncReport: Cnc(), eventId: Guid.NewGuid()));

        Assert.False(result.IsManifold);
        Assert.Equal("open shell", result.NonManifoldReason);
    }

    [Fact]
    public async Task InMemoryAdapter_StoragePathLookupRemainsCaseInsensitive()
    {
        var service = new QuoteFileAnalysisStatusService();
        await service.SetProcessingAsync("QUOTES/Temp/Part.step");

        var result = await service.GetStatusAsync("quotes/temp/part.STEP");

        Assert.NotNull(result);
        Assert.Equal("QUOTES/Temp/Part.step", result.StoragePath);
    }

    [Fact]
    public void ApplyGeometryMetrics_OlderCompleteMetricsBackfillSparseCompletionWithoutChangingTerminalClock()
    {
        var completionId = Guid.NewGuid();
        var completion = QuoteFileAnalysisStatusTransitions.ApplyGlbReady(
            null,
            Completion(completionId) with
            {
                VolumeCc = null,
                BoundingBoxXmm = null,
                BoundingBoxYmm = null,
                BoundingBoxZmm = null,
                TriangleCount = null
            });

        var result = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            completion,
            Metrics(Guid.NewGuid(), BaseTime.AddSeconds(-2)));

        Assert.Equal("GlbReady", result.Status);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.True(result.HasAuthoritativeGeometry);
        Assert.Equal(completionId, result.LastGeometryEventId);
        Assert.Equal(QuoteGeometryEventPhase.Completion, (QuoteGeometryEventPhase)result.LastGeometryEventPhase);
        Assert.Equal(completion.LastGeometryProcessedAtUtc, result.LastGeometryProcessedAtUtc);
    }

    [Fact]
    public void ApplyGeometryMetrics_IncompleteNewerMetricsCannotOverwriteValidGeometry()
    {
        var current = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            null,
            Metrics(Guid.NewGuid(), BaseTime));

        var result = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            current,
            Metrics(Guid.NewGuid(), BaseTime.AddMinutes(1)) with
            {
                VolumeCc = null,
                BoundingBoxXmm = null,
                BoundingBoxYmm = null,
                BoundingBoxZmm = null,
                TriangleCount = null,
                BodyCount = 99,
                IsManifold = false,
                NonManifoldReason = "invalid replacement"
            });

        Assert.Same(current, result);
        Assert.Equal(12.5m, result.VolumeCc);
        Assert.Equal(1, result.BodyCount);
        Assert.True(result.IsManifold);
    }

    [Fact]
    public void ApplyGeometryMetrics_DfmAlreadyReady_PreservesDfmTerminalState()
    {
        var dfm = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
            null,
            Dfm(fdmReport: Fdm(), overlays: ["https://cdn/dfm.glb"]));

        var result = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            dfm,
            Metrics(Guid.NewGuid(), BaseTime.AddSeconds(1)));

        Assert.Equal("DfmAnalysisReady", result.Status);
        Assert.NotNull(result.FdmReport);
        Assert.Equal(["https://cdn/dfm.glb"], result.OverlayGlbUrls);
        Assert.Equal(12.5m, result.VolumeCc);
    }

    [Fact]
    public void ApplyLocalAdvisory_OnlyChangesAdvisoryAndKeepsDuplicateOverlayOrder()
    {
        var authoritative = QuoteFileAnalysisStatusTransitions.ApplyGlbReady(
            null,
            Completion(Guid.NewGuid()));
        var withGeometryAdvisory = QuoteFileAnalysisStatusTransitions.ApplyLocalGeometryMetrics(
            authoritative,
            new LocalGeometryTransition(StoragePath, 99m, 101m, false, "browser open shell"));

        var result = QuoteFileAnalysisStatusTransitions.ApplyLocalDfmReports(
            withGeometryAdvisory,
            new LocalDfmReportsTransition(
                StoragePath,
                Fdm(),
                null,
                null,
                ["overlay-a", "overlay-a"],
                "browser issue"));

        Assert.Equal(authoritative.Status, result.Status);
        Assert.Equal(authoritative.VolumeCc, result.VolumeCc);
        Assert.Equal(authoritative.LastGeometryEventId, result.LastGeometryEventId);
        Assert.Equal(99m, result.AdvisoryAnalysis?.VolumeCc);
        Assert.Equal(["overlay-a", "overlay-a"], result.AdvisoryAnalysis?.OverlayGlbUrls);
        Assert.Empty(authoritative.OverlayGlbUrls);
        Assert.Null(authoritative.AdvisoryAnalysis);
    }

    [Fact]
    public void GeometryOrdering_EqualClockUsesPhaseThenEventIdTieBreak()
    {
        var currentId = Guid.Parse("00000000-0000-0000-0000-000000000010");
        var current = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            null,
            Metrics(currentId, BaseTime));

        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            BaseTime, BaseTime, QuoteGeometryEventPhase.Completion));
        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, Guid.Parse("00000000-0000-0000-0000-000000000001"),
            BaseTime, BaseTime, QuoteGeometryEventPhase.Metrics));
        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            current, FileId, Guid.Parse("00000000-0000-0000-0000-000000000020"),
            BaseTime, BaseTime, QuoteGeometryEventPhase.Metrics));
    }

    [Fact]
    public void GeometryOrdering_GuidEmptyOnlyAppliesBeforeAnOrderedEventExists()
    {
        var cold = new QuoteFileAnalysisStatus { StoragePath = StoragePath };
        var ordered = QuoteFileAnalysisStatusTransitions.ApplyGeometryMetrics(
            null,
            Metrics(Guid.NewGuid(), BaseTime));

        Assert.True(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            cold, FileId, Guid.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue,
            QuoteGeometryEventPhase.Completion));
        Assert.False(QuoteFileAnalysisStatusTransitions.CanApplyGeometry(
            ordered, FileId, Guid.Empty, DateTimeOffset.MinValue, DateTimeOffset.MinValue,
            QuoteGeometryEventPhase.Completion));
    }

    [Fact]
    public void ApplyDfmReports_RetainsOnlyLatestThirtyTwoReplayIds()
    {
        QuoteFileAnalysisStatus? current = null;
        var ids = Enumerable.Range(0, 33).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids)
        {
            current = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(
                current,
                Dfm(fdmReport: Fdm(), eventId: id));
        }

        Assert.NotNull(current);
        Assert.Equal(32, current.ProcessedDfmEventIds.Count);
        Assert.DoesNotContain(ids[0], current.ProcessedDfmEventIds);
        Assert.Equal(ids.Skip(1), current.ProcessedDfmEventIds);
    }

    [Fact]
    public void ColdTransitions_ProduceSameCanonicalDefaultsWithoutMutatingInputs()
    {
        var overlays = new[] { "overlay-a" };
        var input = Dfm(fdmReport: Fdm(), overlays: overlays, eventId: Guid.NewGuid());

        var result = QuoteFileAnalysisStatusTransitions.ApplyDfmReports(null, input);

        Assert.Equal(StoragePath, result.StoragePath);
        Assert.Equal(FileId, result.FileId);
        Assert.Equal("server_analysis", result.AnalysisSource);
        Assert.True(result.IsAuthoritative);
        Assert.True(result.HasAuthoritativeDfm);
        Assert.Equal(1, result.BodyCount);
        Assert.Equal(["overlay-a"], result.OverlayGlbUrls);
        Assert.Equal(["overlay-a"], overlays);
    }

    private static GeometryCompletionTransition Completion(Guid eventId) => new(
        StoragePath,
        "https://cdn/part.glb",
        "https://cdn/part.png",
        1,
        true,
        StoragePath + "_viewer.glb",
        ".glb",
        12.5m,
        42.5m,
        FileId,
        1.25m,
        10m,
        20m,
        30m,
        456,
        null,
        eventId,
        BaseTime,
        BaseTime.AddSeconds(1),
        StoragePath + "_thumbnail_small.webp");

    private static GeometryMetricsTransition Metrics(Guid eventId, DateTimeOffset at) => new(
        StoragePath,
        FileId,
        12.5m,
        1.25m,
        42.5m,
        10m,
        20m,
        30m,
        456,
        1,
        true,
        null,
        eventId,
        at,
        at);

    private static DfmReportsTransition Dfm(
        QeFdmDfmReport? fdmReport = null,
        QeCncDfmReport? cncReport = null,
        IReadOnlyList<string>? overlays = null,
        IReadOnlyList<string>? overlayStoragePaths = null,
        Guid? eventId = null) => new(
            StoragePath,
            fdmReport,
            null,
            cncReport,
            overlays ?? [],
            null,
            null,
            FileId,
            eventId,
            BaseTime,
            BaseTime,
            1,
            overlayStoragePaths);

    private static QeFdmDfmReport Fdm() => new(1, 2, 3.5m, true, 4, []);

    private static QeCncDfmReport Cnc() => new(2, false, true, 3, false, false, false, []);
}
