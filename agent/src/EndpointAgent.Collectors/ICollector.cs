namespace EndpointAgent.Collectors;

/// <summary>
/// One unit of data collection, run once per collection cycle.
/// </summary>
/// <remarks>
/// Collectors publish metrics through observable instruments that read a snapshot refreshed
/// by <see cref="CollectAsync"/>, and report discrete events as log records. A collector that
/// throws fails only its own step of the cycle; the remaining collectors still run.
/// </remarks>
public interface ICollector
{
    /// <summary>Short, stable name used in span names and metric attributes, e.g. "heartbeat".</summary>
    string Name { get; }

    Task CollectAsync(CancellationToken cancellationToken);
}
