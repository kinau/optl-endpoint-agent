using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EndpointAgent.Windows.Interop;

/// <summary>P/Invoke declarations for kernel32 and ntdll.</summary>
internal static unsafe partial class Win32
{
    private const string Kernel32 = "kernel32.dll";
    private const string NtDll = "ntdll.dll";

    internal const int SystemProcessInformation = 5;
    internal const int SystemPageFileInformation = 18;
    internal const uint StatusInfoLengthMismatch = 0xC0000004;
    internal const uint IoctlDiskPerformance = 0x00070020; // CTL_CODE(IOCTL_DISK_BASE, 0x0008, METHOD_BUFFERED, FILE_ANY_ACCESS)
    internal const int RelationProcessorCore = 0;

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalMemoryStatusEx(MemoryStatusEx* buffer);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool K32GetPerformanceInfo(PerformanceInformation* info, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLogicalProcessorInformation(SystemLogicalProcessorInformation* buffer, ref uint length);

    // Desired access 0 (query device attributes only) works without administrator rights.
    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, void* inBuffer, uint inBufferSize,
        void* outBuffer, uint outBufferSize, out uint bytesReturned, nint overlapped);

    [LibraryImport(NtDll)]
    internal static partial uint NtQuerySystemInformation(int systemInformationClass, void* systemInformation,
        uint systemInformationLength, out uint returnLength);

    /// <summary>Parses a block returned by NtQuerySystemInformation while it is still in memory.</summary>
    /// <param name="data">The filled part of the block.</param>
    /// <param name="baseAddress">The block's address, for resolving pointers stored inside it.</param>
    internal delegate T SystemInformationParser<T>(ReadOnlySpan<byte> data, nint baseAddress);

    /// <summary>
    /// Calls NtQuerySystemInformation with an unmanaged buffer that grows until the result fits,
    /// then parses it before the buffer is freed. The buffer must not move during parsing because
    /// some results (such as process image names) contain pointers into it.
    /// </summary>
    internal static T QuerySystemInformation<T>(int informationClass, ref int bufferSize, SystemInformationParser<T> parse)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = NativeMemory.Alloc((nuint)bufferSize);
            try
            {
                var status = NtQuerySystemInformation(informationClass, buffer, (uint)bufferSize, out var needed);
                if (status == 0)
                {
                    var length = needed == 0 || needed > bufferSize ? bufferSize : (int)needed;
                    return parse(new ReadOnlySpan<byte>(buffer, length), (nint)buffer);
                }

                if (status != StatusInfoLengthMismatch)
                {
                    throw new InvalidOperationException($"NtQuerySystemInformation({informationClass}) failed: 0x{status:X8}.");
                }

                // The data can grow between calls (new processes), so leave headroom.
                bufferSize = Math.Max(bufferSize * 2, (int)needed + 64 * 1024);
            }
            finally
            {
                NativeMemory.Free(buffer);
            }
        }

        throw new InvalidOperationException($"NtQuerySystemInformation({informationClass}) kept growing.");
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    /// <summary>PERFORMANCE_INFORMATION (psapi.h). Memory values are in pages.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        public uint Size;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    /// <summary>DISK_PERFORMANCE (ntdddisk.h). Byte counts are cumulative since boot.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiskPerformance
    {
        public long BytesRead;
        public long BytesWritten;
        public long ReadTime;
        public long WriteTime;
        public long IdleTime;
        public uint ReadCount;
        public uint WriteCount;
        public uint QueueDepth;
        public uint SplitCount;
        public long QueryTime;
        public uint StorageDeviceNumber;
        public fixed char StorageManagerName[8];
    }

    /// <summary>SYSTEM_LOGICAL_PROCESSOR_INFORMATION (winnt.h); the 16-byte union is not read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemLogicalProcessorInformation
    {
        public nuint ProcessorMask;
        public int Relationship;
        public fixed byte Union[16];
    }
}
