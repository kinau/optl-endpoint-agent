using System.Diagnostics;
using System.Net.Sockets;
using EndpointAgent.Collectors;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent;

/// <summary>
/// "EndpointAgent --check": reads every sampler twice, prints what the agent would report, and
/// tests that the OTLP endpoint accepts connections. Exits 0 when everything worked.
/// </summary>
public static class SelfCheck
{
    private const double GiB = 1024.0 * 1024 * 1024;
    private static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

    public static async Task<int> RunAsync(AgentOptions options, TextWriter output)
    {
        var failures = 0;
        void Fail(string area, Exception ex)
        {
            failures++;
            output.WriteLine($"  FAILED: {area}: {ex.GetType().Name}: {ex.Message}");
        }

        output.WriteLine($"Endpoint agent self-check on {Environment.MachineName} ({Environment.OSVersion}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
        output.WriteLine($"Running as: {Environment.UserName}{(Environment.IsPrivilegedProcess ? " (elevated)" : " (not elevated)")}");

        var samplers = PlatformServices.CreateSamplers();
        if (samplers is null)
        {
            output.WriteLine("No samplers for this OS; only heartbeat and filesystem collectors run.");
        }
        else
        {
            Section(output, "Inventory", () => PrintInventory(samplers.Inventory.Read(), output), Fail);

            SystemSample? system1 = null;
            ProcessSnapshot? processes1 = null;
            IReadOnlyList<InterfaceCounters>? network1 = null;
            Section(output, "First sample", () =>
            {
                system1 = samplers.System.Sample();
                processes1 = samplers.Processes.Sample();
                network1 = samplers.Network.Sample();
                output.WriteLine($"  ok; sampling again in {Gap.TotalSeconds:F0} s");
            }, Fail);

            var started = Stopwatch.GetTimestamp();
            await Task.Delay(Gap);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;

            if (system1 is not null)
            {
                Section(output, "System", () => PrintSystem(system1, samplers.System.Sample(), elapsed, output), Fail);
            }
            if (processes1 is not null)
            {
                Section(output, "Processes", () => PrintProcesses(processes1, samplers.Processes.Sample(), elapsed, output), Fail);
            }
            if (network1 is not null)
            {
                Section(output, "Network", () => PrintNetwork(network1, samplers.Network.Sample(), elapsed, output), Fail);
            }
        }

        Section(output, "Volumes", () => PrintVolumes(output), Fail);
        await CheckEndpointAsync(options, output, Fail);

        output.WriteLine();
        output.WriteLine(failures == 0 ? "All checks passed." : $"{failures} check(s) failed.");
        return failures == 0 ? 0 : 1;
    }

    private static void Section(TextWriter output, string title, Action body, Action<string, Exception> fail)
    {
        output.WriteLine();
        output.WriteLine($"[{title}]");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            fail(title, ex);
        }
    }

    private static void PrintInventory(MachineInventory i, TextWriter output)
    {
        output.WriteLine($"  {i.Manufacturer} {i.Model}");
        output.WriteLine($"  CPU: {i.CpuModel} ({i.CpuVendor}), {i.PhysicalCores} cores / {i.LogicalCores} logical");
        output.WriteLine($"  RAM: {i.MemoryBytes / GiB:F1} GiB");
        output.WriteLine($"  OS: {i.OsName} {i.OsVersion} (build {i.OsBuild}), booted {i.BootTime:u}");
    }

    private static void PrintSystem(SystemSample before, SystemSample after, double elapsed, TextWriter output)
    {
        var total = after.Cpu.Total - before.Cpu.Total;
        if (total > 0)
        {
            output.WriteLine($"  CPU: user {(after.Cpu.User - before.Cpu.User) / total:P1}, system {(after.Cpu.System - before.Cpu.System) / total:P1}, idle {(after.Cpu.Idle - before.Cpu.Idle) / total:P1} ({after.LogicalCpuCount} logical CPUs)");
        }
        else
        {
            output.WriteLine("  CPU: counters did not advance");
        }

        var m = after.Memory;
        output.WriteLine($"  Memory: {m.TotalBytes / GiB:F1} GiB total = used {m.UsedBytes / GiB:F1} + cached {m.CachedBytes / GiB:F1} + free {m.FreeBytes / GiB:F1}");
        output.WriteLine($"  Swap: {m.SwapUsedBytes / GiB:F2} of {m.SwapTotalBytes / GiB:F2} GiB used");
        output.WriteLine($"  Uptime: {after.Uptime:d\\.hh\\:mm\\:ss}");
        foreach (var d in after.Disks)
        {
            var prev = before.Disks.FirstOrDefault(x => x.Device == d.Device);
            var rate = prev is null ? "" : $", now {(d.ReadBytes - prev.ReadBytes) / elapsed / 1024:F0} KiB/s read, {(d.WriteBytes - prev.WriteBytes) / elapsed / 1024:F0} KiB/s write";
            output.WriteLine($"  Disk {d.Device}: {d.ReadBytes / GiB:F1} GiB read, {d.WriteBytes / GiB:F1} GiB written since boot{rate}");
        }
        if (after.Disks.Count == 0)
        {
            output.WriteLine("  Disks: none found");
        }
    }

    private static void PrintProcesses(ProcessSnapshot before, ProcessSnapshot after, double elapsed, TextWriter output)
    {
        output.WriteLine($"  {after.Processes.Count} readable of {after.TotalCount} processes");
        var previous = before.Processes.ToDictionary(p => (p.Pid, p.StartTime));
        var capacity = elapsed * Environment.ProcessorCount;
        var apps = after.Processes
            .GroupBy(p => p.Name)
            .Select(g => (
                Name: g.Key,
                Count: g.Count(),
                Cpu: g.Sum(p => (p.CpuTime - (previous.GetValueOrDefault((p.Pid, p.StartTime))?.CpuTime ?? p.CpuTime)).TotalSeconds) / capacity,
                Memory: g.Sum(p => p.MemoryBytes)))
            .ToList();

        output.WriteLine("  Top by CPU:");
        foreach (var a in apps.OrderByDescending(a => a.Cpu).Take(5))
        {
            output.WriteLine($"    {a.Cpu,7:P1}  {a.Name} ({a.Count})");
        }
        output.WriteLine("  Top by memory:");
        foreach (var a in apps.OrderByDescending(a => a.Memory).Take(5))
        {
            output.WriteLine($"    {a.Memory / GiB,6:F2} GiB  {a.Name} ({a.Count})");
        }
    }

    private static void PrintNetwork(IReadOnlyList<InterfaceCounters> before, IReadOnlyList<InterfaceCounters> after, double elapsed, TextWriter output)
    {
        foreach (var i in after.Where(i => i.ReceivedBytes + i.SentBytes > 0))
        {
            var prev = before.FirstOrDefault(x => x.Name == i.Name);
            var rate = prev is null ? "" : $", now {(i.ReceivedBytes - prev.ReceivedBytes) / elapsed / 1024:F1} KiB/s in, {(i.SentBytes - prev.SentBytes) / elapsed / 1024:F1} KiB/s out";
            output.WriteLine($"  {i.Name}: {i.ReceivedBytes / GiB:F2} GiB in, {i.SentBytes / GiB:F2} GiB out since boot{rate}");
        }
    }

    private static void PrintVolumes(TextWriter output)
    {
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalSize > 0 && FileSystemCollector.IsUserVolume(d)))
        {
            output.WriteLine($"  {d.Name} ({d.DriveFormat}): {(d.TotalSize - d.TotalFreeSpace) / GiB:F1} of {d.TotalSize / GiB:F1} GiB used");
        }
    }

    private static async Task CheckEndpointAsync(AgentOptions options, TextWriter output, Action<string, Exception> fail)
    {
        var endpoint = options.Otlp.Endpoint
            ?? (Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT") is { Length: > 0 } env ? new Uri(env) : null)
            ?? new Uri(options.Otlp.Protocol == OpenTelemetry.Exporter.OtlpExportProtocol.Grpc ? "http://localhost:4317" : "http://localhost:4318");

        output.WriteLine();
        output.WriteLine("[OTLP endpoint]");
        output.WriteLine($"  {endpoint} ({options.Otlp.Protocol})");
        try
        {
            using var client = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await client.ConnectAsync(endpoint.Host, endpoint.Port, timeout.Token);
            output.WriteLine("  reachable");
        }
        catch (Exception ex)
        {
            fail("OTLP endpoint", new Exception($"cannot connect to {endpoint.Host}:{endpoint.Port} ({ex.Message})"));
        }
    }
}
