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
}
