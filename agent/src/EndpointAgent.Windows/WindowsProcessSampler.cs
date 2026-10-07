using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.Windows.Interop;

namespace EndpointAgent.Windows;

/// <summary>
/// Per-process counters for every process from one NtQuerySystemInformation(SystemProcessInformation)
/// call. No process handles are opened, so this sees all processes even without administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessSampler : IProcessSampler
{
    // SYSTEM_PROCESS_INFORMATION field offsets on 64-bit Windows (x64 and ARM64).
    private const int NextEntryOffset = 0;
    private const int WorkingSetPrivateSizeOffset = 8;
    private const int CreateTimeOffset = 32;
    private const int UserTimeOffset = 40;
    private const int KernelTimeOffset = 48;
    private const int ImageNameLengthOffset = 56;
    private const int ImageNameBufferOffset = 64;
    private const int UniqueProcessIdOffset = 80;
    private const int ReadTransferCountOffset = 232;
    private const int WriteTransferCountOffset = 240;
    private const int MinEntrySize = 256;

    private int _bufferSize = 512 * 1024;

    public ProcessSnapshot Sample()
    {
        if (!Environment.Is64BitProcess)
        {
            throw new PlatformNotSupportedException("The Windows process sampler requires a 64-bit process.");
        }

        return Win32.QuerySystemInformation(Win32.SystemProcessInformation, ref _bufferSize, Parse);
    }

    internal static ProcessSnapshot Parse(ReadOnlySpan<byte> data, nint baseAddress)
    {
        var processes = new List<ProcessSample>();
        for (var offset = 0; offset + MinEntrySize <= data.Length;)
        {
            var entry = data[offset..];
            var pid = (int)BinaryPrimitives.ReadInt64LittleEndian(entry[UniqueProcessIdOffset..]);

            // PID 0 is the System Idle Process: its "CPU time" is idle time, not work.
            if (pid != 0)
            {
                processes.Add(new ProcessSample(
                    pid,
                    (ulong)BinaryPrimitives.ReadInt64LittleEndian(entry[CreateTimeOffset..]),
                    ReadImageName(data, entry, baseAddress, pid),
                    TimeSpan.FromTicks(BinaryPrimitives.ReadInt64LittleEndian(entry[UserTimeOffset..])
                                       + BinaryPrimitives.ReadInt64LittleEndian(entry[KernelTimeOffset..])),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[WorkingSetPrivateSizeOffset..]),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[ReadTransferCountOffset..]),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[WriteTransferCountOffset..])));
            }

            var next = BinaryPrimitives.ReadUInt32LittleEndian(entry[NextEntryOffset..]);
            if (next == 0)
            {
                break;
            }
            offset += (int)next;
        }

        return new ProcessSnapshot(processes, processes.Count);
    }

    /// <summary>
    /// ImageName is a UNICODE_STRING whose buffer points into the same returned block, so it is read
    /// at its offset from the block's start, bounds-checked against the block.
    /// </summary>
    internal static string ReadImageName(ReadOnlySpan<byte> data, ReadOnlySpan<byte> entry, nint baseAddress, int pid)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(entry[ImageNameLengthOffset..]);
        var pointer = (nint)BinaryPrimitives.ReadInt64LittleEndian(entry[ImageNameBufferOffset..]);
        var offset = (long)pointer - baseAddress;
        if (length == 0 || pointer == 0 || offset < 0 || offset + length > data.Length)
        {
            return pid == 4 ? "System" : $"pid-{pid}";
        }

        return new string(MemoryMarshal.Cast<byte, char>(data.Slice((int)offset, length)));
    }
}
