using System.Runtime.InteropServices;
using System.Text;

namespace EndpointAgent.MacOS.Interop;

/// <summary>P/Invoke declarations for libSystem: sysctl, Mach host statistics and libproc.</summary>
internal static unsafe partial class Libc
{
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    internal const int HostCpuLoadInfo = 3;          // HOST_CPU_LOAD_INFO
    internal const int HostVmInfo64 = 4;             // HOST_VM_INFO64
    internal const int RusageInfoV2 = 2;             // RUSAGE_INFO_V2
    internal const int ProcPidPathInfoMaxSize = 4096; // PROC_PIDPATHINFO_MAXSIZE

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int sysctlbyname(string name, void* oldp, ref nuint oldlenp, void* newp, nuint newlen);

    [LibraryImport(LibSystem, SetLastError = true)]
    internal static partial int sysctl(int* name, uint namelen, void* oldp, ref nuint oldlenp, void* newp, nuint newlen);

    [LibraryImport(LibSystem)]
    internal static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    internal static partial int host_statistics(uint host, int flavor, void* info, ref uint count);

    [LibraryImport(LibSystem)]
    internal static partial int host_statistics64(uint host, int flavor, void* info, ref uint count);

    [LibraryImport(LibSystem)]
    internal static partial int mach_timebase_info(out MachTimebaseInfo info);

    [LibraryImport(LibSystem, SetLastError = true)]
    internal static partial int proc_listallpids(int* buffer, int buffersize);

    [LibraryImport(LibSystem, SetLastError = true)]
    internal static partial int proc_pid_rusage(int pid, int flavor, RusageInfoV2Struct* buffer);

    [LibraryImport(LibSystem, SetLastError = true)]
    internal static partial int proc_pidpath(int pid, byte* buffer, uint buffersize);

    internal static string? SysctlString(string name)
    {
        nuint length = 0;
        if (sysctlbyname(name, null, ref length, null, 0) != 0 || length == 0)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* p = buffer)
        {
            if (sysctlbyname(name, p, ref length, null, 0) != 0)
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, (int)length).TrimEnd('\0');
    }

    /// <summary>Reads an integer sysctl, whether the kernel stores it as 32 or 64 bits.</summary>
    internal static long? SysctlInt64(string name)
    {
        long value = 0;
        nuint length = sizeof(long);
        if (sysctlbyname(name, &value, ref length, null, 0) != 0)
        {
            return null;
        }

        return length == sizeof(int) ? (int)value : value;
    }

    internal static T? SysctlStruct<T>(string name)
        where T : unmanaged
    {
        T value;
        nuint length = (nuint)sizeof(T);
        return sysctlbyname(name, &value, ref length, null, 0) == 0 ? value : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MachTimebaseInfo
    {
        public uint Numer;
        public uint Denom;
    }

    /// <summary>struct timeval, as returned by kern.boottime.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeVal
    {
        public long Seconds;
        public int Microseconds;
    }

    /// <summary>struct xsw_usage, as returned by vm.swapusage.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct XswUsage
    {
        public ulong Total;
        public ulong Available;
        public ulong Used;
        public uint PageSize;
        public int Encrypted;
    }

    /// <summary>host_cpu_load_info: cumulative ticks per CPU state, summed over all CPUs.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct HostCpuLoadInfoStruct
    {
        public uint User;
        public uint System;
        public uint Idle;
        public uint Nice;
    }

    /// <summary>struct vm_statistics64 (mach/vm_statistics.h). Counts are in pages.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct VmStatistics64
    {
        public uint FreeCount;
        public uint ActiveCount;
        public uint InactiveCount;
        public uint WireCount;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong Pageins;
        public ulong Pageouts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeableCount;
        public uint SpeculativeCount;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong Swapins;
        public ulong Swapouts;
        public uint CompressorPageCount;
        public uint ThrottledCount;
        public uint ExternalPageCount;
        public uint InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    /// <summary>struct rusage_info_v2 (sys/resource.h). Times are in Mach absolute time units.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RusageInfoV2Struct
    {
        public fixed byte Uuid[16];
        public ulong UserTime;
        public ulong SystemTime;
        public ulong PkgIdleWakeups;
        public ulong InterruptWakeups;
        public ulong Pageins;
        public ulong WiredSize;
        public ulong ResidentSize;
        public ulong PhysFootprint;
        public ulong ProcStartAbstime;
        public ulong ProcExitAbstime;
        public ulong ChildUserTime;
        public ulong ChildSystemTime;
        public ulong ChildPkgIdleWakeups;
        public ulong ChildInterruptWakeups;
        public ulong ChildPageins;
        public ulong ChildElapsedAbstime;
        public ulong DiskioBytesRead;
        public ulong DiskioBytesWritten;
    }
}
