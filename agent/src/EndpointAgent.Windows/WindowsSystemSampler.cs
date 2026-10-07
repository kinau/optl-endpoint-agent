using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.Windows.Interop;

namespace EndpointAgent.Windows;

/// <summary>Machine-wide counters from GetSystemTimes, memory status, page files and disk IOCTLs.</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsSystemSampler : ISystemSampler
{
    // Physical drive numbers are not always contiguous (removed disks leave gaps), so probe a range.
    private const int MaxPhysicalDrives = 32;

    // SYSTEM_PAGEFILE_INFORMATION: NextEntryOffset, TotalSize, TotalInUse (sizes in pages).
    private const int PageFileNextOffset = 0;
    private const int PageFileTotalOffset = 4;
    private const int PageFileInUseOffset = 8;

    public SystemSample Sample() => new(
        ReadCpu(),
        Environment.ProcessorCount,
        ReadMemory(),
        ReadDisks(),
        TimeSpan.FromMilliseconds(Environment.TickCount64));

    private static CpuTimes ReadCpu()
    {
        if (!Win32.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            throw new InvalidOperationException($"GetSystemTimes failed: {Marshal.GetLastPInvokeError()}.");
        }

        // Kernel time includes idle time.
        return new CpuTimes(user, kernel - idle, idle);
    }

    private static MemoryStats ReadMemory()
    {
        var status = new Win32.MemoryStatusEx { Length = (uint)sizeof(Win32.MemoryStatusEx) };
        if (!Win32.GlobalMemoryStatusEx(&status))
        {
            throw new InvalidOperationException($"GlobalMemoryStatusEx failed: {Marshal.GetLastPInvokeError()}.");
        }

        var performance = new Win32.PerformanceInformation { Size = (uint)sizeof(Win32.PerformanceInformation) };
        Win32.K32GetPerformanceInfo(&performance, performance.Size);
        var pageSize = (long)Math.Max(performance.PageSize, 4096u);

        // Available = standby (cache) + free. SystemCache is the standby list plus the system
        // working set, so it is capped at Available to keep the states non-overlapping.
        var total = (long)status.TotalPhys;
        var available = (long)status.AvailPhys;
        var cached = Math.Min((long)performance.SystemCache * pageSize, available);
        var (swapTotal, swapUsed) = ReadPageFiles(pageSize);
        return new MemoryStats(total, total - available, cached, available - cached, swapTotal, swapUsed);
    }

    private static (long Total, long Used) ReadPageFiles(long pageSize)
    {
        var bufferSize = 1024;
        try
        {
            return Win32.QuerySystemInformation(Win32.SystemPageFileInformation, ref bufferSize,
                (data, _) => SumPageFiles(data, pageSize));
        }
        catch (InvalidOperationException)
        {
            return (0, 0);
        }
    }

    internal static (long Total, long Used) SumPageFiles(ReadOnlySpan<byte> data, long pageSize)
    {
        long total = 0, used = 0;
        for (var offset = 0; offset + 12 <= data.Length;)
        {
            var entry = data[offset..];
            total += BinaryPrimitives.ReadUInt32LittleEndian(entry[PageFileTotalOffset..]) * pageSize;
            used += BinaryPrimitives.ReadUInt32LittleEndian(entry[PageFileInUseOffset..]) * pageSize;

            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry[PageFileNextOffset..]);
            if (next == 0)
            {
                break;
            }
            offset += (int)next;
        }

        return (total, used);
    }

    private static IReadOnlyList<DiskIoCounters> ReadDisks()
    {
        var disks = new List<DiskIoCounters>();
        for (var number = 0; number < MaxPhysicalDrives; number++)
        {
            using var device = Win32.CreateFile($@"\\.\PhysicalDrive{number}", 0, 3 /* FILE_SHARE_READ | FILE_SHARE_WRITE */,
                0, 3 /* OPEN_EXISTING */, 0, 0);
            if (device.IsInvalid)
            {
                continue;
            }

            Win32.DiskPerformance performance;
            if (Win32.DeviceIoControl(device, Win32.IoctlDiskPerformance, null, 0, &performance,
                    (uint)sizeof(Win32.DiskPerformance), out _, 0))
            {
                disks.Add(new DiskIoCounters($"PhysicalDrive{number}", performance.BytesRead, performance.BytesWritten,
                    performance.ReadCount, performance.WriteCount));
            }
        }

        return disks;
    }
}
