namespace EndpointAgent.Collectors.Sampling;

// Platform projects implement these interfaces to read raw OS counters. The shared collectors
// turn the samples into metrics (deltas, utilization, top-N), so each platform only gathers data.

/// <summary>Reads machine-wide CPU, memory, disk I/O and uptime counters.</summary>
public interface ISystemSampler
{
    SystemSample Sample();
}

/// <summary>Reads per-process counters for every process the agent can access.</summary>
public interface IProcessSampler
{
    ProcessSnapshot Sample();
}

/// <summary>Reads cumulative traffic counters for network interfaces that are up (loopback excluded).</summary>
public interface INetworkSampler
{
    IReadOnlyList<InterfaceCounters> Sample();
}

/// <summary>Reads static hardware and OS facts.</summary>
public interface IMachineInventorySource
{
    MachineInventory Read();
}

/// <summary>
/// Cumulative CPU time since boot, summed over all logical CPUs. Any unit works as long as it is
/// the same in every sample, because only ratios of deltas are reported.
/// </summary>
public readonly record struct CpuTimes(double User, double System, double Idle)
{
    public double Total => User + System + Idle;
}

/// <summary>
/// Physical memory split into non-overlapping states that add up to <see cref="TotalBytes"/>.
/// Used is memory applications and the OS hold; cached is reclaimable file cache.
/// </summary>
public sealed record MemoryStats(
    long TotalBytes,
    long UsedBytes,
    long CachedBytes,
    long FreeBytes,
    long SwapTotalBytes,
    long SwapUsedBytes);

/// <summary>Cumulative I/O counters for one physical disk.</summary>
public sealed record DiskIoCounters(string Device, long ReadBytes, long WriteBytes, long ReadOperations, long WriteOperations);

public sealed record SystemSample(
    CpuTimes Cpu,
    int LogicalCpuCount,
    MemoryStats Memory,
    IReadOnlyList<DiskIoCounters> Disks,
    TimeSpan Uptime);

/// <param name="StartTime">Opaque process start time, used with <paramref name="Pid"/> to detect PID reuse.</param>
/// <param name="CpuTime">Cumulative user + system CPU time.</param>
/// <param name="MemoryBytes">Memory attributed to the process (macOS: physical footprint; Windows: private working set).</param>
public sealed record ProcessSample(
    int Pid,
    ulong StartTime,
    string Name,
    TimeSpan CpuTime,
    long MemoryBytes,
    long DiskReadBytes,
    long DiskWriteBytes);

/// <param name="Processes">Processes the agent could read.</param>
/// <param name="TotalCount">All processes on the machine, including ones the agent lacks access to.</param>
public sealed record ProcessSnapshot(IReadOnlyList<ProcessSample> Processes, int TotalCount);

/// <summary>Cumulative traffic counters for one network interface.</summary>
public sealed record InterfaceCounters(
    string Name,
    long ReceivedBytes,
    long SentBytes,
    long ReceivedPackets,
    long SentPackets,
    long ReceiveErrors,
    long SendErrors);

public sealed record MachineInventory(
    string Manufacturer,
    string Model,
    string CpuModel,
    string CpuVendor,
    int PhysicalCores,
    int LogicalCores,
    long MemoryBytes,
    string OsName,
    string OsVersion,
    string OsBuild,
    DateTimeOffset BootTime);
