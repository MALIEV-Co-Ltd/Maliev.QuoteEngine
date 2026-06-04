using System.Diagnostics.Metrics;
using Maliev.QuoteEngine.Bff;
using Microsoft.Extensions.DependencyInjection;

namespace Maliev.QuoteEngine.Tests;

public sealed class BffMetricsTests
{
    [Fact]
    public void RecordBrowserDfmRuntimeStart_EmitsStartMetricTags()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        var measurements = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == "quote_browser_dfm_runtime_starts")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                snapshot[tag.Key] = tag.Value;
            }

            measurements.Add(snapshot);
        });
        listener.Start();

        using var provider = services.BuildServiceProvider();
        var metrics = new BffMetrics(provider.GetRequiredService<IMeterFactory>());
        metrics.RecordBrowserDfmRuntimeStart("CNC_MILL", "local_primary", "primary_interactive");

        var tags = Assert.Single(measurements);
        Assert.Equal("cnc", tags["process_family"]);
        Assert.Equal("local_primary", tags["authority"]);
        Assert.Equal("primary_interactive", tags["execution_mode"]);
    }

    [Fact]
    public void RecordBrowserDfmRuntimeStart_RecordsLocalInputWorkload()
    {
        var services = new ServiceCollection();
        services.AddMetrics();

        var measurements = new List<(string InstrumentName, long Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name is "quote_browser_dfm_runtime_input_bytes"
                    or "quote_browser_dfm_runtime_input_triangles")
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                snapshot[tag.Key] = tag.Value;
            }

            measurements.Add((instrument.Name, value, snapshot));
        });
        listener.Start();

        using var provider = services.BuildServiceProvider();
        var metrics = new BffMetrics(provider.GetRequiredService<IMeterFactory>());
        metrics.RecordBrowserDfmRuntimeStart(
            "CNC_MILL",
            "local_primary",
            "primary_interactive",
            84,
            1);

        var byteMeasurement = Assert.Single(
            measurements,
            item => item.InstrumentName == "quote_browser_dfm_runtime_input_bytes");
        Assert.Equal(84, byteMeasurement.Value);
        Assert.Equal("cnc", byteMeasurement.Tags["process_family"]);
        Assert.Equal("local_primary", byteMeasurement.Tags["authority"]);
        Assert.Equal("primary_interactive", byteMeasurement.Tags["execution_mode"]);

        var triangleMeasurement = Assert.Single(
            measurements,
            item => item.InstrumentName == "quote_browser_dfm_runtime_input_triangles");
        Assert.Equal(1, triangleMeasurement.Value);
        Assert.Equal("cnc", triangleMeasurement.Tags["process_family"]);
        Assert.Equal("local_primary", triangleMeasurement.Tags["authority"]);
        Assert.Equal("primary_interactive", triangleMeasurement.Tags["execution_mode"]);
    }
}
