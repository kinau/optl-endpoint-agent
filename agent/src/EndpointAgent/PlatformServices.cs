using EndpointAgent.Collectors;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.MacOS;
using EndpointAgent.Windows;

namespace EndpointAgent;

/// <summary>The OS-specific samplers the shared collectors read from.</summary>
public sealed record PlatformSamplers(
    IMachineInventorySource Inventory,
    ISystemSampler System,
    IProcessSampler Processes,
    INetworkSampler Network);

/// <summary>Registers the collectors for the current operating system.</summary>
public static class PlatformServices
{
    /// <summary>Samplers for the current OS, or null when the OS is not supported yet.</summary>
    public static PlatformSamplers? CreateSamplers()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new PlatformSamplers(new MacInventorySource(), new MacSystemSampler(), new MacProcessSampler(), new MacNetworkSampler());
        }

        if (OperatingSystem.IsWindows())
        {
            return new PlatformSamplers(new WindowsInventorySource(), new WindowsSystemSampler(), new WindowsProcessSampler(), new WindowsNetworkSampler());
        }

        return null;
    }

    /// <summary>
    /// Adds the shared collectors, wired to this OS's samplers when there are any, and returns the
    /// inventory source (also used for resource attributes).
    /// </summary>
    public static IMachineInventorySource? AddCollectors(IServiceCollection services)
    {
        services.AddSingleton<ICollector, HeartbeatCollector>();
        services.AddSingleton<ICollector, FileSystemCollector>();

        if (CreateSamplers() is not { } samplers)
        {
            return null;
        }

        services.AddSingleton(samplers.Inventory);
        services.AddSingleton(samplers.System);
        services.AddSingleton(samplers.Processes);
        services.AddSingleton(samplers.Network);
        services.AddSingleton<ICollector, InventoryCollector>();
        services.AddSingleton<ICollector, SystemCollector>();
        services.AddSingleton<ICollector, ProcessCollector>();
        services.AddSingleton<ICollector, NetworkCollector>();
        return samplers.Inventory;
    }

    /// <summary>
    /// Static hardware facts as OpenTelemetry host.* resource attributes, plus a readable
    /// os.description. These are added after the OS detector and replace its description, which on
    /// Windows 11 reads "Microsoft Windows 10.0.26300".
    /// </summary>
    public static IEnumerable<KeyValuePair<string, object>> ResourceAttributes(IMachineInventorySource? source)
    {
        if (source is null)
        {
            return [];
        }

        var inventory = source.Read();
        return
        [
            new("host.type", inventory.Model),
            new("host.cpu.model.name", inventory.CpuModel),
            new("host.cpu.vendor.id", inventory.CpuVendor),
            new("os.description", $"{inventory.OsName} {inventory.OsVersion} (build {inventory.OsBuild})"),
        ];
    }
}
