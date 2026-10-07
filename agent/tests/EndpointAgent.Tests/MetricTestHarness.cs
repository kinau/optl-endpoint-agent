using System.Diagnostics.Metrics;
using EndpointAgent.Collectors;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace EndpointAgent.Tests;

/// <summary>Collects the agent's metrics in memory and reads them back as (value, tags) points.</summary>
internal sealed class MetricTestHarness : IDisposable
{
    private readonly List<Metric> _metrics = [];
    private readonly ServiceProvider _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
    private readonly MeterProvider _provider;

    public MetricTestHarness()
    {
        _provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(AgentDiagnostics.SourceName)
            .AddInMemoryExporter(_metrics)
            .Build();
    }

    public IMeterFactory MeterFactory => _services.GetRequiredService<IMeterFactory>();

    /// <summary>Exports now and returns the points of the named metric.</summary>
    public List<Point> Read(string name)
    {
        _metrics.Clear();
        _provider.ForceFlush();
        var metric = _metrics.LastOrDefault(m => m.Name == name);
        var points = new List<Point>();
        if (metric is null)
        {
            return points;
        }

        foreach (ref readonly var p in metric.GetMetricPoints())
        {
            var tags = new Dictionary<string, string?>();
            foreach (var tag in p.Tags)
            {
                tags[tag.Key] = tag.Value?.ToString();
            }

            double value = metric.MetricType switch
            {
                MetricType.LongSum or MetricType.LongSumNonMonotonic => p.GetSumLong(),
                MetricType.DoubleSum or MetricType.DoubleSumNonMonotonic => p.GetSumDouble(),
                MetricType.LongGauge => p.GetGaugeLastValueLong(),
                MetricType.DoubleGauge => p.GetGaugeLastValueDouble(),
                _ => throw new NotSupportedException(metric.MetricType.ToString()),
            };
            points.Add(new Point(value, tags));
        }

        return points;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _services.Dispose();
    }

    internal sealed record Point(double Value, Dictionary<string, string?> Tags)
    {
        public string? this[string key] => Tags.GetValueOrDefault(key);
    }
}

/// <summary>A clock tests move forward by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
