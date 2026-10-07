using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.MacOS.Interop;

namespace EndpointAgent.MacOS;

/// <summary>
/// Interface counters from the per-interface MIB sysctl (net.link.generic.ifdata), which returns
/// full 64-bit counters to unprivileged processes. .NET's NetworkInterface statistics and the
/// NET_RT_IFLIST2 routing sysctl both return byte counts truncated to 32 bits on macOS.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacNetworkSampler : INetworkSampler
{
    private const int CtlNet = 4;           // CTL_NET
    private const int PfLink = 18;          // PF_LINK
    private const int NetlinkGeneric = 0;   // NETLINK_GENERIC
    private const int IfmibSystem = 1;      // IFMIB_SYSTEM
    private const int IfmibIfCount = 1;     // IFMIB_IFCOUNT
    private const int IfmibIfData = 2;      // IFMIB_IFDATA
    private const int IfDataGeneral = 1;    // IFDATA_GENERAL
    private const int IffUp = 0x1;
    private const int IffLoopback = 0x8;

    // Offsets within struct ifmibdata (net/if_mib.h, #pragma pack(4)) and its struct if_data64.
    private const int NameLength = 16;      // IFNAMSIZ
    private const int FlagsOffset = 20;
    private const int DataOffset = 52;
    private const int InPacketsOffset = DataOffset + 24;
    private const int InErrorsOffset = DataOffset + 32;
    private const int OutPacketsOffset = DataOffset + 40;
    private const int OutErrorsOffset = DataOffset + 48;
    private const int InBytesOffset = DataOffset + 64;
    private const int OutBytesOffset = DataOffset + 72;
    private const int MinLength = OutBytesOffset + 8;

    public IReadOnlyList<InterfaceCounters> Sample()
    {
        var interfaces = new List<InterfaceCounters>();
        var buffer = new byte[256];
        var count = InterfaceCount();

        for (var index = 1; index <= count; index++)
        {
            if (!TryReadInterface(index, buffer, out var data))
            {
                continue;
            }

            var flags = BinaryPrimitives.ReadInt32LittleEndian(data[FlagsOffset..]);
            if ((flags & IffUp) == 0 || (flags & IffLoopback) != 0)
            {
                continue;
            }

            interfaces.Add(new InterfaceCounters(
                Encoding.ASCII.GetString(data[..NameLength]).TrimEnd('\0'),
                ReadCounter(data, InBytesOffset),
                ReadCounter(data, OutBytesOffset),
                ReadCounter(data, InPacketsOffset),
                ReadCounter(data, OutPacketsOffset),
                ReadCounter(data, InErrorsOffset),
                ReadCounter(data, OutErrorsOffset)));
        }

        return interfaces;
    }

    private static long ReadCounter(ReadOnlySpan<byte> data, int offset) =>
        (long)BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);

    private static int InterfaceCount()
    {
        var mib = stackalloc int[] { CtlNet, PfLink, NetlinkGeneric, IfmibSystem, IfmibIfCount };
        int count;
        nuint length = sizeof(int);
        return Libc.sysctl(mib, 5, &count, ref length, null, 0) == 0 ? count : 0;
    }

    /// <summary>Reads one interface's MIB data. Fails for indexes of interfaces that no longer exist.</summary>
    private static bool TryReadInterface(int index, byte[] buffer, out ReadOnlySpan<byte> data)
    {
        var mib = stackalloc int[] { CtlNet, PfLink, NetlinkGeneric, IfmibIfData, index, IfDataGeneral };
        nuint length = (nuint)buffer.Length;
        fixed (byte* p = buffer)
        {
            if (Libc.sysctl(mib, 6, p, ref length, null, 0) != 0 || length < MinLength)
            {
                data = default;
                return false;
            }
        }

        data = buffer.AsSpan(0, (int)length);
        return true;
    }
}
