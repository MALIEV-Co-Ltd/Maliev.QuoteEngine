namespace Maliev.QuoteEngine.Client.Services;

/// <summary>
/// Identifies an independently ordered analysis notification stream.
/// </summary>
public enum AnalysisNotificationStream
{
    /// <summary>The converted-viewer completion stream.</summary>
    GlbReady,

    /// <summary>The merged DFM-analysis stream.</summary>
    DfmAnalysisReady
}

/// <summary>
/// Prevents stale or duplicate versioned SignalR notifications from replacing newer client state.
/// </summary>
public sealed class AnalysisNotificationRevisionTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<AnalysisNotificationStream, Dictionary<string, NotificationCursor>> _cursors = [];

    /// <summary>
    /// Returns whether the notification should update client state.
    /// Unversioned notifications remain compatible and are always accepted.
    /// </summary>
    public bool ShouldApply(
        string storagePath,
        AnalysisNotificationStream stream,
        Guid? eventId,
        long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        if (revision <= 0)
        {
            return true;
        }

        lock (_gate)
        {
            if (!_cursors.TryGetValue(stream, out var streamCursors))
            {
                streamCursors = new Dictionary<string, NotificationCursor>(StringComparer.OrdinalIgnoreCase);
                _cursors.Add(stream, streamCursors);
            }

            if (streamCursors.TryGetValue(storagePath, out var current))
            {
                if (revision == current.Revision && eventId == current.EventId)
                {
                    return false;
                }

                if (revision <= current.Revision)
                {
                    return false;
                }
            }

            streamCursors[storagePath] = new NotificationCursor(revision, eventId);
            return true;
        }
    }

    private sealed record NotificationCursor(long Revision, Guid? EventId);
}
