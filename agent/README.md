# Endpoint agent

.NET 10 worker that runs collectors on a timer and reports metrics, traces and logs over OTLP to the Collector in `../infra`.

## Run

Start the stack first (`cd ../infra && docker compose up -d`), then:

```sh
cd agent
dotnet run --project src/EndpointAgent          # Development: collects and exports every 15 s
dotnet run --project src/EndpointAgent --no-launch-profile   # Production: every 60 s
dotnet test
dotnet run --project src/EndpointAgent -- --check   # one-off self-check, see below
```

## Self-check

`EndpointAgent --check` reads every sampler twice (2 s apart), prints what the agent would report
(inventory, CPU, memory, disks, top apps, network, volumes), tests that the OTLP endpoint accepts
connections, and exits with 0 if everything worked. Settings can be passed on the command line:

```sh
EndpointAgent --check --Agent:Otlp:Endpoint=http://10.211.55.2:4317
```

## Windows build

```sh
dotnet publish src/EndpointAgent -c Release -r win-arm64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
  -o publish/win-arm64
```

This produces `EndpointAgent.exe` (runtime included) plus `appsettings.json`. Use `-r win-x64` for Intel/AMD machines.
From the Parallels VM (shared network), the Mac is `10.211.55.2`, so pass `--Agent:Otlp:Endpoint=http://10.211.55.2:4317`.

## Configuration (`appsettings.json`, section `Agent`)

| Key | Default | Meaning |
|---|---|---|
| `CollectionInterval` | `00:01:00` | How often collectors run |
| `ExportInterval` | `00:01:00` | Metric export interval and trace/log batch flush delay |
| `CollectorTimeout` | `00:00:30` | A collector running longer than this is cancelled |
| `DisabledCollectors` | `[]` | Collector names to skip, e.g. `["process"]` |
| `Processes:TopCount` | `10` | Apps reported per cycle: top N by CPU plus top N by memory |
| `Inventory:Interval` | `01:00:00` | How often the machine inventory log record repeats |
| `Otlp:Endpoint` | unset | Collector URL. Unset means the SDK default `http://localhost:4317`, or `OTEL_EXPORTER_OTLP_ENDPOINT` if set |
| `Otlp:Protocol` | `Grpc` | `Grpc` (port 4317) or `HttpProtobuf` (port 4318) |

Any key can be overridden with an environment variable, e.g. `Agent__Otlp__Endpoint=http://10.211.55.2:4317` for the Parallels Windows VM.

## Collectors

Platform projects only read raw OS counters ("samplers"); the shared collectors in
`EndpointAgent.Collectors` turn them into metrics, so Windows support only needs new samplers.

| Collector | Reports | macOS source | Windows source |
|---|---|---|---|
| `system` | CPU utilization by mode, memory by state, swap, disk I/O per physical disk, uptime | Mach `host_statistics`, `sysctl`, IOKit `IOBlockStorageDriver` (disk images skipped) | `GetSystemTimes`, `GlobalMemoryStatusEx` + `GetPerformanceInfo`, page files via `NtQuerySystemInformation`, `IOCTL_DISK_PERFORMANCE` on `\\.\PhysicalDriveN` |
| `process` | Per app (processes grouped by executable name): CPU share, memory, disk throughput, instance count; total process count | `libproc` (`proc_pid_rusage`, `proc_pidpath`) | One `NtQuerySystemInformation(SystemProcessInformation)` call |
| `network` | Bytes, packets and errors per interface | `sysctl net.link.generic.ifdata` (64-bit counters) | .NET `NetworkInterface` (64-bit `GetIfEntry2` counters) |
| `filesystem` | Space used/free/reserved per volume | .NET `DriveInfo` (Data volume and external drives ≥ 2 GB) | .NET `DriveInfo` (fixed drives) |
| `inventory` | Hardware and OS facts as a log record at startup and hourly | `sysctl` | Registry (BIOS, CPU, `CurrentVersion`), `GetLogicalProcessorInformation` |
| `heartbeat` | Agent uptime and a log record each cycle | — | — |

Notes:

- CPU values are averages over the time since the previous cycle. The first cycle samples twice, one second apart, so values appear right after startup.
- Memory follows Activity Monitor: used = app + wired + compressed; cached = file-backed + purgeable.
- macOS: without root, the process collector can read only the current user's processes (others still count in `system.process.count`). Run as root for full coverage.
- Windows: the process collector sees every process without administrator rights. Memory is the private working set (Task Manager's "Memory" column); disk throughput is all I/O the process issued (files, network and devices), which is what Windows tracks per process. Memory "cached" is the standby list, so used + cached + free = total.
- .NET's `NetworkInterface` statistics and the `NET_RT_IFLIST2` sysctl return byte counts truncated to 32 bits on macOS, which is why the network sampler uses the per-interface MIB.

## What it reports

| Signal | Name | Notes |
|---|---|---|
| Trace | `collection.cycle` → `collect {collector}` | One trace per cycle; failed or timed-out collectors are marked as errors |
| Metric | `endpoint.agent.uptime` (s) | Heartbeat. Prometheus: `endpoint_agent_uptime_seconds` |
| Metric | `endpoint.agent.collection.cycles` | By `outcome` (`ok`/`error`). Prometheus: `endpoint_agent_collection_cycles_total` |
| Metric | `endpoint.agent.collector.duration` (s) | Histogram by `collector.name` and `outcome` |
| Metric | `system.cpu.*`, `system.memory.*`, `system.paging.usage`, `system.disk.*`, `system.network.*`, `system.filesystem.*`, `system.uptime`, `system.process.count` | Machine performance (OpenTelemetry semantic convention names) |
| Metric | `endpoint.process.cpu.utilization`, `.memory.usage`, `.disk.throughput`, `.count` | Top apps, labelled `process.executable.name` |
| Metric | `dotnet.*`, `process.*` | The agent's own runtime and process health |
| Log | Heartbeat and cycle summaries | Records written during a cycle carry its `trace_id` |

Resource attributes (`service.*`, `host.name`, `host.id`, `host.arch`, `os.*`) come from the OpenTelemetry host and OS resource detectors; `host.type` (model), `host.cpu.model.name` and `host.cpu.vendor.id` come from the inventory source.

## Adding a collector

Implement `ICollector` in `EndpointAgent.Collectors` (or a platform project), create instruments on the meter named `AgentDiagnostics.SourceName` via `IMeterFactory`, and register it in `PlatformServices.cs`:

```csharp
services.AddSingleton<ICollector, MyCollector>();
```
