using System.Diagnostics;
using EndpointAgent.Collectors.Sampling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EndpointAgent.Collectors;

/// <summary>
/// Machine inventory as a structured log record, at startup and then every
/// <see cref="InventoryCollectorOptions.Interval"/>. The same facts are also resource attributes.
/// </summary>
public sealed partial class InventoryCollector(
    IMachineInventorySource source,
    IOptions<InventoryCollectorOptions> options,
    ILogger<InventoryCollector> logger) : ICollector
{
    private long? _lastReported;

    public string Name => "inventory";

    public Task CollectAsync(CancellationToken cancellationToken)
    {
        if (_lastReported is { } last && Stopwatch.GetElapsedTime(last) < options.Value.Interval)
        {
            return Task.CompletedTask;
        }

        var i = source.Read();
        LogInventory(i.Manufacturer, i.Model, i.CpuModel, i.CpuVendor, i.PhysicalCores, i.LogicalCores,
            i.MemoryBytes / (1024.0 * 1024 * 1024), i.OsName, i.OsVersion, i.OsBuild, i.BootTime);
        _lastReported = Stopwatch.GetTimestamp();
        return Task.CompletedTask;
    }

    [LoggerMessage(EventName = "machine.inventory", Level = LogLevel.Information,
        Message = "Machine inventory: {Manufacturer} {Model}, {CpuModel} ({CpuVendor}), {PhysicalCores} cores / {LogicalCores} logical CPUs, " +
                  "{MemoryGiB:F0} GiB RAM, {OsName} {OsVersion} ({OsBuild}), booted {BootTime:u}")]
    private partial void LogInventory(string manufacturer, string model, string cpuModel, string cpuVendor,
        int physicalCores, int logicalCores, double memoryGiB, string osName, string osVersion, string osBuild,
        DateTimeOffset bootTime);
}
