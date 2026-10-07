using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.MacOS.Interop;

namespace EndpointAgent.MacOS;

/// <summary>Hardware and OS facts from sysctl.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacInventorySource : IMachineInventorySource
{
    public MachineInventory Read()
    {
        var appleSilicon = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        return new MachineInventory(
            Manufacturer: "Apple",
            Model: Libc.SysctlString("hw.model") ?? "unknown",
            CpuModel: Libc.SysctlString("machdep.cpu.brand_string") ?? "unknown",
            CpuVendor: appleSilicon ? "Apple" : Libc.SysctlString("machdep.cpu.vendor") ?? "unknown",
            PhysicalCores: (int)(Libc.SysctlInt64("hw.physicalcpu") ?? 0),
            LogicalCores: (int)(Libc.SysctlInt64("hw.logicalcpu") ?? Environment.ProcessorCount),
            MemoryBytes: Libc.SysctlInt64("hw.memsize") ?? 0,
            OsName: "macOS",
            OsVersion: Libc.SysctlString("kern.osproductversion") ?? Environment.OSVersion.Version.ToString(),
            OsBuild: Libc.SysctlString("kern.osversion") ?? "unknown",
            BootTime: ReadBootTime());
    }

    internal static DateTimeOffset ReadBootTime() =>
        Libc.SysctlStruct<Libc.TimeVal>("kern.boottime") is { } boot
            ? DateTimeOffset.FromUnixTimeSeconds(boot.Seconds).AddTicks(boot.Microseconds * 10L)
            : DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
}
