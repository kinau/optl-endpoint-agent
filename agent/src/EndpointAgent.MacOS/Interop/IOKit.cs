using System.Runtime.InteropServices;
using EndpointAgent.Collectors.Sampling;

namespace EndpointAgent.MacOS.Interop;

/// <summary>
/// Reads per-disk I/O statistics from IOKit: each IOBlockStorageDriver has a "Statistics"
/// dictionary, and its child IOMedia carries the BSD name (disk0, disk4, ...). Disk images
/// (installers, simulator runtimes) are skipped, so only physical disks are reported.
/// </summary>
internal static unsafe partial class IOKit
{
    private const string IOKitLib = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint MainPortDefault = 0;               // kIOMainPortDefault
    private const uint Utf8Encoding = 0x08000100;          // kCFStringEncodingUTF8
    private const int NumberSInt64Type = 4;                // kCFNumberSInt64Type
    private const uint IterateRecursivelyThroughParents = 3; // kIORegistryIterateRecursively | kIORegistryIterateParents

    private static readonly nint BsdNameKey = CFString("BSD Name");
    private static readonly nint StatisticsKey = CFString("Statistics");
    private static readonly nint ProtocolCharacteristicsKey = CFString("Protocol Characteristics");
    private static readonly nint InterconnectLocationKey = CFString("Physical Interconnect Location");
    private static readonly nint BytesReadKey = CFString("Bytes (Read)");
    private static readonly nint BytesWrittenKey = CFString("Bytes (Write)");
    private static readonly nint ReadOpsKey = CFString("Operations (Read)");
    private static readonly nint WriteOpsKey = CFString("Operations (Write)");

    internal static IReadOnlyList<DiskIoCounters> ReadDiskStatistics()
    {
        var disks = new List<DiskIoCounters>();
        // IOServiceGetMatchingServices consumes the matching dictionary, so it is not released here.
        if (IOServiceGetMatchingServices(MainPortDefault, IOServiceMatching("IOBlockStorageDriver"), out var iterator) != 0)
        {
            return disks;
        }

        try
        {
            uint driver;
            while ((driver = IOIteratorNext(iterator)) != 0)
            {
                try
                {
                    if (ReadDriver(driver) is { } disk)
                    {
                        disks.Add(disk);
                    }
                }
                finally
                {
                    IOObjectRelease(driver);
                }
            }
        }
        finally
        {
            IOObjectRelease(iterator);
        }

        return disks;
    }

    private static DiskIoCounters? ReadDriver(uint driver)
    {
        if (IsDiskImage(driver) || IORegistryEntryGetChildEntry(driver, "IOService", out var media) != 0)
        {
            return null;
        }

        string? name;
        try
        {
            name = ReadStringProperty(media, BsdNameKey);
        }
        finally
        {
            IOObjectRelease(media);
        }

        var stats = IORegistryEntryCreateCFProperty(driver, StatisticsKey, 0, 0);
        if (name is null || stats == 0)
        {
            if (stats != 0)
            {
                CFRelease(stats);
            }
            return null;
        }

        try
        {
            return new DiskIoCounters(
                name,
                ReadNumber(stats, BytesReadKey),
                ReadNumber(stats, BytesWrittenKey),
                ReadNumber(stats, ReadOpsKey),
                ReadNumber(stats, WriteOpsKey));
        }
        finally
        {
            CFRelease(stats);
        }
    }

    /// <summary>Disk images report "Physical Interconnect Location" = "File" on an ancestor device.</summary>
    private static bool IsDiskImage(uint driver)
    {
        var characteristics = IORegistryEntrySearchCFProperty(driver, "IOService", ProtocolCharacteristicsKey, 0, IterateRecursivelyThroughParents);
        if (characteristics == 0)
        {
            return false;
        }

        try
        {
            return ReadString(CFDictionaryGetValue(characteristics, InterconnectLocationKey)) == "File";
        }
        finally
        {
            CFRelease(characteristics);
        }
    }

    private static string? ReadStringProperty(uint entry, nint key)
    {
        var value = IORegistryEntryCreateCFProperty(entry, key, 0, 0);
        if (value == 0)
        {
            return null;
        }

        try
        {
            return ReadString(value);
        }
        finally
        {
            CFRelease(value);
        }
    }

    private static string? ReadString(nint cfString)
    {
        if (cfString == 0)
        {
            return null;
        }

        var buffer = stackalloc byte[256];
        return CFStringGetCString(cfString, buffer, 256, Utf8Encoding) ? Marshal.PtrToStringUTF8((nint)buffer) : null;
    }

    // CFDictionaryGetValue follows the "get rule": the returned number is not owned, so not released.
    private static long ReadNumber(nint dictionary, nint key)
    {
        var number = CFDictionaryGetValue(dictionary, key);
        return number != 0 && CFNumberGetValue(number, NumberSInt64Type, out var value) ? value : 0;
    }

    // Keys live for the whole process, so they are intentionally never released.
    private static nint CFString(string value) => CFStringCreateWithCString(0, value, Utf8Encoding);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint IOServiceMatching(string name);

    [LibraryImport(IOKitLib)]
    private static partial int IOServiceGetMatchingServices(uint mainPort, nint matching, out uint iterator);

    [LibraryImport(IOKitLib)]
    private static partial uint IOIteratorNext(uint iterator);

    [LibraryImport(IOKitLib)]
    private static partial int IOObjectRelease(uint obj);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int IORegistryEntryGetChildEntry(uint entry, string plane, out uint child);

    [LibraryImport(IOKitLib)]
    private static partial nint IORegistryEntryCreateCFProperty(uint entry, nint key, nint allocator, uint options);

    [LibraryImport(IOKitLib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint IORegistryEntrySearchCFProperty(uint entry, string plane, nint key, nint allocator, uint options);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string value, uint encoding);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFStringGetCString(nint value, byte* buffer, nint bufferSize, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(nint number, int type, out long value);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint value);
}
