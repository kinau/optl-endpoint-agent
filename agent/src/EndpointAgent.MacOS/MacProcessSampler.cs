using System.Runtime.Versioning;
using System.Text;
using EndpointAgent.Collectors.Sampling;
using EndpointAgent.MacOS.Interop;

namespace EndpointAgent.MacOS;

/// <summary>
/// Per-process counters from libproc. Without root, processes owned by other users cannot be read
/// and are only included in the total count.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacProcessSampler : IProcessSampler
{
    private readonly double _nanosecondsPerTick;
    private Dictionary<(int Pid, ulong StartTime), string> _names = [];

    public MacProcessSampler()
    {
        // rusage CPU times are in Mach absolute time units (not nanoseconds on Apple Silicon).
        Libc.mach_timebase_info(out var timebase);
        _nanosecondsPerTick = timebase.Denom == 0 ? 1 : (double)timebase.Numer / timebase.Denom;
    }

    public ProcessSnapshot Sample()
    {
        var pids = ListPids();
        var processes = new List<ProcessSample>(pids.Length);
        var names = new Dictionary<(int, ulong), string>(pids.Length);

        foreach (var pid in pids)
        {
            Libc.RusageInfoV2Struct usage;
            if (Libc.proc_pid_rusage(pid, Libc.RusageInfoV2, &usage) != 0)
            {
                continue;
            }

            var key = (pid, usage.ProcStartAbstime);
            if (!_names.TryGetValue(key, out var name) && (name = ReadName(pid)) is null)
            {
                continue;
            }
            names[key] = name;

            var cpuNanoseconds = (usage.UserTime + usage.SystemTime) * _nanosecondsPerTick;
            processes.Add(new ProcessSample(
                pid,
                usage.ProcStartAbstime,
                name,
                TimeSpan.FromTicks((long)(cpuNanoseconds / 100)),
                (long)usage.PhysFootprint,
                (long)usage.DiskioBytesRead,
                (long)usage.DiskioBytesWritten));
        }

        // Keep only names of live processes so the cache cannot grow without bound.
        _names = names;
        return new ProcessSnapshot(processes, pids.Length);
    }

    private static int[] ListPids()
    {
        var count = Libc.proc_listallpids(null, 0);
        if (count <= 0)
        {
            return [];
        }

        // Leave headroom for processes started between the two calls.
        var buffer = new int[count + 64];
        fixed (int* p = buffer)
        {
            count = Libc.proc_listallpids(p, buffer.Length * sizeof(int));
        }

        return count <= 0 ? [] : buffer.AsSpan(0, Math.Min(count, buffer.Length)).ToArray();
    }

    private static string? ReadName(int pid)
    {
        if (pid == 0)
        {
            return "kernel_task";
        }

        var buffer = stackalloc byte[Libc.ProcPidPathInfoMaxSize];
        var length = Libc.proc_pidpath(pid, buffer, Libc.ProcPidPathInfoMaxSize);
        return length > 0 ? Path.GetFileName(Encoding.UTF8.GetString(buffer, length)) : null;
    }
}
