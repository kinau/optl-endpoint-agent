using System.Diagnostics.Metrics;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent.Collectors;

/// <summary>
/// Network traffic per interface: bytes, packets and errors as cumulative counters
/// (system.network.* semantic conventions). Interfaces that have never carried traffic are skipped.
/// </summary>
public sealed class NetworkCollector : ICollector
{
    private readonly INetworkSampler _sampler;
    private IReadOnlyList<InterfaceCounters> _interfaces = [];

    public NetworkCollector(INetworkSampler sampler, IMeterFactory meterFactory)
    {
        _sampler = sampler;

        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        meter.CreateObservableCounter("system.network.io",
            () => Observe(i => i.ReceivedBytes, i => i.SentBytes), unit: "By",
            description: "Bytes received and transmitted per network interface.");
        meter.CreateObservableCounter("system.network.packets",
            () => Observe(i => i.ReceivedPackets, i => i.SentPackets), unit: "{packet}",
            description: "Packets received and transmitted per network interface.");
        meter.CreateObservableCounter("system.network.errors",
            () => Observe(i => i.ReceiveErrors, i => i.SendErrors), unit: "{error}",
            description: "Receive and transmit errors per network interface.");
    }

    public string Name => "network";

    public Task CollectAsync(CancellationToken cancellationToken)
    {
        _interfaces = _sampler.Sample().Where(i => i.ReceivedBytes + i.SentBytes > 0).ToList();
        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<long>> Observe(Func<InterfaceCounters, long> received, Func<InterfaceCounters, long> sent) =>
        _interfaces.SelectMany(i => new[]
        {
            new Measurement<long>(received(i),
                new KeyValuePair<string, object?>("network.interface.name", i.Name),
                new KeyValuePair<string, object?>("network.io.direction", "receive")),
            new Measurement<long>(sent(i),
                new KeyValuePair<string, object?>("network.interface.name", i.Name),
                new KeyValuePair<string, object?>("network.io.direction", "transmit")),
        });
}
