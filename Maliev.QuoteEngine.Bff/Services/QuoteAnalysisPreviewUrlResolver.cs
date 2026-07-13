using System.Collections.Concurrent;
using Maliev.QuoteEngine.Bff.Clients;
using StackExchange.Redis;

namespace Maliev.QuoteEngine.Bff.Services;

public interface IQuoteAnalysisPreviewUrlResolver
{
    Task<QuoteAnalysisPreviewResolution> ResolveAsync(
        string ownedStoragePath,
        QuoteFileAnalysisStatus status,
        CancellationToken ct = default);
}

internal sealed class QuoteAnalysisPreviewUrlResolver : IQuoteAnalysisPreviewUrlResolver
{
    private static readonly TimeSpan RefreshLease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RefreshWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);
    private const int MaxLocalBackoffEntries = 1024;
    private readonly ConcurrentDictionary<string, Lazy<Task<QuoteAnalysisPreviewResolution>>> _refreshes =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _retryAfter =
        new(StringComparer.Ordinal);
    private readonly IQuoteFileAnalysisStatusService _statusService;
    private readonly QuoteUploadServiceClient _uploadClient;
    private readonly IDatabase? _redis;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly ILogger<QuoteAnalysisPreviewUrlResolver> _logger;

    internal int ActiveRefreshCount => _refreshes.Count;
    internal int LocalBackoffCount => _retryAfter.Count;

    public QuoteAnalysisPreviewUrlResolver(
        IQuoteFileAnalysisStatusService statusService,
        QuoteUploadServiceClient uploadClient,
        IServiceProvider services,
        IHostApplicationLifetime applicationLifetime,
        ILogger<QuoteAnalysisPreviewUrlResolver> logger)
        : this(
            statusService,
            uploadClient,
            services.GetService<IConnectionMultiplexer>(),
            services.GetService<TimeProvider>() ?? TimeProvider.System,
            applicationLifetime,
            logger)
    {
    }

    internal QuoteAnalysisPreviewUrlResolver(
        IQuoteFileAnalysisStatusService statusService,
        QuoteUploadServiceClient uploadClient,
        IConnectionMultiplexer? redis,
        TimeProvider timeProvider,
        IHostApplicationLifetime applicationLifetime,
        ILogger<QuoteAnalysisPreviewUrlResolver> logger)
    {
        _statusService = statusService;
        _uploadClient = uploadClient;
        _redis = redis?.GetDatabase();
        _timeProvider = timeProvider;
        _applicationLifetime = applicationLifetime;
        _logger = logger;
    }

    public async Task<QuoteAnalysisPreviewResolution> ResolveAsync(
        string ownedStoragePath,
        QuoteFileAnalysisStatus status,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!string.Equals(ownedStoragePath, status.StoragePath, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Analysis status identity did not match the authorized upload storage path.");
            return new QuoteAnalysisPreviewResolution(
                StripUntrustedPreview(status),
                "temporarily_unavailable",
                (int)FailureBackoff.TotalSeconds);
        }
        if (!HasRefreshableSources(status))
        {
            return new QuoteAnalysisPreviewResolution(
                StripCapabilities(status),
                IsAnalysisPending(status) ? "pending" : "unavailable");
        }

        try
        {
            ValidateCanonicalSources(status);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Analysis preview source paths are invalid for {StoragePath} at revision {Revision}",
                status.StoragePath,
                status.Revision);
            return new QuoteAnalysisPreviewResolution(
                StripUntrustedPreview(status),
                "temporarily_unavailable",
                (int)FailureBackoff.TotalSeconds);
        }

        if (!NeedsRefresh(status))
            return new QuoteAnalysisPreviewResolution(status, "ready");

        var key = $"{status.StoragePath}\n{status.Revision}";
        if (_retryAfter.TryGetValue(key, out var retryAt) && retryAt > _timeProvider.GetUtcNow())
        {
            return new QuoteAnalysisPreviewResolution(
                StripCapabilities(status),
                "temporarily_unavailable",
                Math.Max(1, (int)Math.Ceiling((retryAt - _timeProvider.GetUtcNow()).TotalSeconds)));
        }

        Lazy<Task<QuoteAnalysisPreviewResolution>>? created = null;
        created = new Lazy<Task<QuoteAnalysisPreviewResolution>>(
            () => RefreshAndCleanupAsync(
                ownedStoragePath,
                status,
                key,
                created!,
                _applicationLifetime.ApplicationStopping),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _refreshes.GetOrAdd(key, created);
        return await lazy.Value.WaitAsync(ct);
    }

    private async Task<QuoteAnalysisPreviewResolution> RefreshAndCleanupAsync(
        string ownedStoragePath,
        QuoteFileAnalysisStatus status,
        string refreshKey,
        Lazy<Task<QuoteAnalysisPreviewResolution>> owner,
        CancellationToken ct)
    {
        try
        {
            return await RefreshAsync(ownedStoragePath, status, refreshKey, ct);
        }
        finally
        {
            _refreshes.TryRemove(
                new KeyValuePair<string, Lazy<Task<QuoteAnalysisPreviewResolution>>>(refreshKey, owner));
        }
    }

    private async Task<QuoteAnalysisPreviewResolution> RefreshAsync(
        string ownedStoragePath,
        QuoteFileAnalysisStatus initial,
        string refreshKey,
        CancellationToken ct)
    {
        var current = initial;
        var leaseToken = Guid.NewGuid().ToString("N");
        var ownsLease = false;
        try
        {
            if (_redis is not null)
            {
                var distributedRetry = await GetDistributedRetryAfterAsync(current.StoragePath, ct);
                if (distributedRetry is not null)
                {
                    return new QuoteAnalysisPreviewResolution(
                        StripCapabilities(current),
                        "temporarily_unavailable",
                        RetrySeconds(distributedRetry.Value));
                }

                var deadline = _timeProvider.GetUtcNow() + RefreshWait;
                while (!(ownsLease = await _redis.LockTakeAsync(
                    RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshLockKey(current.StoragePath),
                    leaseToken,
                    RefreshLease).WaitAsync(ct)))
                {
                    current = await _statusService.GetStatusAsync(current.StoragePath, ct) ?? current;
                    if (!MatchesOwnedStoragePath(ownedStoragePath, current))
                        return UntrustedIdentity(current);
                    if (!NeedsRefresh(current))
                        return new QuoteAnalysisPreviewResolution(current, "ready");
                    distributedRetry = await GetDistributedRetryAfterAsync(current.StoragePath, ct);
                    if (distributedRetry is not null)
                    {
                        return new QuoteAnalysisPreviewResolution(
                            StripCapabilities(current),
                            "temporarily_unavailable",
                            RetrySeconds(distributedRetry.Value));
                    }
                    if (_timeProvider.GetUtcNow() >= deadline)
                        throw new TimeoutException("Timed out waiting for the analysis preview refresh lease.");
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _timeProvider, ct);
                }

                current = await _statusService.GetStatusAsync(current.StoragePath, ct) ?? current;
                if (!MatchesOwnedStoragePath(ownedStoragePath, current))
                    return UntrustedIdentity(current);
                if (!NeedsRefresh(current))
                    return new QuoteAnalysisPreviewResolution(current, "ready");
            }

            if (!MatchesOwnedStoragePath(ownedStoragePath, current))
                return UntrustedIdentity(current);
            var request = await CreateRefreshAsync(current, ct);
            var refreshed = await _statusService.RefreshPreviewUrlsAsync(
                current.StoragePath,
                request,
                ct) ?? current;
            if (NeedsRefresh(refreshed))
            {
                var latest = await _statusService.GetStatusAsync(current.StoragePath, ct);
                refreshed = latest ?? refreshed;
            }

            if (!MatchesOwnedStoragePath(ownedStoragePath, refreshed))
                return UntrustedIdentity(refreshed);

            if (NeedsRefresh(refreshed))
            {
                current = refreshed;
                throw new InvalidOperationException("The analysis preview changed while its URLs were refreshed.");
            }

            _retryAfter.TryRemove(refreshKey, out _);
            if (_redis is not null)
            {
                try
                {
                    await _redis.KeyDeleteAsync(
                        RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshFailureKey(current.StoragePath));
                }
                catch (Exception ex) when (ex is RedisException or ObjectDisposedException)
                {
                    _logger.LogWarning(ex, "Could not clear preview signing backoff.");
                }
            }
            return new QuoteAnalysisPreviewResolution(refreshed, "ready");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or RedisException or TimeoutException)
        {
            var retryAt = _timeProvider.GetUtcNow() + FailureBackoff;
            SetLocalRetryAfter(refreshKey, retryAt);
            if (_redis is not null)
            {
                try
                {
                    await _redis.StringSetAsync(
                        RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshFailureKey(current.StoragePath),
                        retryAt.ToUnixTimeMilliseconds(),
                        FailureBackoff);
                }
                catch (Exception markerException) when (markerException is RedisException or ObjectDisposedException)
                {
                    _logger.LogWarning(markerException, "Could not persist preview signing backoff.");
                }
            }
            _logger.LogWarning(
                ex,
                "Analysis preview URLs are temporarily unavailable for {StoragePath} at revision {Revision}",
                initial.StoragePath,
                initial.Revision);
            return new QuoteAnalysisPreviewResolution(
                StripCapabilities(current),
                "temporarily_unavailable",
                (int)FailureBackoff.TotalSeconds);
        }
        finally
        {
            if (_redis is not null && ownsLease)
            {
                try
                {
                    await _redis.LockReleaseAsync(
                        RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshLockKey(initial.StoragePath),
                        leaseToken);
                }
                catch (Exception ex) when (
                    ex is RedisException or ObjectDisposedException || ct.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not release the analysis preview refresh lease.");
                }
            }
        }
    }

    private async Task<QuoteAnalysisPreviewRefresh> CreateRefreshAsync(
        QuoteFileAnalysisStatus status,
        CancellationToken ct)
    {
        ValidateCanonicalSources(status);
        var viewerTask = !IsHttpsCapability(status.GlbUrl) && status.ViewerStoragePath is not null
            ? SignAsync(status.ViewerStoragePath, ct)
            : Task.FromResult(status.GlbUrl);
        var thumbnailTask = !IsHttpsCapability(status.ThumbnailUrl) && status.ThumbnailStoragePath is not null
            ? SignAsync(status.ThumbnailStoragePath, ct)
            : Task.FromResult(status.ThumbnailUrl);
        var overlayTasks = status.OverlayGlbUrls.Count == status.AuthoritativeOverlayStoragePaths.Count &&
            status.OverlayGlbUrls.All(IsHttpsCapability)
            ? status.OverlayGlbUrls.Select(Task.FromResult).ToArray()
            : status.AuthoritativeOverlayStoragePaths.Select(path => SignRequiredAsync(path, ct)).ToArray();

        await Task.WhenAll(viewerTask, thumbnailTask).WaitAsync(ct);
        await Task.WhenAll(overlayTasks).WaitAsync(ct);
        return new QuoteAnalysisPreviewRefresh(
            status.Revision,
            status.ViewerStoragePath,
            await viewerTask,
            status.ThumbnailStoragePath,
            await thumbnailTask,
            status.AuthoritativeOverlayStoragePaths,
            [.. overlayTasks.Select(task => task.Result)]);
    }

    private async Task<string?> SignAsync(string path, CancellationToken ct) =>
        await SignRequiredAsync(path, ct);

    private async Task<string> SignRequiredAsync(string path, CancellationToken ct)
    {
        var url = await _uploadClient.GetDownloadUrlByPathAsync(path, expirationMinutes: 60, ct);
        if (!QuoteAnalysisArtifactPathValidator.IsHttpsCapability(url))
            throw new InvalidOperationException("UploadService returned an invalid preview URL.");
        return url;
    }

    private async Task<DateTimeOffset?> GetDistributedRetryAfterAsync(
        string storagePath,
        CancellationToken ct)
    {
        if (_redis is null)
            return null;
        var value = await _redis.StringGetAsync(
            RedisQuoteFileAnalysisStatusService.BuildPreviewRefreshFailureKey(storagePath)).WaitAsync(ct);
        return long.TryParse(value.ToString(), out var unixMilliseconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds)
            : null;
    }

    private void SetLocalRetryAfter(string key, DateTimeOffset retryAt)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var expired in _retryAfter.Where(pair => pair.Value <= now))
            _retryAfter.TryRemove(expired);
        while (_retryAfter.Count >= MaxLocalBackoffEntries)
        {
            var oldest = _retryAfter.MinBy(pair => pair.Value);
            if (!_retryAfter.TryRemove(oldest))
                break;
        }
        _retryAfter[key] = retryAt;
    }

    private int RetrySeconds(DateTimeOffset retryAt) =>
        Math.Max(1, (int)Math.Ceiling((retryAt - _timeProvider.GetUtcNow()).TotalSeconds));

    private static bool MatchesOwnedStoragePath(
        string ownedStoragePath,
        QuoteFileAnalysisStatus status) =>
        string.Equals(ownedStoragePath, status.StoragePath, StringComparison.Ordinal);

    private static QuoteAnalysisPreviewResolution UntrustedIdentity(QuoteFileAnalysisStatus status) =>
        new(StripUntrustedPreview(status), "temporarily_unavailable", (int)FailureBackoff.TotalSeconds);

    internal static void ValidateCanonicalSources(QuoteFileAnalysisStatus status)
    {
        if (!QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(status.StoragePath))
            throw new InvalidOperationException("The upload storage path is invalid.");

        if (status.ViewerStoragePath is not null &&
            !QuoteAnalysisArtifactPathValidator.IsCanonicalViewerPath(
                status.StoragePath,
                status.ViewerStoragePath))
        {
            throw new InvalidOperationException("The viewer storage path is not canonical.");
        }

        if (status.ThumbnailStoragePath is not null &&
            !QuoteAnalysisArtifactPathValidator.IsCanonicalThumbnailPath(
                status.StoragePath,
                status.ThumbnailStoragePath))
        {
            throw new InvalidOperationException("The thumbnail storage path is not canonical.");
        }

        foreach (var path in status.AuthoritativeOverlayStoragePaths)
        {
            if (!QuoteAnalysisArtifactPathValidator.IsCanonicalUploadPath(path) ||
                !QuoteAnalysisArtifactPathValidator.IsCanonicalOverlayPath(status.StoragePath, path))
            {
                throw new InvalidOperationException("An overlay storage path is not canonical.");
            }
        }
    }

    private static bool HasRefreshableSources(QuoteFileAnalysisStatus status) =>
        status.ViewerStoragePath is not null || status.ThumbnailStoragePath is not null ||
        status.AuthoritativeOverlayStoragePaths.Count > 0;

    private static bool NeedsRefresh(QuoteFileAnalysisStatus status) =>
        (status.ViewerStoragePath is not null && !IsHttpsCapability(status.GlbUrl)) ||
        (status.ThumbnailStoragePath is not null && !IsHttpsCapability(status.ThumbnailUrl)) ||
        status.OverlayGlbUrls.Count != status.AuthoritativeOverlayStoragePaths.Count ||
        status.OverlayGlbUrls.Any(url => !IsHttpsCapability(url));

    private static bool IsHttpsCapability(string? url) =>
        QuoteAnalysisArtifactPathValidator.IsHttpsCapability(url);

    private static QuoteFileAnalysisStatus StripCapabilities(QuoteFileAnalysisStatus status) =>
        string.IsNullOrEmpty(status.GlbUrl) &&
        string.IsNullOrEmpty(status.ThumbnailUrl) &&
        status.OverlayGlbUrls.Count == 0
            ? status
            : status with { GlbUrl = null, ThumbnailUrl = null, OverlayGlbUrls = [] };

    private static QuoteFileAnalysisStatus StripUntrustedPreview(QuoteFileAnalysisStatus status) =>
        StripCapabilities(status) with
        {
            ViewerStoragePath = null,
            ViewerFileExtension = null,
            ThumbnailStoragePath = null,
            AuthoritativeOverlayStoragePaths = []
        };

    private static bool IsAnalysisPending(QuoteFileAnalysisStatus status) =>
        string.Equals(status.Status, "Processing", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status.Status, "WaitingForUpload", StringComparison.OrdinalIgnoreCase);
}
