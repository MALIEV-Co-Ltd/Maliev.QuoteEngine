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
    /// Unversioned notifications remain compatible only until a versioned baseline is observed.
    /// </summary>
    public bool ShouldApply(
        string storagePath,
        AnalysisNotificationStream stream,
        Guid? eventId,
        long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);

        lock (_gate)
        {
            var streamCursors = GetOrCreateStream(stream);
            if (revision <= 0)
                return !streamCursors.ContainsKey(storagePath);

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

    /// <summary>
    /// Seeds both analysis streams from an authoritative hydrated status snapshot.
    /// </summary>
    public void Seed(string storagePath, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storagePath);
        if (revision <= 0)
            return;

        lock (_gate)
        {
            foreach (var stream in Enum.GetValues<AnalysisNotificationStream>())
            {
                var cursors = GetOrCreateStream(stream);
                if (!cursors.TryGetValue(storagePath, out var current) || revision > current.Revision)
                    cursors[storagePath] = new NotificationCursor(revision, null);
            }
        }
    }

    private Dictionary<string, NotificationCursor> GetOrCreateStream(AnalysisNotificationStream stream)
    {
        if (_cursors.TryGetValue(stream, out var streamCursors))
            return streamCursors;

        streamCursors = new Dictionary<string, NotificationCursor>(StringComparer.OrdinalIgnoreCase);
        _cursors.Add(stream, streamCursors);
        return streamCursors;
    }

    private sealed record NotificationCursor(long Revision, Guid? EventId);
}
