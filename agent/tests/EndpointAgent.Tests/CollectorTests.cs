using EndpointAgent.Collectors;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent.Tests;

[Collection("Telemetry")]
public sealed class CollectorTests : IDisposable
{
    private readonly MetricTestHarness _metrics = new();

    public void Dispose() => _metrics.Dispose();

    [Fact]
    public async Task System_CpuUtilization_IsShareOfCpuTimeSincePreviousSample()
    {
        var sampler = new FakeSystemSampler(
            SystemSampleWith(new CpuTimes(User: 100, System: 50, Idle: 850)),
            SystemSampleWith(new CpuTimes(User: 400, System: 150, Idle: 1450)));
        var collector = new SystemCollector(sampler, _metrics.MeterFactory, firstSampleGap: TimeSpan.Zero);

        await collector.CollectAsync(CancellationToken.None);

        var cpu = _metrics.Read("system.cpu.utilization").ToDictionary(p => p["cpu.mode"]!, p => p.Value);
        Assert.Equal(0.3, cpu["user"], precision: 6);
        Assert.Equal(0.1, cpu["system"], precision: 6);
        Assert.Equal(0.6, cpu["idle"], precision: 6);
    }

    [Fact]
    public async Task System_ReportsMemoryStatesSwapAndDisks()
    {
        var sampler = new FakeSystemSampler(SystemSampleWith(
            new CpuTimes(1, 1, 1),
            new MemoryStats(TotalBytes: 1000, UsedBytes: 600, CachedBytes: 300, FreeBytes: 100, SwapTotalBytes: 400, SwapUsedBytes: 100),
            [new DiskIoCounters("disk0", ReadBytes: 10, WriteBytes: 20, ReadOperations: 1, WriteOperations: 2)]));
        var collector = new SystemCollector(sampler, _metrics.MeterFactory, firstSampleGap: TimeSpan.Zero);

        await collector.CollectAsync(CancellationToken.None);

        var memory = _metrics.Read("system.memory.usage").ToDictionary(p => p["system.memory.state"]!, p => p.Value);
        Assert.Equal(new Dictionary<string, double> { ["used"] = 600, ["cached"] = 300, ["free"] = 100 }, memory);
        Assert.Equal(1000, Assert.Single(_metrics.Read("system.memory.limit")).Value);
        Assert.Equal(0.6, _metrics.Read("system.memory.utilization").Single(p => p["system.memory.state"] == "used").Value, precision: 6);

        var paging = _metrics.Read("system.paging.usage").ToDictionary(p => p["system.paging.state"]!, p => p.Value);
        Assert.Equal(100, paging["used"]);
        Assert.Equal(300, paging["free"]);

        var diskIo = _metrics.Read("system.disk.io");
        Assert.Equal(20, diskIo.Single(p => p["system.device"] == "disk0" && p["disk.io.direction"] == "write").Value);
    }

    [Fact]
    public async Task Process_GroupsByName_AndComputesCpuShareOfMachineCapacity()
    {
        var time = new ManualTimeProvider();
        var sampler = new FakeProcessSampler(time, TimeSpan.FromSeconds(10),
            Snapshot(Proc(1, "chrome", cpuSeconds: 10, memory: 100), Proc(2, "chrome", cpuSeconds: 5, memory: 50), Proc(3, "idle", cpuSeconds: 0, memory: 10)),
            Snapshot(Proc(1, "chrome", cpuSeconds: 14, memory: 120), Proc(2, "chrome", cpuSeconds: 6, memory: 60), Proc(3, "idle", cpuSeconds: 0, memory: 10)));
        // 4 logical CPUs over 10 s = 40 CPU-seconds of capacity; chrome used 4 + 1 = 5.
        var collector = new ProcessCollector(sampler, topCount: 10, logicalCpuCount: 4, _metrics.MeterFactory, time, TimeSpan.Zero);

        await collector.CollectAsync(CancellationToken.None);

        var cpu = _metrics.Read("endpoint.process.cpu.utilization").ToDictionary(p => p["process.executable.name"]!, p => p.Value);
        Assert.Equal(5.0 / 40, cpu["chrome"], precision: 6);
        Assert.Equal(0, cpu["idle"]);
        Assert.Equal(180, _metrics.Read("endpoint.process.memory.usage").Single(p => p["process.executable.name"] == "chrome").Value);
        Assert.Equal(2, _metrics.Read("endpoint.process.count").Single(p => p["process.executable.name"] == "chrome").Value);
        Assert.Equal(3, Assert.Single(_metrics.Read("system.process.count")).Value);
    }

    [Fact]
    public async Task Process_ReusedPid_CountsAllCpuTimeOfTheNewProcess()
    {
        var time = new ManualTimeProvider();
        var sampler = new FakeProcessSampler(time, TimeSpan.FromSeconds(10),
            Snapshot(Proc(7, "old", cpuSeconds: 30, memory: 1, startTime: 1)),
            Snapshot(Proc(7, "new", cpuSeconds: 2, memory: 1, startTime: 2)));
        var collector = new ProcessCollector(sampler, topCount: 10, logicalCpuCount: 1, _metrics.MeterFactory, time, TimeSpan.Zero);

        await collector.CollectAsync(CancellationToken.None);

        var cpu = Assert.Single(_metrics.Read("endpoint.process.cpu.utilization"));
        Assert.Equal("new", cpu["process.executable.name"]);
        Assert.Equal(0.2, cpu.Value, precision: 6);
    }

    [Fact]
    public async Task Process_ReportsTopNByCpuPlusTopNByMemory()
    {
        var time = new ManualTimeProvider();
        var before = Snapshot(Proc(1, "busy", 0, 1), Proc(2, "big", 0, 1000), Proc(3, "small", 0, 1));
        var after = Snapshot(Proc(1, "busy", 5, 1), Proc(2, "big", 0, 1000), Proc(3, "small", 1, 1));
        var collector = new ProcessCollector(new FakeProcessSampler(time, TimeSpan.FromSeconds(10), before, after),
            topCount: 1, logicalCpuCount: 1, _metrics.MeterFactory, time, TimeSpan.Zero);

        await collector.CollectAsync(CancellationToken.None);

        var names = _metrics.Read("endpoint.process.memory.usage").Select(p => p["process.executable.name"]).Order();
        Assert.Equal(["big", "busy"], names);
    }

    [Fact]
    public async Task Network_SkipsInterfacesThatNeverCarriedTraffic()
    {
        var sampler = new FakeNetworkSampler(
            new InterfaceCounters("en0", ReceivedBytes: 500, SentBytes: 200, 5, 2, 0, 1),
            new InterfaceCounters("utun0", 0, 0, 0, 0, 0, 0));
        var collector = new NetworkCollector(sampler, _metrics.MeterFactory);

        await collector.CollectAsync(CancellationToken.None);

        var io = _metrics.Read("system.network.io");
        Assert.All(io, p => Assert.Equal("en0", p["network.interface.name"]));
        Assert.Equal(500, io.Single(p => p["network.io.direction"] == "receive").Value);
        Assert.Equal(200, io.Single(p => p["network.io.direction"] == "transmit").Value);
        Assert.Equal(1, _metrics.Read("system.network.errors").Single(p => p["network.io.direction"] == "transmit").Value);
    }

    private static SystemSample SystemSampleWith(CpuTimes cpu, MemoryStats? memory = null, IReadOnlyList<DiskIoCounters>? disks = null) =>
        new(cpu, 4, memory ?? new MemoryStats(1, 1, 0, 0, 0, 0), disks ?? [], TimeSpan.FromHours(1));

    private static ProcessSample Proc(int pid, string name, double cpuSeconds, long memory, ulong startTime = 1) =>
        new(pid, startTime, name, TimeSpan.FromSeconds(cpuSeconds), memory, 0, 0);

    private static ProcessSnapshot Snapshot(params ProcessSample[] processes) => new(processes, processes.Length);

    private sealed class FakeSystemSampler(params SystemSample[] samples) : ISystemSampler
    {
        private int _next;

        public SystemSample Sample() => samples[Math.Min(_next++, samples.Length - 1)];
    }

    /// <summary>Returns the snapshots in order, advancing the clock by <paramref name="step"/> between them.</summary>
    private sealed class FakeProcessSampler(ManualTimeProvider time, TimeSpan step, params ProcessSnapshot[] snapshots) : IProcessSampler
    {
        private int _next;

        public ProcessSnapshot Sample()
        {
            if (_next > 0)
            {
                time.Advance(step);
            }

            return snapshots[Math.Min(_next++, snapshots.Length - 1)];
        }
    }

    private sealed class FakeNetworkSampler(params InterfaceCounters[] interfaces) : INetworkSampler
    {
        public IReadOnlyList<InterfaceCounters> Sample() => interfaces;
    }
}
