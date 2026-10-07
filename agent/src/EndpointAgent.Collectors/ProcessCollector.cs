using System.Diagnostics.Metrics;
using EndpointAgent.Collectors.Sampling;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Collectors;

/// <summary>
/// Application performance: processes grouped by executable name, reporting the top N by CPU and
/// the top N by memory. Values are averages over the time since the previous cycle.
/// </summary>
/// <remarks>
/// Grouping by name matches how users think about apps (all "Google Chrome Helper" processes are
/// one app) and keeps PIDs out of metric labels.
/// </remarks>
public sealed class ProcessCollector : ICollector
{
    private readonly IProcessSampler _sampler;
    private readonly int _topCount;
    private readonly int _logicalCpuCount;
    private readonly TimeProvider _time;
    private readonly TimeSpan _firstSampleGap;
    private Dictionary<(int Pid, ulong StartTime), ProcessSample>? _previous;
    private long _previousTimestamp;
    private State? _state;

    public ProcessCollector(IProcessSampler sampler, IOptions<ProcessCollectorOptions> options, IMeterFactory meterFactory)
        : this(sampler, options.Value.TopCount, Environment.ProcessorCount, meterFactory, TimeProvider.System, TimeSpan.FromSeconds(1))
    {
    }

    internal ProcessCollector(
        IProcessSampler sampler, int topCount, int logicalCpuCount, IMeterFactory meterFactory, TimeProvider time, TimeSpan firstSampleGap)
    {
        _sampler = sampler;
        _topCount = topCount;
        _logicalCpuCount = logicalCpuCount;
        _time = time;
        _firstSampleGap = firstSampleGap;

        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        meter.CreateObservableGauge("endpoint.process.cpu.utilization",
            () => ObserveTop(a => a.CpuUtilization), unit: "1",
            description: "Share of total machine CPU capacity used by the application since the previous cycle.");
        meter.CreateObservableUpDownCounter("endpoint.process.memory.usage",
            () => ObserveTop(a => a.MemoryBytes), unit: "By",
            description: "Memory used by all processes of the application.");
        meter.CreateObservableGauge("endpoint.process.disk.throughput", ObserveDiskThroughput, unit: "By/s",
            description: "Disk bytes read and written per second by the application since the previous cycle.");
        meter.CreateObservableUpDownCounter("endpoint.process.count",
            () => ObserveTop(a => (long)a.Instances), unit: "{process}",
            description: "Running processes of the application.");
        meter.CreateObservableUpDownCounter<long>("system.process.count",
            () => _state is { } s ? [new Measurement<long>(s.TotalCount)] : [], unit: "{process}",
            description: "All processes on the machine.");
    }

    public string Name => "process";

    public async Task CollectAsync(CancellationToken cancellationToken)
    {
        if (_previous is null)
        {
            Remember(_sampler.Sample());
            await Task.Delay(_firstSampleGap, _time, cancellationToken);
        }

        var snapshot = _sampler.Sample();
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_previousTimestamp, now).TotalSeconds;

        var apps = Aggregate(snapshot.Processes, _previous!, elapsed);
        _state = new State(SelectTop(apps), snapshot.TotalCount);
        Remember(snapshot, now);
    }

    private void Remember(ProcessSnapshot snapshot, long? timestamp = null)
    {
        _previous = snapshot.Processes.ToDictionary(p => (p.Pid, p.StartTime));
        _previousTimestamp = timestamp ?? _time.GetTimestamp();
    }

    private List<AppUsage> Aggregate(
        IReadOnlyList<ProcessSample> processes,
        Dictionary<(int Pid, ulong StartTime), ProcessSample> previous,
        double elapsedSeconds)
    {
        var capacitySeconds = elapsedSeconds * _logicalCpuCount;
        var apps = new Dictionary<string, AppUsage>(StringComparer.Ordinal);

        foreach (var p in processes)
        {
            // A process absent from the previous sample started during the interval, so all of its
            // counters accrued within it.
            previous.TryGetValue((p.Pid, p.StartTime), out var before);
            var cpu = (p.CpuTime - (before?.CpuTime ?? TimeSpan.Zero)).TotalSeconds;
            var read = p.DiskReadBytes - (before?.DiskReadBytes ?? 0);
            var write = p.DiskWriteBytes - (before?.DiskWriteBytes ?? 0);

            var app = apps.GetValueOrDefault(p.Name) ?? new AppUsage(p.Name);
            apps[p.Name] = app with
            {
                Instances = app.Instances + 1,
                CpuSeconds = app.CpuSeconds + Math.Max(0, cpu),
                MemoryBytes = app.MemoryBytes + p.MemoryBytes,
                DiskReadBytes = app.DiskReadBytes + Math.Max(0, read),
                DiskWriteBytes = app.DiskWriteBytes + Math.Max(0, write),
            };
        }

        return apps.Values
            .Select(a => a with
            {
                CpuUtilization = capacitySeconds > 0 ? Math.Min(1, a.CpuSeconds / capacitySeconds) : 0,
                DiskReadRate = elapsedSeconds > 0 ? a.DiskReadBytes / elapsedSeconds : 0,
                DiskWriteRate = elapsedSeconds > 0 ? a.DiskWriteBytes / elapsedSeconds : 0,
            })
            .ToList();
    }

    private List<AppUsage> SelectTop(List<AppUsage> apps) => apps
        .OrderByDescending(a => a.CpuSeconds).Take(_topCount)
        .UnionBy(apps.OrderByDescending(a => a.MemoryBytes).Take(_topCount), a => a.Name)
        .ToList();

    private IEnumerable<Measurement<T>> ObserveTop<T>(Func<AppUsage, T> value)
        where T : struct => _state is { } s
        ? s.Top.Select(a => new Measurement<T>(value(a), new KeyValuePair<string, object?>("process.executable.name", a.Name)))
        : [];

    private IEnumerable<Measurement<double>> ObserveDiskThroughput() => _state is { } s
        ? s.Top.SelectMany(a => new[]
        {
            new Measurement<double>(a.DiskReadRate,
                new KeyValuePair<string, object?>("process.executable.name", a.Name),
                new KeyValuePair<string, object?>("disk.io.direction", "read")),
            new Measurement<double>(a.DiskWriteRate,
                new KeyValuePair<string, object?>("process.executable.name", a.Name),
                new KeyValuePair<string, object?>("disk.io.direction", "write")),
        })
        : [];

    internal sealed record AppUsage(string Name)
    {
        public int Instances { get; init; }
        public double CpuSeconds { get; init; }
        public long MemoryBytes { get; init; }
        public long DiskReadBytes { get; init; }
        public long DiskWriteBytes { get; init; }
        public double CpuUtilization { get; init; }
        public double DiskReadRate { get; init; }
        public double DiskWriteRate { get; init; }
    }

    private sealed record State(IReadOnlyList<AppUsage> Top, int TotalCount);
}
