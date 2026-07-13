using Maliev.QuoteEngine.Client.Services;

namespace Maliev.QuoteEngine.Tests;

public sealed class AnalysisNotificationRevisionTrackerTests
{
    private const string StoragePath = "quotes/temp/session/upload/part.step";

    [Fact]
    public void ShouldApply_LegacyNotificationWithoutRevision_RemainsCompatible()
    {
        var tracker = new AnalysisNotificationRevisionTracker();

        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 0));
        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), -1));
    }

    [Fact]
    public void ShouldApply_RevisedNotifications_AcceptsOnlyStrictlyNewerRevision()
    {
        var tracker = new AnalysisNotificationRevisionTracker();
        var eventId = Guid.NewGuid();

        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, eventId, 4));
        Assert.False(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, eventId, 4));
        Assert.False(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 3));
        Assert.False(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 4));
        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 5));
    }

    [Fact]
    public void ShouldApply_TracksStoragePathsIndependentlyIgnoringPathCase()
    {
        var tracker = new AnalysisNotificationRevisionTracker();

        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 2));
        Assert.False(tracker.ShouldApply(StoragePath.ToUpperInvariant(), AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 2));
        Assert.True(tracker.ShouldApply("quotes/temp/session/upload/other.step", AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 1));
    }

    [Fact]
    public void ShouldApply_TracksGeometryAndDfmStreamsIndependently()
    {
        var tracker = new AnalysisNotificationRevisionTracker();

        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 7));
        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.DfmAnalysisReady, Guid.NewGuid(), 1));
        Assert.False(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.GlbReady, Guid.NewGuid(), 6));
        Assert.True(tracker.ShouldApply(StoragePath, AnalysisNotificationStream.DfmAnalysisReady, Guid.NewGuid(), 2));
    }
}
