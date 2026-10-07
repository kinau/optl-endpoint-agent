using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace EndpointAgent.Collectors;

/// <summary>
/// Cross-platform liveness signal: the agent's uptime as a metric, plus a log record each cycle.
/// </summary>
public sealed partial class HeartbeatCollector : ICollector
{
    private readonly ILogger<HeartbeatCollector> _logger;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();
    private long _beats;

    public HeartbeatCollector(IMeterFactory meterFactory, ILogger<HeartbeatCollector> logger)
    {
        _logger = logger;

        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        meter.CreateObservableGauge(
            "endpoint.agent.uptime",
            () => Uptime.TotalSeconds,
            unit: "s",
            description: "Time since the agent started.");
    }

    public string Name => "heartbeat";

    private TimeSpan Uptime => Stopwatch.GetElapsedTime(_startTimestamp);

    public Task CollectAsync(CancellationToken cancellationToken)
    {
        LogHeartbeat(Interlocked.Increment(ref _beats), Uptime.TotalSeconds);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Heartbeat {Heartbeat}: agent up for {UptimeSeconds:F0} s")]
    private partial void LogHeartbeat(long heartbeat, double uptimeSeconds);
}
