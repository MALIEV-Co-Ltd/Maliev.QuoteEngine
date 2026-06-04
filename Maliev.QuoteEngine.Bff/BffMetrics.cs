using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Maliev.QuoteEngine.Bff;

/// <summary>
/// Provides business metrics collection for the QuoteEngine BFF.
/// </summary>
public sealed class BffMetrics
{
    private readonly Counter<long> _browserDfmRuntimeCompletions;
    private readonly Counter<long> _browserDfmRuntimeStarts;
    private readonly Counter<long> _browserDfmRuntimeTerminalAttempts;

    /// <summary>
    /// Initializes a new instance of the <see cref="BffMetrics"/> class.
    /// </summary>
    /// <param name="meterFactory">The meter factory used to create counters.</param>
    public BffMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create("quote-engine");
        _browserDfmRuntimeCompletions = meter.CreateCounter<long>(
            "quote_browser_dfm_runtime_completions",
            unit: "{completion}",
            description: "Counts browser-first local DFM runtime completions observed by QuoteEngine.");
        _browserDfmRuntimeStarts = meter.CreateCounter<long>(
            "quote_browser_dfm_runtime_starts",
            unit: "{start}",
            description: "Counts browser-first local DFM runtime starts observed by QuoteEngine.");
        _browserDfmRuntimeTerminalAttempts = meter.CreateCounter<long>(
            "quote_browser_dfm_runtime_terminal_attempts",
            unit: "{attempt}",
            description: "Counts browser-first local DFM runtime attempts that ended before producing a local report.");
    }

    /// <summary>
    /// Records a browser-first local DFM runtime completion.
    /// </summary>
    /// <param name="processCode">The process code analyzed by the browser runtime.</param>
    /// <param name="accepted">Whether the Blazor client accepted the local result for the active part.</param>
    /// <param name="authority">The runtime authority marker.</param>
    /// <param name="executionMode">The runtime execution mode marker.</param>
    public void RecordBrowserDfmRuntimeCompletion(
        string? processCode,
        bool accepted,
        string? authority,
        string? executionMode)
    {
        _browserDfmRuntimeCompletions.Add(1, new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "accepted", accepted },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        });
    }

    /// <summary>
    /// Records that a browser-first local DFM runtime started.
    /// </summary>
    /// <param name="processCode">The process code requested for the browser runtime.</param>
    /// <param name="authority">The runtime authority marker.</param>
    /// <param name="executionMode">The runtime execution mode marker.</param>
    public void RecordBrowserDfmRuntimeStart(
        string? processCode,
        string? authority,
        string? executionMode)
    {
        _browserDfmRuntimeStarts.Add(1, new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        });
    }

    /// <summary>
    /// Records a browser-first local DFM runtime attempt that ended before producing a report.
    /// </summary>
    /// <param name="processCode">The process code requested for the browser runtime.</param>
    /// <param name="reason">The low-cardinality terminal reason reported by the viewer.</param>
    /// <param name="authority">The runtime authority marker.</param>
    /// <param name="executionMode">The runtime execution mode marker.</param>
    public void RecordBrowserDfmRuntimeTerminalAttempt(
        string? processCode,
        string? reason,
        string? authority,
        string? executionMode)
    {
        _browserDfmRuntimeTerminalAttempts.Add(1, new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "reason", NormalizeMarker(reason, "local_runtime_unavailable") },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        });
    }

    private static string NormalizeProcessFamily(string? processCode)
    {
        var normalized = NormalizeMarker(processCode, "unknown")
            .Replace("-", "_", StringComparison.Ordinal)
            .Replace(" ", "_", StringComparison.Ordinal)
            .ToUpperInvariant();

        return normalized switch
        {
            "CNC" or "CNC_MILL" or "CNC_MILLING" or "CNC_TURN" or "CNC_TURNING" => "cnc",
            "SLA" or "DLP" or "SLA_DLP" => "sla",
            "FDM" or "FFF" => "fdm",
            "SLS" or "MJF" or "MJ" or "BJ" or "DMLS" => "powder_bed",
            _ => "other"
        };
    }

    private static string NormalizeMarker(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var normalized = value.Trim();
        return normalized.Length <= 40 ? normalized : fallback;
    }
}
