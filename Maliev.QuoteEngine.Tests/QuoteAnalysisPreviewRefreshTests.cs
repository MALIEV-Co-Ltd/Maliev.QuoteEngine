using Maliev.QuoteEngine.Bff.Services;

namespace Maliev.QuoteEngine.Tests;

public sealed class QuoteAnalysisPreviewRefreshTests
{
    private const string SourcePath = "quotes/temp/session/upload/bracket.step";
    private const string ViewerPath = SourcePath + "_viewer.glb";
    private const string ThumbnailPath = SourcePath + "_thumbnail_small.webp";
    private static readonly string[] OverlayPaths =
    [
        SourcePath + "_thin_wall_overlay.glb",
        SourcePath + "_overhang_overlay.glb"
    ];

    [Fact]
    public async Task RefreshPreviewUrlsAsync_MatchingRevisionAndCanonicalPaths_ReplacesOnlyEphemeralUrls()
    {
        var store = await CreateSeededStoreAsync();
        var before = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.GetStatusAsync(SourcePath));
        var refresh = CreateRefresh(before.Revision);

        var refreshed = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.RefreshPreviewUrlsAsync(SourcePath, refresh));

        Assert.Equal(before.Revision, refreshed.Revision);
        Assert.Equal(before.StoragePath, refreshed.StoragePath);
        Assert.Equal(before.FileId, refreshed.FileId);
        Assert.Equal(before.Status, refreshed.Status);
        Assert.Equal(before.VolumeCc, refreshed.VolumeCc);
        Assert.Equal(ViewerPath, refreshed.ViewerStoragePath);
        Assert.Equal(ThumbnailPath, refreshed.ThumbnailStoragePath);
        Assert.Equal(OverlayPaths, refreshed.AuthoritativeOverlayStoragePaths);
        Assert.Equal("https://signed.example/viewer", refreshed.GlbUrl);
        Assert.Equal("https://signed.example/thumbnail", refreshed.ThumbnailUrl);
        Assert.Equal(
            ["https://signed.example/thin-wall", "https://signed.example/overhang"],
            refreshed.OverlayGlbUrls);
    }

    [Fact]
    public async Task RefreshPreviewUrlsAsync_StaleRevision_DoesNotOverwriteCurrentUrls()
    {
        var store = await CreateSeededStoreAsync();
        var before = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.GetStatusAsync(SourcePath));
        var staleRefresh = CreateRefresh(before.Revision - 1);

        var result = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.RefreshPreviewUrlsAsync(SourcePath, staleRefresh));

        Assert.Equal(before, result);
        Assert.Equal("https://expired.example/viewer", result.GlbUrl);
        Assert.Equal("https://expired.example/thumbnail", result.ThumbnailUrl);
        Assert.Equal(
            ["https://expired.example/thin-wall", "https://expired.example/overhang"],
            result.OverlayGlbUrls);
    }

    [Theory]
    [InlineData("viewer")]
    [InlineData("thumbnail")]
    [InlineData("overlay")]
    public async Task RefreshPreviewUrlsAsync_MismatchedCanonicalPath_DoesNotOverwriteCurrentUrls(
        string mismatchedField)
    {
        var store = await CreateSeededStoreAsync();
        var before = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.GetStatusAsync(SourcePath));
        var valid = CreateRefresh(before.Revision);
        var mismatched = mismatchedField switch
        {
            "viewer" => valid with { ViewerStoragePath = "other/customer/private.glb" },
            "thumbnail" => valid with { ThumbnailStoragePath = "other/customer/private.webp" },
            "overlay" => valid with
            {
                OverlayStoragePaths = [OverlayPaths[0], "other/customer/private_overlay.glb"]
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchedField))
        };

        var result = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.RefreshPreviewUrlsAsync(SourcePath, mismatched));

        Assert.Equal(before, result);
    }

    [Fact]
    public async Task RefreshPreviewUrlsAsync_CancelledRequest_DoesNotMutateStatus()
    {
        var store = await CreateSeededStoreAsync();
        var before = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.GetStatusAsync(SourcePath));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.RefreshPreviewUrlsAsync(SourcePath, CreateRefresh(before.Revision), cancellation.Token));

        Assert.Equal(before, await store.GetStatusAsync(SourcePath));
    }

    [Fact]
    public async Task RefreshPreviewUrlsAsync_UrlWithoutMatchingCanonicalSource_DoesNotCreateCapability()
    {
        var store = new QuoteFileAnalysisStatusService();
        await store.SetProcessingAsync(SourcePath);
        var before = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.GetStatusAsync(SourcePath));
        var injected = new QuoteAnalysisPreviewRefresh(
            before.Revision,
            ViewerStoragePath: null,
            ViewerUrl: "https://signed.example/unbound-object",
            ThumbnailStoragePath: null,
            ThumbnailUrl: "https://signed.example/unbound-thumbnail",
            OverlayStoragePaths: [],
            OverlayUrls: []);

        var result = Assert.IsType<QuoteFileAnalysisStatus>(
            await store.RefreshPreviewUrlsAsync(SourcePath, injected));

        Assert.Equal(before, result);
        Assert.Null(result.GlbUrl);
        Assert.Null(result.ThumbnailUrl);
    }

    private static async Task<QuoteFileAnalysisStatusService> CreateSeededStoreAsync()
    {
        var store = new QuoteFileAnalysisStatusService();
        await store.SetGlbReadyAsync(
            SourcePath,
            "https://expired.example/viewer",
            "https://expired.example/thumbnail",
            bodyCount: 1,
            isManifold: true,
            viewerStoragePath: ViewerPath,
            viewerFileExtension: ".glb",
            volumeCc: 12.5m,
            fileId: "file-owner-1",
            thumbnailStoragePath: ThumbnailPath);
        await store.SetDfmReportsAsync(
            SourcePath,
            fdmReport: null,
            slaReport: null,
            cncReport: null,
            overlayGlbUrls:
            [
                "https://expired.example/thin-wall",
                "https://expired.example/overhang"
            ],
            nonManifoldReason: null,
            analysisErrorCode: null,
            fileId: "file-owner-1",
            overlayStoragePaths: OverlayPaths);
        return store;
    }

    private static QuoteAnalysisPreviewRefresh CreateRefresh(long expectedRevision) =>
        new(
            expectedRevision,
            ViewerPath,
            "https://signed.example/viewer",
            ThumbnailPath,
            "https://signed.example/thumbnail",
            OverlayPaths,
            ["https://signed.example/thin-wall", "https://signed.example/overhang"]);
}
