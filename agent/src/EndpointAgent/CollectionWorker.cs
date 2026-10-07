using Microsoft.Extensions.Options;

namespace EndpointAgent;

/// <summary>Runs a collection cycle at startup and then once per <see cref="AgentOptions.CollectionInterval"/>.</summary>
public sealed partial class CollectionWorker(
    CollectionRunner runner,
    IOptions<AgentOptions> options,
    ILogger<CollectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.CollectionInterval;
        LogStarted(runner.Collectors.Count, string.Join(", ", runner.Collectors.Select(c => c.Name)), interval);

        using var timer = new PeriodicTimer(interval);
        try
        {
            do
            {
                await runner.RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint agent started with {CollectorCount} collectors ({Collectors}), collecting every {Interval}")]
    private partial void LogStarted(int collectorCount, string collectors, TimeSpan interval);
}
