using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Maliev.QuoteEngine.Bff;

/// <summary>
/// Provides business metrics collection for the QuoteEngine BFF.
/// </summary>
public sealed class BffMetrics
{
    private readonly Counter<long> _browserDfmRuntimeCompletions;
    private readonly Histogram<long> _browserDfmRuntimeInputBytes;
    private readonly Histogram<long> _browserDfmRuntimeInputTriangles;
    private readonly Counter<long> _browserDfmRuntimeStarts;
    private readonly Counter<long> _browserDfmRuntimeTerminalAttempts;
    private readonly Counter<long> _dfmExecutionDecisions;
    private readonly Histogram<long> _dfmServerAvoidedInputBytes;
    private readonly Histogram<long> _dfmServerAvoidedInputTriangles;
    private readonly Counter<long> _previewGenerations;
    private readonly Counter<long> _previewBuildOutcomes;
    private readonly Counter<long> _previewFeedback;

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
        _browserDfmRuntimeInputBytes = meter.CreateHistogram<long>(
            "quote_browser_dfm_runtime_input_bytes",
            unit: "By",
            description: "Records browser-first local DFM runtime input payload sizes observed by QuoteEngine.");
        _browserDfmRuntimeInputTriangles = meter.CreateHistogram<long>(
            "quote_browser_dfm_runtime_input_triangles",
            unit: "{triangle}",
            description: "Records browser-first local DFM runtime triangle workloads observed by QuoteEngine.");
        _browserDfmRuntimeTerminalAttempts = meter.CreateCounter<long>(
            "quote_browser_dfm_runtime_terminal_attempts",
            unit: "{attempt}",
            description: "Counts browser-first local DFM runtime attempts that ended before producing a local report.");
        _dfmExecutionDecisions = meter.CreateCounter<long>(
            "quote_dfm_execution_decisions",
            unit: "{decision}",
            description: "Counts whether QuoteEngine DFM work was satisfied by browser-local execution or required GeometryService fallback.");
        _dfmServerAvoidedInputBytes = meter.CreateHistogram<long>(
            "quote_dfm_server_avoided_input_bytes",
            unit: "By",
            description: "Records accepted browser-local DFM input bytes that avoided GeometryService server processing.");
        _dfmServerAvoidedInputTriangles = meter.CreateHistogram<long>(
            "quote_dfm_server_avoided_input_triangles",
            unit: "{triangle}",
            description: "Records accepted browser-local DFM triangle workloads that avoided GeometryService server processing.");
        _previewGenerations = meter.CreateCounter<long>(
            "quote_agent_preview_generations",
            unit: "{generation}",
            description: "Counts agent 3D preview generation outcomes (generated, validation_rejected, fallback_used).");
        _previewBuildOutcomes = meter.CreateCounter<long>(
            "quote_agent_preview_build_outcomes",
            unit: "{build}",
            description: "Counts client-side 3D preview build outcomes (success, build_failed) by error class.");
        _previewFeedback = meter.CreateCounter<long>(
            "quote_agent_preview_feedback",
            unit: "{feedback}",
            description: "Counts customer thumbs feedback recorded for generated 3D previews.");
    }

    /// <summary>
    /// Records the outcome of an agent 3D preview generation attempt.
    /// </summary>
    /// <param name="outcome">The generation outcome: generated, validation_rejected, or fallback_used.</param>
    public void RecordPreviewGeneration(string? outcome)
    {
        _previewGenerations.Add(1, new TagList
        {
            { "outcome", NormalizePreviewGenerationOutcome(outcome) },
        });
    }

    /// <summary>
    /// Records the outcome of a client-side 3D preview build reported by the inline viewer.
    /// </summary>
    /// <param name="success">Whether the browser worker built the preview mesh.</param>
    /// <param name="errorClass">The low-cardinality failure class when <paramref name="success"/> is false.</param>
    public void RecordPreviewBuildOutcome(bool success, string? errorClass)
    {
        _previewBuildOutcomes.Add(1, new TagList
        {
            { "outcome", success ? "success" : "build_failed" },
            { "error_class", success ? "none" : NormalizePreviewErrorClass(errorClass) },
        });
    }

    /// <summary>
    /// Records customer thumbs feedback recorded for a generated 3D preview.
    /// </summary>
    /// <param name="sentiment">The feedback sentiment: up or down.</param>
    public void RecordPreviewFeedback(string? sentiment)
    {
        _previewFeedback.Add(1, new TagList
        {
            { "sentiment", NormalizePreviewSentiment(sentiment) },
        });
    }

    private static string NormalizePreviewGenerationOutcome(string? outcome)
    {
        return NormalizeMarker(outcome, "other").ToLowerInvariant() switch
        {
            "generated" => "generated",
            "validation_rejected" => "validation_rejected",
            "fallback_used" => "fallback_used",
            _ => "other"
        };
    }

    private static string NormalizePreviewErrorClass(string? errorClass)
    {
        return NormalizeMarker(errorClass, "other").ToLowerInvariant() switch
        {
            "worker_unavailable" => "worker_unavailable",
            "build_timeout" => "build_timeout",
            "invalid_geometry" => "invalid_geometry",
            "empty_mesh" => "empty_mesh",
            "parse_error" => "parse_error",
            _ => "other"
        };
    }

    private static string NormalizePreviewSentiment(string? sentiment)
    {
        return NormalizeMarker(sentiment, "other").ToLowerInvariant() switch
        {
            "up" => "up",
            "down" => "down",
            _ => "other"
        };
    }

    /// <summary>
    /// Records a browser-first local DFM runtime completion.
    /// </summary>
    /// <param name="processCode">The process code analyzed by the browser runtime.</param>
    /// <param name="accepted">Whether the Blazor client accepted the local result for the active part.</param>
    /// <param name="authority">The runtime authority marker.</param>
    /// <param name="executionMode">The runtime execution mode marker.</param>
    /// <param name="inputByteCount">The browser-local input size that did not require server processing.</param>
    /// <param name="inputTriangleCount">The browser-local triangle workload that did not require server processing.</param>
    public void RecordBrowserDfmRuntimeCompletion(
        string? processCode,
        bool accepted,
        string? authority,
        string? executionMode,
        long? inputByteCount = null,
        long? inputTriangleCount = null)
    {
        _browserDfmRuntimeCompletions.Add(1, new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "accepted", accepted },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        });

        if (accepted)
        {
            _dfmExecutionDecisions.Add(1, new TagList
            {
                { "process_family", NormalizeProcessFamily(processCode) },
                { "execution_path", "browser_primary" },
                { "decision", "satisfied" },
                { "server_cpu", "avoided" },
                { "authority", NormalizeMarker(authority, "other") },
                { "execution_mode", NormalizeMarker(executionMode, "other") },
            });
            RecordAvoidedServerWorkload(processCode, authority, executionMode, inputByteCount, inputTriangleCount);
        }
    }

    /// <summary>
    /// Records that a browser-first local DFM runtime started.
    /// </summary>
    /// <param name="processCode">The process code requested for the browser runtime.</param>
    /// <param name="authority">The runtime authority marker.</param>
    /// <param name="executionMode">The runtime execution mode marker.</param>
    /// <param name="inputByteCount">The approximate local runtime input size in bytes.</param>
    /// <param name="inputTriangleCount">The approximate local runtime triangle workload.</param>
    public void RecordBrowserDfmRuntimeStart(
        string? processCode,
        string? authority,
        string? executionMode,
        long? inputByteCount = null,
        long? inputTriangleCount = null)
    {
        var tags = new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        };

        _browserDfmRuntimeStarts.Add(1, tags);
        if (inputByteCount is > 0)
        {
            _browserDfmRuntimeInputBytes.Record(inputByteCount.Value, tags);
        }

        if (inputTriangleCount is > 0)
        {
            _browserDfmRuntimeInputTriangles.Record(inputTriangleCount.Value, tags);
        }
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

    /// <summary>
    /// Records that a server DFM analysis result was received from GeometryService.
    /// </summary>
    /// <param name="processCode">The manufacturing process represented by the DFM report.</param>
    public void RecordServerDfmAnalysisReady(string? processCode)
    {
        _dfmExecutionDecisions.Add(1, new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "execution_path", "server_fallback" },
            { "decision", "server_completed" },
            { "server_cpu", "consumed" },
        });
    }

    private void RecordAvoidedServerWorkload(
        string? processCode,
        string? authority,
        string? executionMode,
        long? inputByteCount,
        long? inputTriangleCount)
    {
        var tags = new TagList
        {
            { "process_family", NormalizeProcessFamily(processCode) },
            { "execution_path", "browser_primary" },
            { "server_cpu", "avoided" },
            { "authority", NormalizeMarker(authority, "other") },
            { "execution_mode", NormalizeMarker(executionMode, "other") },
        };

        if (inputByteCount is > 0)
        {
            _dfmServerAvoidedInputBytes.Record(inputByteCount.Value, tags);
        }

        if (inputTriangleCount is > 0)
        {
            _dfmServerAvoidedInputTriangles.Record(inputTriangleCount.Value, tags);
        }
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
