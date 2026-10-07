using System.Buffers.Binary;
using System.Text;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.Windows;

namespace EndpointAgent.Tests;

// The helpers under test are pure parsing code that runs on any OS; only the classes that call
// the Windows APIs are Windows-only.
#pragma warning disable CA1416

/// <summary>
/// Exercises the Windows parsers on any OS with hand-built NtQuerySystemInformation blocks laid out
/// like the 64-bit Windows structs.
/// </summary>
public sealed class WindowsParsingTests
{
    private const nint FakeBase = 0x10_0000;
    private const int EntrySize = 256;

    [Fact]
    public void ProcessBlock_IsParsed_SkippingIdleAndResolvingNames()
    {
        var block = new ProcessBlock()
            .Add(pid: 0, name: null, createTime: 0, userTicks: 999_000_000, kernelTicks: 0, privateBytes: 0, read: 0, write: 0)
            .Add(pid: 4, name: null, createTime: 1, userTicks: 0, kernelTicks: 50_000_000, privateBytes: 100, read: 10, write: 20)
            .Add(pid: 1234, name: "chrome.exe", createTime: 42, userTicks: 30_000_000, kernelTicks: 10_000_000, privateBytes: 5_000_000, read: 7, write: 9)
            .Build();

        var snapshot = WindowsProcessSampler.Parse(block, FakeBase);

        Assert.Equal(2, snapshot.TotalCount);
        var system = Assert.Single(snapshot.Processes, p => p.Pid == 4);
        Assert.Equal("System", system.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), system.CpuTime);

        var chrome = Assert.Single(snapshot.Processes, p => p.Pid == 1234);
        Assert.Equal("chrome.exe", chrome.Name);
        Assert.Equal(42UL, chrome.StartTime);
        Assert.Equal(TimeSpan.FromSeconds(4), chrome.CpuTime);
        Assert.Equal(5_000_000, chrome.MemoryBytes);
        Assert.Equal(7, chrome.DiskReadBytes);
        Assert.Equal(9, chrome.DiskWriteBytes);
    }

    [Fact]
    public void ProcessBlock_NamePointerOutsideBlock_FallsBackToPid()
    {
        var block = new ProcessBlock().Add(pid: 77, name: "x.exe", createTime: 1, userTicks: 0, kernelTicks: 0, privateBytes: 0, read: 0, write: 0).Build();
        // Point the name 1 MiB past the block.
        BinaryPrimitives.WriteInt64LittleEndian(block.AsSpan(64), FakeBase + (1 << 20));

        Assert.Equal("pid-77", Assert.Single(WindowsProcessSampler.Parse(block, FakeBase).Processes).Name);
    }

    [Fact]
    public void PageFileBlock_SumsAllPageFiles()
    {
        // Two SYSTEM_PAGEFILE_INFORMATION entries of 32 bytes: NextEntryOffset, TotalSize, TotalInUse (pages).
        var block = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(4), 1000);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(8), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(32), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(36), 500);
        BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(40), 50);

        var (total, used) = WindowsSystemSampler.SumPageFiles(block, pageSize: 4096);

        Assert.Equal(1500L * 4096, total);
        Assert.Equal(150L * 4096, used);
    }

    [Fact]
    public void Network_DropsFilterLayersOfAnAdapter()
    {
        InterfaceCounters Nic(string name) => new(name, 100, 10, 1, 1, 0, 0);
        var interfaces = new[]
        {
            Nic("Ethernet"),
            Nic("Ethernet-WFP Native MAC Layer LightWeight Filter-0000"),
            Nic("Ethernet-QoS Packet Scheduler-0000"),
            Nic("Wi-Fi"),
            Nic("Wi-Fi-WFP 802.3 MAC Layer LightWeight Filter-0000"),
            Nic("vEthernet (WSL)"),
        };

        var names = WindowsNetworkSampler.WithoutFilterLayers(interfaces).Select(i => i.Name);

        Assert.Equal(["Ethernet", "Wi-Fi", "vEthernet (WSL)"], names);
    }

    [Theory]
    [InlineData("Windows 10 Pro", 26100, "Windows 11 Pro")]
    [InlineData("Windows 10 Enterprise", 19045, "Windows 10 Enterprise")]
    [InlineData(null, 22631, "Windows")]
    public void ProductName_ReportsWindows11ForBuild22000AndLater(string? registryName, int build, string expected) =>
        Assert.Equal(expected, WindowsInventorySource.ProductName(registryName, build));

    /// <summary>Builds SYSTEM_PROCESS_INFORMATION entries followed by their UTF-16 image names.</summary>
    private sealed class ProcessBlock
    {
        private readonly List<(int Pid, string? Name, long CreateTime, long User, long Kernel, long Private, long Read, long Write)> _entries = [];

        public ProcessBlock Add(int pid, string? name, long createTime, long userTicks, long kernelTicks, long privateBytes, long read, long write)
        {
            _entries.Add((pid, name, createTime, userTicks, kernelTicks, privateBytes, read, write));
            return this;
        }

        public byte[] Build()
        {
            var namesStart = _entries.Count * EntrySize;
            var names = _entries.Select(e => e.Name is null ? [] : Encoding.Unicode.GetBytes(e.Name)).ToList();
            var block = new byte[namesStart + names.Sum(n => n.Length)];

            var nameOffset = namesStart;
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                var entry = block.AsSpan(i * EntrySize, EntrySize);
                BinaryPrimitives.WriteUInt32LittleEndian(entry[0..], i == _entries.Count - 1 ? 0u : EntrySize);
                BinaryPrimitives.WriteInt64LittleEndian(entry[8..], e.Private);
                BinaryPrimitives.WriteInt64LittleEndian(entry[32..], e.CreateTime);
                BinaryPrimitives.WriteInt64LittleEndian(entry[40..], e.User);
                BinaryPrimitives.WriteInt64LittleEndian(entry[48..], e.Kernel);
                BinaryPrimitives.WriteInt64LittleEndian(entry[80..], e.Pid);
                BinaryPrimitives.WriteInt64LittleEndian(entry[232..], e.Read);
                BinaryPrimitives.WriteInt64LittleEndian(entry[240..], e.Write);

                if (names[i].Length > 0)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(entry[56..], (ushort)names[i].Length);
                    BinaryPrimitives.WriteInt64LittleEndian(entry[64..], FakeBase + nameOffset);
                    names[i].CopyTo(block.AsSpan(nameOffset));
                    nameOffset += names[i].Length;
                }
            }

            return block;
        }
    }
}
