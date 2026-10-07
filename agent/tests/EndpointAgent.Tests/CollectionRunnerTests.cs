using System.Diagnostics;
using System.Diagnostics.Metrics;
using EndpointAgent.Collectors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace EndpointAgent.Tests;

// The agent's ActivitySource and meter name are process-wide, so every test class that listens to
// them joins the "Telemetry" collection, which xUnit runs sequentially.
[Collection("Telemetry")]
public sealed class CollectionRunnerTests : IDisposable
{
    private readonly List<Activity> _spans = [];
    private readonly List<Metric> _metrics = [];
    private readonly ServiceProvider _services;
    private readonly TracerProvider _tracerProvider;
    private readonly MeterProvider _meterProvider;

    public CollectionRunnerTests()
    {
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        _tracerProvider = Sdk.CreateTracerProviderBuilder()
            .AddSource(AgentDiagnostics.SourceName)
            .AddInMemoryExporter(_spans)
            .Build();
        _meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(AgentDiagnostics.SourceName)
            .AddInMemoryExporter(_metrics)
            .Build();
    }

    public void Dispose()
    {
        _tracerProvider.Dispose();
        _meterProvider.Dispose();
        _services.Dispose();
    }

    [Fact]
    public async Task Cycle_IsOneTrace_WithAChildSpanPerCollector()
    {
        var runner = CreateRunner(new FakeCollector("a"), new FakeCollector("b"));

        await runner.RunCycleAsync(CancellationToken.None);
        _tracerProvider.ForceFlush();

        var root = Assert.Single(_spans, s => s.DisplayName == "collection.cycle");
        Assert.Equal(default, root.ParentSpanId);
        Assert.Equal(1L, root.GetTagItem("endpoint.agent.cycle"));
        Assert.Equal(ActivityStatusCode.Unset, root.Status);

        var children = _spans.Where(s => s.ParentSpanId == root.SpanId).Select(s => s.DisplayName).ToList();
        Assert.Equal(["collect a", "collect b"], children);
    }

    [Fact]
    public async Task FailingCollector_MarksSpansAsError_AndOtherCollectorsStillRun()
    {
        var after = new FakeCollector("after");
        var runner = CreateRunner(new FakeCollector("broken", _ => throw new InvalidOperationException("boom")), after);

        await runner.RunCycleAsync(CancellationToken.None);
        _tracerProvider.ForceFlush();

        Assert.Equal(1, after.Calls);
        var broken = Assert.Single(_spans, s => s.DisplayName == "collect broken");
        Assert.Equal(ActivityStatusCode.Error, broken.Status);
        Assert.Contains(broken.Events, e => e.Name == "exception");
        Assert.Equal(ActivityStatusCode.Error, Assert.Single(_spans, s => s.DisplayName == "collection.cycle").Status);
    }

    [Fact]
    public async Task SlowCollector_IsCancelledAfterTimeout()
    {
        var runner = CreateRunner(
            [new FakeCollector("slow", ct => Task.Delay(Timeout.Infinite, ct))],
            collectorTimeout: TimeSpan.FromMilliseconds(50));

        await runner.RunCycleAsync(CancellationToken.None);
        _tracerProvider.ForceFlush();

        var slow = Assert.Single(_spans, s => s.DisplayName == "collect slow");
        Assert.Equal(ActivityStatusCode.Error, slow.Status);
        Assert.Equal("timeout", slow.StatusDescription);
    }

    [Fact]
    public async Task Cycle_RecordsCycleCountAndCollectorDuration_ByOutcome()
    {
        var runner = CreateRunner(new FakeCollector("good"), new FakeCollector("bad", _ => throw new Exception()));

        await runner.RunCycleAsync(CancellationToken.None);
        await runner.RunCycleAsync(CancellationToken.None);
        _meterProvider.ForceFlush();

        var cycles = Points(_metrics.Last(m => m.Name == "endpoint.agent.collection.cycles"));
        Assert.Equal(2, Assert.Single(cycles, p => Tag(p, "outcome") == "error").GetSumLong());

        var durations = Points(_metrics.Last(m => m.Name == "endpoint.agent.collector.duration"));
        Assert.Equal(2, Assert.Single(durations, p => Tag(p, "collector.name") == "good" && Tag(p, "outcome") == "ok").GetHistogramCount());
        Assert.Equal(2, Assert.Single(durations, p => Tag(p, "collector.name") == "bad" && Tag(p, "outcome") == "error").GetHistogramCount());
    }

    [Fact]
    public async Task Heartbeat_ReportsUptimeGauge()
    {
        var heartbeat = new HeartbeatCollector(_services.GetRequiredService<IMeterFactory>(), NullLogger<HeartbeatCollector>.Instance);

        await CreateRunner(heartbeat).RunCycleAsync(CancellationToken.None);
        _meterProvider.ForceFlush();

        var uptime = Assert.Single(Points(_metrics.Last(m => m.Name == "endpoint.agent.uptime")));
        Assert.True(uptime.GetGaugeLastValueDouble() >= 0);
        Assert.Equal("s", _metrics.Last(m => m.Name == "endpoint.agent.uptime").Unit);
    }

    private CollectionRunner CreateRunner(params ICollector[] collectors) => CreateRunner(collectors, TimeSpan.FromSeconds(30));

    private CollectionRunner CreateRunner(ICollector[] collectors, TimeSpan collectorTimeout) => new(
        collectors,
        Options.Create(new AgentOptions { CollectorTimeout = collectorTimeout }),
        _services.GetRequiredService<IMeterFactory>(),
        NullLogger<CollectionRunner>.Instance);

    private static List<MetricPoint> Points(Metric metric)
    {
        var points = new List<MetricPoint>();
        foreach (ref readonly var point in metric.GetMetricPoints())
        {
            points.Add(point);
        }
        return points;
    }

    private static string? Tag(MetricPoint point, string key)
    {
        foreach (var tag in point.Tags)
        {
            if (tag.Key == key)
            {
                return tag.Value?.ToString();
            }
        }
        return null;
    }

    private sealed class FakeCollector(string name, Func<CancellationToken, Task>? collect = null) : ICollector
    {
        public string Name => name;

        public int Calls { get; private set; }

        public Task CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return collect?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
