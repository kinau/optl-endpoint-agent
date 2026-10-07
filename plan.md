# Plan: Endpoint telemetry agent (.NET 10 + OpenTelemetry → LGTM stack)

Implementation plan for [spec.md](spec.md).

uberAgent collects hundreds of data points, and many are tied to Windows or Citrix. This plan treats the [uberAgent metrics catalog](https://docs.citrix.com/en-us/uberagent/7-4-0/metrics) as a menu to pick from in phases, not a target to match in full.

## Decisions

| Topic | Decision |
|---|---|
| Windows test target | Windows 11 ARM in a Parallels Desktop VM on the Apple Silicon Mac (switched from VMware Fusion), so the primary Windows build is `win-arm64` |
| Privileges | The agent runs as SYSTEM (Windows Service) or root (launchd daemon), so privileged sources such as the Security event log are available |
| Scope | Phases 1–2 only for now. Phases 3–4 are listed for later and not scheduled |
| Prometheus ingestion | Push: the Collector sends metrics to Prometheus's native OTLP receiver. Prometheus does not scrape the Collector |

## 1. Architecture

```
┌────────────── Endpoint (Windows / macOS) ──────────────┐
│  EndpointAgent (.NET 10 Worker Service)                 │
│   ├─ Collectors (per platform: Windows / macOS)         │
│   ├─ OTel SDK: Meter / ActivitySource / ILogger         │
│   └─ OTLP exporter (gRPC :4317), 60s export interval    │
└───────────────────────────┬─────────────────────────────┘
                            │ OTLP
┌───────────────────────────▼──── docker compose ─────────┐
│ otel-collector ── metrics ─▶ Prometheus  ─┐             │
│                ── traces  ─▶ Tempo       ─┼─▶ Grafana   │
│                ── logs    ─▶ Loki        ─┘             │
└─────────────────────────────────────────────────────────┘
```

## 2. Repository layout

```
learn-optl/
├─ spec.md
├─ plan.md
├─ agent/
│  ├─ EndpointAgent.slnx
│  ├─ src/EndpointAgent/              # Worker host, OTel wiring, scheduler
│  ├─ src/EndpointAgent.Collectors/   # ICollector abstractions + shared logic
│  ├─ src/EndpointAgent.Windows/      # WMI, PerfCounters, Event Log, ETW
│  ├─ src/EndpointAgent.MacOS/        # sysctl/libproc P/Invoke, IOKit, `log`
│  ├─ src/EndpointAgent.UserHelper/   # per-user process: foreground app, idle time
│  └─ tests/EndpointAgent.Tests/
└─ infra/
   ├─ docker-compose.yml
   ├─ otel-collector/config.yaml
   ├─ prometheus/prometheus.yml
   ├─ tempo/tempo.yaml
   ├─ loki/loki.yaml
   └─ grafana/
      ├─ provisioning/datasources/datasources.yaml
      ├─ provisioning/dashboards/dashboards.yaml
      └─ dashboards/endpoint-overview.json
```

## 3. Backend stack

Build this first, so the agent has somewhere to send data.

**docker-compose.yml** runs five services on a shared network, each with a pinned image version:

| Service | Image | Ports |
|---|---|---|
| otel-collector | `otel/opentelemetry-collector-contrib` | 4317 (gRPC), 4318 (HTTP), 13133 (health) |
| prometheus | `prom/prometheus` (3.x) | 9090 (UI, query API, and OTLP ingest at `/api/v1/otlp`) |
| tempo | `grafana/tempo` | 3200 |
| loki | `grafana/loki` (3.x) | 3100 |
| grafana | `grafana/grafana` | 3000 |

**Collector pipelines (`config.yaml`)**

- Receivers: `otlp` over both gRPC and HTTP.
- Processors: `memory_limiter`, then `resource` (adds `deployment.environment`), then `batch`.
- Metrics go to the `otlphttp` exporter, pointed at `http://prometheus:9090/api/v1/otlp`. Prometheus does not scrape the Collector.
- Traces go to the `otlp` exporter, pointed at `tempo:4317`.
- Logs go to the `otlphttp` exporter, pointed at `http://loki:3100/otlp`. Loki 3 accepts OTLP natively, and the old `loki` exporter is deprecated.
- Add the `debug` exporter while developing.

**Prometheus (OTLP receiver)**

- Start Prometheus with `--web.enable-otlp-receiver`.
- In `prometheus.yml`, add an `otlp:` block with `promote_resource_attributes` set to `service.name`, `host.name`, `host.arch`, `os.type` and `os.version`. These become labels on every series, so the dashboard can filter by host and OS. Other resource attributes stay available through the `target_info` series.
- Set `storage.tsdb.out_of_order_time_window` (for example `30m`). Pushed samples from many agents can arrive slightly out of order.
- No `scrape_configs` entries are needed for agent data.

**Grafana provisioning**

- Data sources for Prometheus, Tempo and Loki.
- Cross-links between them: Tempo to Loki (trace to logs), Loki to Tempo (a derived field on `trace_id`), and Tempo to Prometheus (span metrics, optional).
- A dashboard provider that loads `dashboards/*.json`.

**Done when:** `docker compose up` starts everything healthy, and a test signal from `telemetrygen` (the OTel tool that sends sample metrics, traces and logs) shows up in all three backends in Grafana.

## 4. Agent: core host

- Create it with `dotnet new worker`, targeting `net10.0`. Publish self-contained single-file builds for `osx-arm64` and `win-arm64` (the Parallels VM), with `win-x64` and `osx-x64` built for completeness.
- Packages: `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol`. Optionally add `OpenTelemetry.Instrumentation.Runtime` and `.Process` so the agent reports on its own health.
- Resource attributes: `service.name=endpoint-agent`, `service.version`, `host.name`, `host.id`, `os.type`, `os.version`, `host.arch`.
- **1-minute reporting:**
  - Metrics: set `PeriodicExportingMetricReader` to `ExportIntervalMilliseconds = 60000`.
  - Collection loop: a `PeriodicTimer(TimeSpan.FromMinutes(1))` runs the collectors. Each run is wrapped in a root `Activity` named `collection.cycle`, with one child span per collector.
  - Traces and logs: set the batch processors' `ScheduledDelayMilliseconds` to 60000 to match.
- Config lives in `appsettings.json`: the OTLP endpoint, the interval, which collectors are on, and process top-N limits.
- Collector abstraction: `ICollector { string Name; Task CollectAsync(CancellationToken) }`. Platform implementations are registered at startup through `OperatingSystem.IsWindows()` / `IsMacOS()`.
- Metrics use observable instruments (`ObservableGauge` / `ObservableCounter`), reading from a snapshot that the collection loop refreshes. Events (crashes, logons, boots) go out as logs, and as spans where they have a duration.
- Run it as a Windows Service (`UseWindowsService`) under LocalSystem on Windows, and as a launchd daemon (a `.plist` in `/Library/LaunchDaemons`) as root on macOS.
- Running as SYSTEM or root means per-user data (foreground app, idle time) is not directly visible from the service, because it runs outside the user's desktop session. On both platforms, add a small per-user helper that starts at logon (a Run key or scheduled task on Windows, a LaunchAgent on macOS) and sends foreground-app and idle data to the service over a local named pipe or Unix socket. On Windows, session and logon data still comes straight from the WTS API inside the service.

## 5. Agent: collectors, mapped to uberAgent categories

Metric names follow OTel semantic conventions (`system.*`, `process.*`) where one exists, and use an `endpoint.*` prefix otherwise.

| Phase | uberAgent area | Data points | Windows source | macOS source | Signal |
|---|---|---|---|---|---|
| **P1** | Machine performance | CPU %, memory used/available, disk I/O and free space, network bytes, uptime | PerformanceCounter, `DriveInfo`, `NetworkInterface` | `host_statistics64` / `sysctl` P/Invoke, `DriveInfo`, `NetworkInterface` | metrics |
| **P1** | Process / app performance | Top-N processes by CPU, RAM and IO, with process count | `Process` API + WMI `Win32_PerfFormattedData_PerfProc_Process` | `Process` API + `libproc` (`proc_pid_rusage`) | metrics (bounded `process.executable.name` label) |
| **P1** | Machine inventory | OS, model, CPU, RAM, disks | WMI (`Win32_ComputerSystem`, `Win32_OperatingSystem`) | `sysctl hw.*`, `system_profiler -json` (hourly) | logs + resource attributes |
| **P2** | Application inventory and usage | Installed apps; foreground app and its active time | Registry Uninstall keys; `GetForegroundWindow` (per-user helper) | `/Applications` Info.plist; `NSWorkspace` frontmost app (per-user helper) | logs + metrics |
| **P2** | Application errors | Crashes and hangs | Event Log (Application Error 1000, Hang 1002) | `~/Library/Logs/DiagnosticReports` watcher | logs (+ counter) |
| **P2** | User sessions | Active user, session count, logon/logoff, idle time | WTS API, Security Event Log 4624/4634 | `utmpx`, `CGEventSourceSecondsSinceLastEventType` (per-user helper) | metrics + logs |
| *Later* | | *Phases 3–4 below are deferred and not scheduled.* | | | |
| **P3** | Logon duration | Total logon time and phases (GPO, profile, shell) | Event Log (Group Policy, User Profile Service) | Approximation from the unified log | traces (one span per phase) |
| **P3** | Boot / shutdown | Boot duration and phases, slow drivers/services | `Microsoft-Windows-Diagnostics-Performance` 100/200 | `kern.boottime`, unified log | traces + logs |
| **P3** | Network per application | Connections, failures, latency per process | ETW (`Microsoft-Windows-Kernel-Network`) or `GetExtendedTcpTable` | `nettop -P -L 1` / `lsof -i` | metrics |
| **P4** | GPU | Utilization and memory | Perf counters `GPU Engine` | IOKit `IOAccelerator` (limited) | metrics |
| **P4** | Browser / web apps | Per-tab performance | Needs a browser extension | Same | stretch goal |
| — | Citrix, NetScaler, ESA security | — | — | — | **out of scope** |

Two rules hold across every collector:

- Keep metric cardinality bounded: top-N processes only, and no PIDs or usernames as metric labels. High-cardinality details go into logs instead.
- The agent runs privileged, but each collector still catches access and platform errors, logs them, and skips that cycle instead of crashing the agent. This also keeps it usable during development, when it runs as a normal user from `dotnet run`.

## 6. Grafana dashboard ("Endpoint Overview")

The dashboard has variables for `host`, `os` and `user`, and these rows:

1. **Fleet health:** host count, top hosts by CPU and memory, crash count over 24h.
2. **Device performance:** CPU, memory, disk and network time series per host.
3. **App performance:** top processes by CPU/RAM, app crashes and hangs (Loki panel), foreground app usage.
4. **User activity:** active sessions, logons and logoffs over time, idle versus active time.
5. **Agent health:** last report time per host, export errors, the agent's own CPU and memory, and a link from each collection cycle to its trace in Tempo.

A **Boot and logon** row (duration histograms plus a Tempo trace list) comes later with Phase 3.

## 7. Delivery milestones

| # | Milestone | Done when |
|---|---|---|
| M1 | Infra stack | `telemetrygen` data visible in Prometheus (through its OTLP receiver), Tempo and Loki through Grafana |
| M2 | Agent skeleton | Worker sends resource info, a heartbeat metric, cycle traces and logs every 60s from the Mac |
| M3 | Phase 1 collectors (macOS) | Machine and process metrics on the dashboard |
| M4 | Phase 1 collectors (Windows) | Same data from the Windows 11 ARM VM in Parallels; dashboard filters by `os` |
| M5 | Phase 2 | App inventory, crashes, sessions and idle time on both platforms, including the per-user helper |
| M6 | Packaging | Windows Service install script (`sc.exe create`) and macOS launchd plists (daemon + per-user agent); README |

**Reaching the stack from the VM:** the Collector publishes 4317/4318 on all host interfaces. On Parallels' shared network the Mac is `10.211.55.2` from inside the VM, so the agent there uses `--Agent:Otlp:Endpoint=http://10.211.55.2:4317` (or the `OTEL_EXPORTER_OTLP_ENDPOINT` environment variable). `prlctl exec` runs commands in the VM as SYSTEM, which matches how the Windows Service will run.

## 8. Testing

- **Unit tests:** collector parsing logic, fed fake WMI, sysctl and log data through interfaces.
- **Integration tests:** run the agent with the in-memory exporter (`OpenTelemetry.Exporter.InMemory`) and check metric names, units and attributes.
- **End-to-end:** run the compose stack, then query the Prometheus, Loki and Tempo APIs to confirm data from the agent arrived.

## 9. Later (not scheduled)

- **Phase 3:** logon duration and boot/shutdown traces, per-application network metrics, and the dashboard's Boot and logon row.
- **Phase 4:** GPU metrics and browser/web app monitoring.
