using EndpointAgent.MacOS;

namespace EndpointAgent.Tests;

/// <summary>
/// Checks the macOS samplers against the live machine. Each test returns early on other platforms.
/// </summary>
public sealed class MacSamplerTests
{
    [Fact]
    public void System_ReadsPlausibleCountersThatAdvance()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var sampler = new MacSystemSampler();
        var first = sampler.Sample();
        // The kernel's CPU tick counters are coarse; the agent samples at least a second apart.
        Thread.Sleep(1000);
        var second = sampler.Sample();

        Assert.True(first.LogicalCpuCount > 0);
        Assert.True(second.Cpu.Total > first.Cpu.Total, "CPU ticks should advance.");
        var m = second.Memory;
        Assert.True(m.TotalBytes >= 1L << 30);
        Assert.True(m.UsedBytes > 0 && m.UsedBytes <= m.TotalBytes);
        Assert.Equal(m.TotalBytes, m.UsedBytes + m.CachedBytes + m.FreeBytes);
        Assert.NotEmpty(second.Disks);
        Assert.All(second.Disks, d => Assert.StartsWith("disk", d.Device));
        Assert.True(second.Uptime > TimeSpan.Zero);
    }

    [Fact]
    public void Process_IncludesThisProcess()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var snapshot = new MacProcessSampler().Sample();

        Assert.True(snapshot.TotalCount >= snapshot.Processes.Count);
        var self = Assert.Single(snapshot.Processes, p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(self.Name));
        Assert.True(self.MemoryBytes > 0);
        Assert.True(self.CpuTime > TimeSpan.Zero);
    }

    [Fact]
    public void Network_ReportsNamedNonLoopbackInterfaces()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var interfaces = new MacNetworkSampler().Sample();

        Assert.NotEmpty(interfaces);
        Assert.DoesNotContain(interfaces, i => i.Name.StartsWith("lo", StringComparison.Ordinal));
        Assert.All(interfaces, i => Assert.False(string.IsNullOrWhiteSpace(i.Name)));
    }

    [Fact]
    public void Inventory_ReadsHardwareAndOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var inventory = new MacInventorySource().Read();

        Assert.NotEqual("unknown", inventory.Model);
        Assert.NotEqual("unknown", inventory.CpuModel);
        Assert.True(inventory.PhysicalCores > 0);
        Assert.True(inventory.MemoryBytes > 0);
        Assert.Matches(@"^\d+\.\d+", inventory.OsVersion);
        Assert.True(inventory.BootTime < DateTimeOffset.UtcNow);
    }
}
