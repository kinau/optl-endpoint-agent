using System.Diagnostics.Metrics;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent.Collectors;

/// <summary>
/// Machine performance: CPU utilization, memory, swap, disk I/O and uptime, named after the
/// OpenTelemetry system.* semantic conventions.
/// </summary>
/// <remarks>
/// CPU utilization is the average over the time since the previous cycle. The first cycle takes two
/// samples one second apart so that a value is available right after startup.
/// </remarks>
public sealed class SystemCollector : ICollector
{
    private readonly ISystemSampler _sampler;
    private readonly TimeSpan _firstSampleGap;
    private State? _state;

    public SystemCollector(ISystemSampler sampler, IMeterFactory meterFactory)
        : this(sampler, meterFactory, TimeSpan.FromSeconds(1))
    {
    }

    internal SystemCollector(ISystemSampler sampler, IMeterFactory meterFactory, TimeSpan firstSampleGap)
    {
        _sampler = sampler;
        _firstSampleGap = firstSampleGap;

        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        meter.CreateObservableGauge("system.cpu.utilization", ObserveCpuUtilization, unit: "1",
            description: "Share of CPU time spent in each mode since the previous cycle, across all logical CPUs.");
        meter.CreateObservableUpDownCounter("system.cpu.logical.count",
            () => Observe<long>(s => [new Measurement<long>(s.Sample.LogicalCpuCount)]), unit: "{cpu}",
            description: "Number of logical CPUs.");
        meter.CreateObservableUpDownCounter("system.memory.usage", ObserveMemoryUsage, unit: "By",
            description: "Physical memory by state (used, cached, free).");
        meter.CreateObservableUpDownCounter("system.memory.limit",
            () => Observe<long>(s => [new Measurement<long>(s.Sample.Memory.TotalBytes)]), unit: "By",
            description: "Total physical memory.");
        meter.CreateObservableGauge("system.memory.utilization", ObserveMemoryUtilization, unit: "1",
            description: "Share of physical memory by state.");
        meter.CreateObservableUpDownCounter("system.paging.usage", ObservePagingUsage, unit: "By",
            description: "Swap space by state (used, free).");
        meter.CreateObservableCounter("system.disk.io", ObserveDiskBytes, unit: "By",
            description: "Bytes read from and written to each physical disk.");
        meter.CreateObservableCounter("system.disk.operations", ObserveDiskOperations, unit: "{operation}",
            description: "Read and write operations on each physical disk.");
        meter.CreateObservableGauge("system.uptime",
            () => Observe<double>(s => [new Measurement<double>(s.Sample.Uptime.TotalSeconds)]), unit: "s",
            description: "Time since the machine booted.");
    }

    public string Name => "system";

    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        var previous = _state?.Sample;
        if (previous is null)
        {
            previous = _sampler.Sample();
            await Task.Delay(_firstSampleGap, cancellationToken);
        }

        var current = _sampler.Sample();
        _state = new State(current, CpuUtilization(previous.Cpu, current.Cpu));
    }

    private static CpuTimes? CpuUtilization(CpuTimes previous, CpuTimes current)
    {
        var total = current.Total - previous.Total;
        if (total <= 0)
        {
            return null;
        }

        return new CpuTimes(
            Math.Max(0, current.User - previous.User) / total,
            Math.Max(0, current.System - previous.System) / total,
            Math.Max(0, current.Idle - previous.Idle) / total);
    }

    private IEnumerable<Measurement<T>> Observe<T>(Func<State, IEnumerable<Measurement<T>>> read)
        where T : struct => _state is { } state ? read(state) : [];

    private IEnumerable<Measurement<double>> ObserveCpuUtilization() => Observe<double>(s => s.Cpu is { } cpu
        ?
        [
            new Measurement<double>(cpu.User, Tag("cpu.mode", "user")),
            new Measurement<double>(cpu.System, Tag("cpu.mode", "system")),
            new Measurement<double>(cpu.Idle, Tag("cpu.mode", "idle")),
        ]
        : []);

    private IEnumerable<Measurement<long>> ObserveMemoryUsage() => Observe<long>(s =>
    {
        var m = s.Sample.Memory;
        return
        [
            new Measurement<long>(m.UsedBytes, Tag("system.memory.state", "used")),
            new Measurement<long>(m.CachedBytes, Tag("system.memory.state", "cached")),
            new Measurement<long>(m.FreeBytes, Tag("system.memory.state", "free")),
        ];
    });

    private IEnumerable<Measurement<double>> ObserveMemoryUtilization() => Observe<double>(s =>
    {
        var m = s.Sample.Memory;
        if (m.TotalBytes <= 0)
        {
            return [];
        }

        double total = m.TotalBytes;
        return
        [
            new Measurement<double>(m.UsedBytes / total, Tag("system.memory.state", "used")),
            new Measurement<double>(m.CachedBytes / total, Tag("system.memory.state", "cached")),
            new Measurement<double>(m.FreeBytes / total, Tag("system.memory.state", "free")),
        ];
    });

    private IEnumerable<Measurement<long>> ObservePagingUsage() => Observe<long>(s =>
    {
        var m = s.Sample.Memory;
        return
        [
            new Measurement<long>(m.SwapUsedBytes, Tag("system.paging.state", "used")),
            new Measurement<long>(Math.Max(0, m.SwapTotalBytes - m.SwapUsedBytes), Tag("system.paging.state", "free")),
        ];
    });

    private IEnumerable<Measurement<long>> ObserveDiskBytes() => Observe(s => s.Sample.Disks.SelectMany(d => new[]
    {
        new Measurement<long>(d.ReadBytes, Tag("system.device", d.Device), Tag("disk.io.direction", "read")),
        new Measurement<long>(d.WriteBytes, Tag("system.device", d.Device), Tag("disk.io.direction", "write")),
    }));

    private IEnumerable<Measurement<long>> ObserveDiskOperations() => Observe(s => s.Sample.Disks.SelectMany(d => new[]
    {
        new Measurement<long>(d.ReadOperations, Tag("system.device", d.Device), Tag("disk.io.direction", "read")),
        new Measurement<long>(d.WriteOperations, Tag("system.device", d.Device), Tag("disk.io.direction", "write")),
    }));

    private static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);

    /// <summary>Latest sample plus CPU utilization ratios; replaced atomically each cycle.</summary>
    private sealed record State(SystemSample Sample, CpuTimes? Cpu);
}
