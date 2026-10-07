using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.MacOS.Interop;

namespace EndpointAgent.MacOS;

/// <summary>Machine-wide counters from Mach host statistics, sysctl and IOKit.</summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacSystemSampler : ISystemSampler
{
    private readonly uint _host = Libc.mach_host_self();
    private readonly long _pageSize = Libc.SysctlInt64("hw.pagesize") ?? Environment.SystemPageSize;
    private readonly long _totalMemory = Libc.SysctlInt64("hw.memsize") ?? 0;
    private readonly int _logicalCpus = (int)(Libc.SysctlInt64("hw.logicalcpu") ?? Environment.ProcessorCount);
    private readonly DateTimeOffset _bootTime = MacInventorySource.ReadBootTime();

    public SystemSample Sample() => new(
        ReadCpu(),
        _logicalCpus,
        ReadMemory(),
        IOKit.ReadDiskStatistics(),
        DateTimeOffset.UtcNow - _bootTime);

    private CpuTimes ReadCpu()
    {
        Libc.HostCpuLoadInfoStruct load;
        var count = (uint)(sizeof(Libc.HostCpuLoadInfoStruct) / sizeof(int));
        if (Libc.host_statistics(_host, Libc.HostCpuLoadInfo, &load, ref count) != 0)
        {
            throw new InvalidOperationException("host_statistics(HOST_CPU_LOAD_INFO) failed.");
        }

        // Nice time is user-mode work at lower priority; Activity Monitor counts it as user.
        return new CpuTimes(load.User + (double)load.Nice, load.System, load.Idle);
    }

    private MemoryStats ReadMemory()
    {
        Libc.VmStatistics64 vm;
        var count = (uint)(sizeof(Libc.VmStatistics64) / sizeof(int));
        if (Libc.host_statistics64(_host, Libc.HostVmInfo64, &vm, ref count) != 0)
        {
            throw new InvalidOperationException("host_statistics64(HOST_VM_INFO64) failed.");
        }

        // Same breakdown as Activity Monitor: Memory Used = App Memory + Wired + Compressed,
        // Cached Files = file-backed + purgeable pages.
        long Bytes(ulong pages) => (long)pages * _pageSize;
        var app = Bytes(vm.InternalPageCount) - Bytes(vm.PurgeableCount);
        var used = app + Bytes(vm.WireCount) + Bytes(vm.CompressorPageCount);
        var cached = Bytes(vm.ExternalPageCount) + Bytes(vm.PurgeableCount);
        var free = Math.Max(0, _totalMemory - used - cached);

        var swap = Libc.SysctlStruct<Libc.XswUsage>("vm.swapusage");
        return new MemoryStats(_totalMemory, used, cached, free, (long)(swap?.Total ?? 0), (long)(swap?.Used ?? 0));
    }
}
