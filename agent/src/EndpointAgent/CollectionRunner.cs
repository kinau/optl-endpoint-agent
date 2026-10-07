using System.Diagnostics;
using System.Diagnostics.Metrics;
using EndpointAgent.Collectors;
using Microsoft.Extensions.Options;

namespace EndpointAgent;

/// <summary>
/// Runs every registered collector once per cycle. Each cycle is one trace: a root
/// "collection.cycle" span with a "collect {name}" child span per collector.
/// </summary>
public sealed partial class CollectionRunner
{
    private static readonly IReadOnlyList<double> DurationBuckets =
        [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30];

    private readonly IReadOnlyList<ICollector> _collectors;
    private readonly AgentOptions _options;
    private readonly ILogger<CollectionRunner> _logger;
    private readonly Counter<long> _cycles;
    private readonly Histogram<double> _collectorDuration;
    private long _cycle;

    public CollectionRunner(
        IEnumerable<ICollector> collectors,
        IOptions<AgentOptions> options,
        IMeterFactory meterFactory,
        ILogger<CollectionRunner> logger)
    {
        _options = options.Value;
        _collectors = collectors
            .Where(c => !_options.DisabledCollectors.Contains(c.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        _logger = logger;

        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        _cycles = meter.CreateCounter<long>(
            "endpoint.agent.collection.cycles",
            unit: "{cycle}",
            description: "Completed collection cycles, by outcome.");
        _collectorDuration = meter.CreateHistogram(
            "endpoint.agent.collector.duration",
            unit: "s",
            description: "Time each collector took, by collector and outcome.",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = DurationBuckets });
    }

    public IReadOnlyList<ICollector> Collectors => _collectors;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        var cycle = Interlocked.Increment(ref _cycle);
        using var activity = AgentDiagnostics.ActivitySource.StartActivity("collection.cycle");
        activity?.SetTag("endpoint.agent.cycle", cycle);

        var started = Stopwatch.GetTimestamp();
        var failed = 0;
        foreach (var collector in _collectors)
        {
            if (!await RunCollectorAsync(collector, cancellationToken))
            {
                failed++;
            }
        }

        var outcome = failed == 0 ? "ok" : "error";
        _cycles.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
        if (failed > 0)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"{failed} collector(s) failed");
        }

        LogCycleCompleted(cycle, _collectors.Count, failed, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<bool> RunCollectorAsync(ICollector collector, CancellationToken cancellationToken)
    {
        using var activity = AgentDiagnostics.ActivitySource.StartActivity($"collect {collector.Name}");
        activity?.SetTag("collector.name", collector.Name);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CollectorTimeout);

        var started = Stopwatch.GetTimestamp();
        var ok = false;
        try
        {
            await collector.CollectAsync(timeout.Token);
            ok = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "timeout");
            LogCollectorTimedOut(collector.Name, _options.CollectorTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.AddException(ex);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            LogCollectorFailed(ex, collector.Name);
        }
        finally
        {
            _collectorDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("collector.name", collector.Name),
                new KeyValuePair<string, object?>("outcome", ok ? "ok" : "error"));
        }

        return ok;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Collection cycle {Cycle} finished: {CollectorCount} collectors, {FailedCount} failed, {DurationMs:F1} ms")]
    private partial void LogCycleCompleted(long cycle, int collectorCount, int failedCount, double durationMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collector {Collector} timed out after {TimeoutSeconds} s")]
    private partial void LogCollectorTimedOut(string collector, double timeoutSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collector {Collector} failed")]
    private partial void LogCollectorFailed(Exception exception, string collector);
}
