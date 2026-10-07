using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent.Windows;

/// <summary>
/// Interface counters from .NET's NetworkInterface, which on Windows reads the 64-bit
/// MIB_IF_ROW2 counters (GetIfEntry2). Filter-driver layers such as "Ethernet-WFP Native MAC Layer
/// LightWeight Filter-0000" are listed as extra interfaces carrying the same traffic as their
/// adapter, so they are dropped to avoid counting it several times.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNetworkSampler : INetworkSampler
{
    public IReadOnlyList<InterfaceCounters> Sample()
    {
        var interfaces = new List<InterfaceCounters>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            try
            {
                var s = nic.GetIPStatistics();
                interfaces.Add(new InterfaceCounters(
                    nic.Name,
                    s.BytesReceived,
                    s.BytesSent,
                    s.UnicastPacketsReceived + s.NonUnicastPacketsReceived,
                    s.UnicastPacketsSent + s.NonUnicastPacketsSent,
                    s.IncomingPacketsWithErrors,
                    s.OutgoingPacketsWithErrors));
            }
            catch (NetworkInformationException)
            {
                // The interface went away between enumeration and reading.
            }
        }

        return WithoutFilterLayers(interfaces);
    }

    /// <summary>Drops interfaces named "{adapter}-{filter}" when "{adapter}" is also present.</summary>
    internal static IReadOnlyList<InterfaceCounters> WithoutFilterLayers(IReadOnlyList<InterfaceCounters> interfaces)
    {
        var names = interfaces.Select(i => i.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return interfaces
            .Where(i => !names.Any(other => i.Name.Length > other.Length + 1
                                            && i.Name.StartsWith(other + "-", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }
}
