using System.Diagnostics.Metrics;

namespace EndpointAgent.Collectors;

/// <summary>
/// Disk space per volume (system.filesystem.* semantic conventions), using .NET's cross-platform
/// <see cref="DriveInfo"/> with a per-OS rule for which volumes matter to users.
/// </summary>
public sealed class FileSystemCollector : ICollector
{
    // macOS: smaller /Volumes mounts are almost always installer disk images, not storage.
    private const long MinExternalVolumeBytes = 2L * 1024 * 1024 * 1024;

    private IReadOnlyList<Volume> _volumes = [];

    public FileSystemCollector(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(AgentDiagnostics.SourceName);
        meter.CreateObservableUpDownCounter("system.filesystem.usage", ObserveUsage, unit: "By",
            description: "Volume space by state (used, free, reserved).");
        meter.CreateObservableGauge("system.filesystem.utilization", ObserveUtilization, unit: "1",
            description: "Share of volume space in use.");
    }

    public string Name => "filesystem";

    public Task CollectAsync(CancellationToken cancellationToken)
    {
        var volumes = new List<Volume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.TotalSize > 0 && IsUserVolume(drive))
                {
                    volumes.Add(new Volume(drive.Name, drive.DriveFormat, drive.TotalSize, drive.TotalFreeSpace, drive.AvailableFreeSpace));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A volume can disappear or deny access between enumeration and reading; skip it.
            }
        }

        _volumes = volumes;
        return Task.CompletedTask;
    }

    public static bool IsUserVolume(DriveInfo drive)
    {
        if (OperatingSystem.IsWindows())
        {
            return drive.DriveType == DriveType.Fixed;
        }

        if (OperatingSystem.IsMacOS())
        {
            // The APFS Data volume holds user data; "/" is the sealed, read-only system volume
            // sharing the same container. Recovery, simulator images and system mounts are skipped.
            return drive.Name == "/System/Volumes/Data"
                || (drive.Name.StartsWith("/Volumes/", StringComparison.Ordinal)
                    && drive.Name != "/Volumes/Recovery"
                    && drive.DriveType == DriveType.Fixed
                    && drive.TotalSize >= MinExternalVolumeBytes);
        }

        return drive.DriveType == DriveType.Fixed;
    }

    private IEnumerable<Measurement<long>> ObserveUsage() => _volumes.SelectMany(v => new[]
    {
        new Measurement<long>(v.TotalBytes - v.TotalFreeBytes, Tags(v, "used")),
        new Measurement<long>(v.AvailableBytes, Tags(v, "free")),
        new Measurement<long>(v.TotalFreeBytes - v.AvailableBytes, Tags(v, "reserved")),
    });

    private IEnumerable<Measurement<double>> ObserveUtilization() => _volumes.Select(v =>
        new Measurement<double>((double)(v.TotalBytes - v.TotalFreeBytes) / v.TotalBytes, Tags(v)));

    private static KeyValuePair<string, object?>[] Tags(Volume v, string? state = null) => state is null
        ?
        [
            new("system.filesystem.mountpoint", v.MountPoint),
            new("system.filesystem.type", v.Format),
        ]
        :
        [
            new("system.filesystem.mountpoint", v.MountPoint),
            new("system.filesystem.type", v.Format),
            new("system.filesystem.state", state),
        ];

    private sealed record Volume(string MountPoint, string Format, long TotalBytes, long TotalFreeBytes, long AvailableBytes);
}
