using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Maliev.QuoteEngine.Bff.Clients;
using Maliev.QuoteEngine.Bff.Services;
using Maliev.QuoteEngine.Shared.Quotes;
using NSubstitute;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAnalysisPreviewUrlResolverTests
{
    private const string SourcePath = "quotes/temp/session/upload/bracket.step";
    private const string ViewerPath = SourcePath + "_viewer.glb";
    private const string ThumbnailPath = SourcePath + "_thumbnail_small.webp";

    [Fact]
    public void ResolveAsync_ContractAcceptsStatusAndCancellationOnly()
    {
        var method = Assert.Single(
            typeof(IQuoteAnalysisPreviewUrlResolver).GetMethods(),
            candidate => candidate.Name == nameof(IQuoteAnalysisPreviewUrlResolver.ResolveAsync));

        Assert.Equal(
            [typeof(string), typeof(QuoteFileAnalysisStatus), typeof(CancellationToken)],
            method.GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public async Task ResolveAsync_MissingCanonicalUrls_SignsEachDistinctPathOnceAndRefreshesStatus()
    {
        var store = await CreateStatusAsync(includeThumbnail: true, includeOverlays: true);
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new GatedRecordingUploadClient(expectedDistinctPaths: 4);
        var resolver = CreateResolver(store, signer);

        var pending = Enumerable.Range(0, 16)
            .Select(_ => resolver.ResolveAsync(SourcePath, status))
            .ToArray();
        await signer.AllPathsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        signer.Release();
        var results = await Task.WhenAll(pending);

        Assert.All(results, result => Assert.Equal("ready", result.Availability));
        Assert.All(results, result => Assert.Null(result.RetryAfterSeconds));
        Assert.Equal(1, signer.AttemptsFor(ViewerPath));
        Assert.Equal(1, signer.AttemptsFor(ThumbnailPath));
        Assert.Equal(1, signer.AttemptsFor(SourcePath + "_thin_wall_overlay.glb"));
        Assert.Equal(1, signer.AttemptsFor(SourcePath + "_overhang_overlay.glb"));
        var refreshed = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        Assert.Equal(status.Revision, refreshed.Revision);
        Assert.Equal(Signed(ViewerPath), refreshed.GlbUrl);
        Assert.Equal(Signed(ThumbnailPath), refreshed.ThumbnailUrl);
        Assert.Equal(
            [Signed(SourcePath + "_thin_wall_overlay.glb"), Signed(SourcePath + "_overhang_overlay.glb")],
            refreshed.OverlayGlbUrls);
    }

    [Fact]
    public async Task ResolveAsync_SignerFailure_ReturnsDurableStatusAndBoundedRetryWithoutRetryStorm()
    {
        var store = await CreateStatusAsync();
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new FailingUploadClient();
        var resolver = CreateResolver(store, signer);

        var first = await resolver.ResolveAsync(SourcePath, status);
        var second = await resolver.ResolveAsync(SourcePath, status);

        Assert.Equal("temporarily_unavailable", first.Availability);
        Assert.Equal(5, first.RetryAfterSeconds);
        Assert.Equal(status, first.Status);
        Assert.Equal("temporarily_unavailable", second.Availability);
        Assert.InRange(second.RetryAfterSeconds ?? 0, 1, 5);
        Assert.Equal(1, signer.Attempts);
        Assert.True(string.IsNullOrWhiteSpace((await store.GetStatusAsync(SourcePath))?.GlbUrl));
    }

    [Fact]
    public async Task ResolveAsync_CancelledWaiter_DoesNotCancelSharedRefreshForOtherWaiters()
    {
        var store = await CreateStatusAsync();
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new BlockingUploadClient();
        var resolver = CreateResolver(store, signer);
        using var cancelledWaiter = new CancellationTokenSource();

        var first = resolver.ResolveAsync(SourcePath, status, cancelledWaiter.Token);
        await signer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = resolver.ResolveAsync(SourcePath, status);
        cancelledWaiter.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        signer.Release(Signed(ViewerPath));
        var completed = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ready", completed.Availability);
        Assert.Equal(Signed(ViewerPath), completed.Status.GlbUrl);
        Assert.Equal(1, signer.Attempts);
    }

    public static TheoryData<QuoteFileAnalysisStatus> UnsafeStatuses =>
        new()
        {
            CreateRawStatus("../other-customer/private.step", "../other-customer/private.step_viewer.glb"),
            CreateRawStatus(SourcePath, "other/customer/private.glb"),
            CreateRawStatus(SourcePath, ViewerPath, "other/customer/private.webp"),
            CreateRawStatus(
                SourcePath,
                ViewerPath,
                null,
                [SourcePath + "_../../private_overlay.glb"]),
            CreateRawStatus("https://storage.example/private.step", "https://storage.example/private.step_viewer.glb"),
            CreateRawStatus("/absolute/private.step", "/absolute/private.step_viewer.glb")
        };

    [Theory]
    [MemberData(nameof(UnsafeStatuses))]
    public async Task ResolveAsync_NonCanonicalSourcePath_FailsSoftWithoutCallingSigner(
        QuoteFileAnalysisStatus unsafeStatus)
    {
        var store = new QuoteFileAnalysisStatusService();
        var signer = new RecordingUploadClient();
        var resolver = CreateResolver(store, signer);

        var result = await resolver.ResolveAsync(unsafeStatus.StoragePath, unsafeStatus);

        Assert.Equal("temporarily_unavailable", result.Availability);
        Assert.Equal(5, result.RetryAfterSeconds);
        Assert.Equal(unsafeStatus.Revision, result.Status.Revision);
        Assert.Equal(unsafeStatus.Status, result.Status.Status);
        Assert.Null(result.Status.ViewerStoragePath);
        Assert.Null(result.Status.ThumbnailStoragePath);
        Assert.Empty(result.Status.AuthoritativeOverlayStoragePaths);
        Assert.Equal(0, signer.TotalAttempts);
    }

    [Fact]
    public async Task ResolveAsync_UploadServiceReturnsNonHttpsUrl_FailsSoftWithoutPersistingCapability()
    {
        var store = await CreateStatusAsync();
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new FixedUploadClient("http://unsigned.example/viewer");
        var resolver = CreateResolver(store, signer);

        var result = await resolver.ResolveAsync(SourcePath, status);

        Assert.Equal("temporarily_unavailable", result.Availability);
        Assert.Equal(1, signer.Attempts);
        Assert.True(string.IsNullOrWhiteSpace((await store.GetStatusAsync(SourcePath))?.GlbUrl));
    }

    [Fact]
    public async Task ResolveAsync_StatusIdentityDiffersFromOwnedUpload_DoesNotCallSigner()
    {
        var status = CreateRawStatus(
            "quotes/other/customer/private.step",
            "quotes/other/customer/private.step_viewer.glb");
        var signer = new RecordingUploadClient();
        var resolver = CreateResolver(new QuoteFileAnalysisStatusService(), signer);

        var result = await resolver.ResolveAsync(SourcePath, status);

        Assert.Equal("temporarily_unavailable", result.Availability);
        Assert.Equal(0, signer.TotalAttempts);
        Assert.True(string.IsNullOrWhiteSpace(result.Status.GlbUrl));
    }

    [Fact]
    public async Task ResolveAsync_DfmBeforeGeometryWithoutPreviewSources_DoesNotSignRepeatedly()
    {
        var store = new QuoteFileAnalysisStatusService();
        await store.SetDfmReportsAsync(
            SourcePath,
            new QeFdmDfmReport(0, 0, 0, false, 0, []),
            null,
            null,
            [],
            null,
            null,
            fileId: "file-owner-1",
            eventId: Guid.NewGuid());
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new RecordingUploadClient();
        var resolver = CreateResolver(store, signer);

        var first = await resolver.ResolveAsync(SourcePath, status);
        var second = await resolver.ResolveAsync(SourcePath, status);

        Assert.Equal("unavailable", first.Availability);
        Assert.Equal("unavailable", second.Availability);
        Assert.Null(status.ViewerStoragePath);
        Assert.Equal(0, signer.TotalAttempts);
    }

    [Fact]
    public async Task ResolveAsync_CapabilityWithoutCanonicalSource_IsNeverReturned()
    {
        var status = CreateRawStatus(SourcePath, viewerPath: null) with
        {
            GlbUrl = "https://signed.example/unbound-object"
        };
        var signer = new RecordingUploadClient();
        var resolver = CreateResolver(new QuoteFileAnalysisStatusService(), signer);

        var result = await resolver.ResolveAsync(SourcePath, status);

        Assert.Equal("unavailable", result.Availability);
        Assert.Null(result.Status.GlbUrl);
        Assert.Equal(0, signer.TotalAttempts);
    }

    [Fact]
    public async Task ResolveAsync_RevisionAdvancesDuringSigning_ReturnsLatestFactsWithoutCapability()
    {
        var store = await CreateStatusAsync();
        var initial = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new BlockingUploadClient();
        var resolver = CreateResolver(store, signer);

        var resolution = resolver.ResolveAsync(SourcePath, initial);
        await signer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await store.SetDfmReportsAsync(
            SourcePath,
            new QeFdmDfmReport(0, 0, 0, false, 0, []),
            null,
            null,
            [],
            null,
            null,
            fileId: "file-owner-1",
            eventId: Guid.NewGuid());
        signer.Release(Signed(ViewerPath));

        var result = await resolution;

        Assert.Equal("temporarily_unavailable", result.Availability);
        Assert.True(result.Status.Revision > initial.Revision);
        Assert.Equal("DfmAnalysisReady", result.Status.Status);
        Assert.True(string.IsNullOrWhiteSpace(result.Status.GlbUrl));
    }

    [Fact]
    public async Task ResolveAsync_SoleCancelledWaiter_DoesNotRetainCompletedSingleFlight()
    {
        var store = await CreateStatusAsync();
        var status = Assert.IsType<QuoteFileAnalysisStatus>(await store.GetStatusAsync(SourcePath));
        var signer = new BlockingUploadClient();
        var resolver = CreateResolver(store, signer);
        using var cancellation = new CancellationTokenSource();

        var pending = resolver.ResolveAsync(SourcePath, status, cancellation.Token);
        await signer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        signer.Release(Signed(ViewerPath));

        await AssertEventuallyAsync(() => resolver.ActiveRefreshCount == 0);
    }

    private static QuoteAnalysisPreviewUrlResolver CreateResolver(
        IQuoteFileAnalysisStatusService store,
        QuoteUploadServiceClient signer)
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);
        return new QuoteAnalysisPreviewUrlResolver(
            store,
            signer,
            redis: null,
            TimeProvider.System,
            lifetime,
            NullLogger<QuoteAnalysisPreviewUrlResolver>.Instance);
    }

    private static async Task<QuoteFileAnalysisStatusService> CreateStatusAsync(
        bool includeThumbnail = false,
        bool includeOverlays = false)
    {
        var store = new QuoteFileAnalysisStatusService();
        await store.SetGlbReadyAsync(
            SourcePath,
            glbUrl: string.Empty,
            thumbnailUrl: null,
            bodyCount: 1,
            isManifold: true,
            viewerStoragePath: ViewerPath,
            viewerFileExtension: ".glb",
            fileId: "file-owner-1",
            thumbnailStoragePath: includeThumbnail ? ThumbnailPath : null);
        if (includeOverlays)
        {
            await store.SetDfmReportsAsync(
                SourcePath,
                fdmReport: null,
                slaReport: null,
                cncReport: null,
                overlayGlbUrls: [],
                nonManifoldReason: null,
                analysisErrorCode: null,
                fileId: "file-owner-1",
                overlayStoragePaths:
                [
                    SourcePath + "_thin_wall_overlay.glb",
                    SourcePath + "_overhang_overlay.glb"
                ]);
        }

        return store;
    }

    private static QuoteFileAnalysisStatus CreateRawStatus(
        string sourcePath,
        string? viewerPath,
        string? thumbnailPath = null,
        IReadOnlyList<string>? overlays = null) =>
        new()
        {
            Revision = 7,
            StoragePath = sourcePath,
            FileId = "file-owner-1",
            Status = "DfmAnalysisReady",
            IsAuthoritative = true,
            ViewerStoragePath = viewerPath,
            ThumbnailStoragePath = thumbnailPath,
            AuthoritativeOverlayStoragePaths = overlays ?? []
        };

    private static string Signed(string path) =>
        "https://signed.example/" + Uri.EscapeDataString(path);

    private static async Task AssertEventuallyAsync(Func<bool> assertion)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!assertion() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        Assert.True(assertion());
    }

    private sealed class RecordingUploadClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        private readonly ConcurrentDictionary<string, int> _attempts =
            new(StringComparer.Ordinal);

        public int TotalAttempts => _attempts.Values.Sum();

        public int AttemptsFor(string path) => _attempts.GetValueOrDefault(path);

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Assert.InRange(expirationMinutes, 1, 60);
            _attempts.AddOrUpdate(storagePath, 1, (_, attempts) => attempts + 1);
            return Task.FromResult(Signed(storagePath));
        }
    }

    private sealed class GatedRecordingUploadClient(int expectedDistinctPaths)
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        private readonly ConcurrentDictionary<string, int> _attempts =
            new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllPathsStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AttemptsFor(string path) => _attempts.GetValueOrDefault(path);

        public void Release() => _release.TrySetResult();

        public override async Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            _attempts.AddOrUpdate(storagePath, 1, (_, attempts) => attempts + 1);
            if (_attempts.Count == expectedDistinctPaths)
                AllPathsStarted.TrySetResult();
            await _release.Task.WaitAsync(ct);
            return Signed(storagePath);
        }
    }

    private sealed class FailingUploadClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public int Attempts { get; private set; }

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromException<string>(new HttpRequestException("simulated signer outage"));
        }
    }

    private sealed class BlockingUploadClient()
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        private readonly TaskCompletionSource<string> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Attempts { get; private set; }

        public void Release(string url) => _release.TrySetResult(url);

        public override async Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Attempts++;
            Started.TrySetResult();
            return await _release.Task.WaitAsync(ct);
        }
    }

    private sealed class FixedUploadClient(string url)
        : QuoteUploadServiceClient(new HttpClient(), NullLogger<QuoteUploadServiceClient>.Instance)
    {
        public int Attempts { get; private set; }

        public override Task<string> GetDownloadUrlByPathAsync(
            string storagePath,
            int expirationMinutes = 60,
            CancellationToken ct = default)
        {
            Attempts++;
            return Task.FromResult(url);
        }
    }
}
