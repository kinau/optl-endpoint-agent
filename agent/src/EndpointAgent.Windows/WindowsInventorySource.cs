using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.Windows.Interop;
using Microsoft.Win32;

namespace EndpointAgent.Windows;

/// <summary>Hardware and OS facts from the registry and Win32 APIs (no WMI).</summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsInventorySource : IMachineInventorySource
{
    private const string BiosKey = @"HARDWARE\DESCRIPTION\System\BIOS";
    private const string CpuKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public MachineInventory Read()
    {
        var build = ReadInt(CurrentVersionKey, "CurrentBuildNumber") ?? Environment.OSVersion.Version.Build;
        var revision = ReadInt(CurrentVersionKey, "UBR");

        var memory = new Win32.MemoryStatusEx { Length = (uint)sizeof(Win32.MemoryStatusEx) };
        Win32.GlobalMemoryStatusEx(&memory);

        return new MachineInventory(
            Manufacturer: ReadString(BiosKey, "SystemManufacturer") ?? "unknown",
            Model: ReadString(BiosKey, "SystemProductName") ?? "unknown",
            CpuModel: ReadString(CpuKey, "ProcessorNameString") ?? "unknown",
            CpuVendor: ReadString(CpuKey, "VendorIdentifier") ?? "unknown",
            PhysicalCores: CountPhysicalCores(),
            LogicalCores: Environment.ProcessorCount,
            MemoryBytes: (long)memory.TotalPhys,
            OsName: ProductName(build),
            OsVersion: ReadString(CurrentVersionKey, "DisplayVersion") ?? Environment.OSVersion.Version.ToString(),
            OsBuild: revision is null ? $"{build}" : $"{build}.{revision}",
            BootTime: DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    /// <summary>
    /// ProductName still says "Windows 10" on Windows 11 for compatibility; build 22000 and later is
    /// Windows 11.
    /// </summary>
    internal static string ProductName(string? registryProductName, int build)
    {
        var name = registryProductName ?? "Windows";
        return build >= 22000 ? name.Replace("Windows 10", "Windows 11", StringComparison.Ordinal) : name;
    }

    private static string ProductName(int build) => ProductName(ReadString(CurrentVersionKey, "ProductName"), build);

    private static int CountPhysicalCores()
    {
        uint length = 0;
        Win32.GetLogicalProcessorInformation(null, ref length);
        var count = (int)(length / (uint)sizeof(Win32.SystemLogicalProcessorInformation));
        if (count == 0)
        {
            return 0;
        }

        var buffer = new Win32.SystemLogicalProcessorInformation[count];
        fixed (Win32.SystemLogicalProcessorInformation* p = buffer)
        {
            if (!Win32.GetLogicalProcessorInformation(p, ref length))
            {
                return 0;
            }
        }

        return buffer.Count(i => i.Relationship == Win32.RelationProcessorCore);
    }

    /// <summary>Reads a registry string, treating blank values (some VMs set " ") as missing.</summary>
    private static string? ReadString(string key, string name) =>
        (Registry.LocalMachine.OpenSubKey(key)?.GetValue(name) as string)?.Trim() is { Length: > 0 } value ? value : null;

    private static int? ReadInt(string key, string name) =>
        Registry.LocalMachine.OpenSubKey(key)?.GetValue(name) switch
        {
            int i => i,
            string s when int.TryParse(s, out var i) => i,
            _ => null,
        };
}
