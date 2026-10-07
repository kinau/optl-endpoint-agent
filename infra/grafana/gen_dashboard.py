import json
PROM = {"type": "prometheus", "uid": "prometheus"}
LOKI = {"type": "loki", "uid": "loki"}
# Every query filters on both variables: "All" hosts must still respect the OS selection.
H = 'host_name=~"$host", os_type=~"$os"'
panels = []
pid = 0
def nid():
    global pid; pid += 1; return pid

def row(title, y):
    panels.append({"type": "row", "title": title, "id": nid(), "collapsed": False, "gridPos": {"h": 1, "w": 24, "x": 0, "y": y}, "panels": []})

def target(expr, legend="", ref="A", instant=False, fmt=None):
    t = {"datasource": PROM, "expr": expr, "legendFormat": legend or "__auto", "refId": ref}
    if instant: t.update({"instant": True, "range": False})
    if fmt: t["format"] = fmt
    return t

def status_thresholds(warn, crit):
    return {"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "#EAB839", "value": warn}, {"color": "red", "value": crit}]}

def stat(title, expr, x, y, unit, desc, thresholds=None, w=4):
    panels.append({
        "type": "stat", "title": title, "id": nid(), "description": desc, "datasource": PROM,
        "gridPos": {"h": 4, "w": w, "x": x, "y": y},
        "targets": [target(expr, instant=True)],
        "fieldConfig": {"defaults": {"unit": unit, "decimals": 0 if unit in ("none", "short") else 1,
                         "color": {"mode": "thresholds"},
                         "thresholds": thresholds or {"mode": "absolute", "steps": [{"color": "text", "value": None}]}}, "overrides": []},
        "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                    "colorMode": "value" if thresholds else "none", "graphMode": "none", "textMode": "value", "justifyMode": "center"}})

def ts(title, targets, x, y, w, unit, desc, h=8, maxv=None, stack=False, decimals=None, softmax=None):
    d = {"unit": unit, "min": 0, "custom": {"lineWidth": 2, "fillOpacity": 25 if stack else 0, "showPoints": "never",
         "spanNulls": 180000, "stacking": {"mode": "normal" if stack else "none", "group": "A"}, "axisSoftMin": 0},
         "color": {"mode": "palette-classic"}}
    if maxv is not None: d["max"] = maxv
    if decimals is not None: d["decimals"] = decimals
    if softmax is not None: d["custom"]["axisSoftMax"] = softmax
    panels.append({"type": "timeseries", "title": title, "id": nid(), "description": desc, "datasource": PROM,
        "gridPos": {"h": h, "w": w, "x": x, "y": y}, "targets": targets,
        "fieldConfig": {"defaults": d, "overrides": []},
        "options": {"legend": {"displayMode": "list", "placement": "bottom", "showLegend": True},
                    "tooltip": {"mode": "multi", "sort": "desc"}}})

def bargauge(title, expr, legend, x, y, w, unit, desc, h=9, thresholds=None, maxv=None):
    d = {"unit": unit, "min": 0, "color": {"mode": "thresholds" if thresholds else "fixed", "fixedColor": "blue"},
         "thresholds": thresholds or {"mode": "absolute", "steps": [{"color": "blue", "value": None}]}}
    if maxv is not None: d["max"] = maxv
    panels.append({"type": "bargauge", "title": title, "id": nid(), "description": desc, "datasource": PROM,
        "gridPos": {"h": h, "w": w, "x": x, "y": y}, "targets": [target(expr, legend, instant=True)],
        "fieldConfig": {"defaults": d, "overrides": []},
        "options": {"orientation": "horizontal", "displayMode": "basic", "showUnfilled": True, "valueMode": "text",
                    "namePlacement": "left", "sizing": "manual", "minVizHeight": 22, "maxVizHeight": 22,
                    "reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False}}})

# ---- Fleet
y = 0
row("Fleet", y); y += 1
stat("Hosts reporting", f'count(count by (host_name) (endpoint_agent_uptime_seconds{{{H}}}))', 0, y, "none",
     "Hosts that sent data in the last 5 minutes.")
stat("Average CPU busy", f'avg(1 - system_cpu_utilization_ratio{{cpu_mode="idle", {H}}})', 4, y, "percentunit",
     "Mean CPU busy share across selected hosts.", status_thresholds(0.7, 0.9))
stat("Average memory used", f'avg(system_memory_utilization_ratio{{system_memory_state="used", {H}}})', 8, y, "percentunit",
     "Mean memory used share (excludes reclaimable file cache).", status_thresholds(0.8, 0.95))
stat("Fullest volume", f'max(system_filesystem_utilization_ratio{{{H}}})', 12, y, "percentunit",
     "Highest disk-space utilization of any reported volume.", status_thresholds(0.8, 0.9))
stat("Running processes", f'sum(system_process_count{{{H}}})', 16, y, "none",
     "All processes across selected hosts.")
stat("Late agents (> 3 min)", f'count((time() - timestamp(endpoint_agent_uptime_seconds{{{H}}})) > 180) or vector(0)', 20, y, "none",
     "Hosts whose last report is older than 3 minutes (agents report every minute). Hosts silent for over 5 minutes drop out of the dashboard.",
     {"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "red", "value": 1}]})
y += 4

fleet_targets = [
    # The newest target_info per host only: resource attributes change (e.g. after an OS update) and
    # the previous series stays visible for Prometheus's 5-minute lookback.
    target(f'max by (host_name, host_type, os_description) (topk by (host_name) (1, timestamp(target_info{{job="endpoint-agent", {H}}})))', ref="INFO", instant=True, fmt="table"),
    target(f'max by (host_name) (1 - system_cpu_utilization_ratio{{cpu_mode="idle", {H}}})', ref="CPU", instant=True, fmt="table"),
    target(f'max by (host_name) (system_memory_utilization_ratio{{system_memory_state="used", {H}}})', ref="MEM", instant=True, fmt="table"),
    target(f'max by (host_name) (system_filesystem_utilization_ratio{{{H}}})', ref="DISK", instant=True, fmt="table"),
    target(f'max by (host_name) (system_uptime_seconds{{{H}}})', ref="UP", instant=True, fmt="table"),
    target(f'max by (host_name) (time() - timestamp(endpoint_agent_uptime_seconds{{{H}}}))', ref="SEEN", instant=True, fmt="table"),
]
def gauge_override(name, warn, crit):
    return {"matcher": {"id": "byName", "options": name}, "properties": [
        {"id": "unit", "value": "percentunit"}, {"id": "min", "value": 0}, {"id": "max", "value": 1},
        {"id": "thresholds", "value": status_thresholds(warn, crit)},
        {"id": "custom.cellOptions", "value": {"type": "gauge", "mode": "basic", "valueDisplayMode": "text"}}]}
panels.append({"type": "table", "title": "Hosts", "id": nid(), "datasource": PROM,
    "description": "One row per host: hardware, current load, uptime and time since the agent last reported.",
    "gridPos": {"h": 7, "w": 24, "x": 0, "y": y}, "targets": fleet_targets,
    "transformations": [
        {"id": "merge", "options": {}},
        {"id": "organize", "options": {
            "excludeByName": {"Time": True, "Value #INFO": True},
            "indexByName": {"host_name": 0, "host_type": 1, "os_description": 2, "Value #CPU": 3, "Value #MEM": 4, "Value #DISK": 5, "Value #UP": 6, "Value #SEEN": 7},
            "renameByName": {"host_name": "Host", "host_type": "Model", "os_description": "OS", "Value #CPU": "CPU busy",
                             "Value #MEM": "Memory used", "Value #DISK": "Fullest volume", "Value #UP": "Uptime", "Value #SEEN": "Last report"}}}],
    "fieldConfig": {"defaults": {"custom": {"align": "auto", "cellOptions": {"type": "auto"}}}, "overrides": [
        gauge_override("CPU busy", 0.7, 0.9), gauge_override("Memory used", 0.8, 0.95), gauge_override("Fullest volume", 0.8, 0.9),
        {"matcher": {"id": "byName", "options": "Uptime"}, "properties": [{"id": "unit", "value": "dtdurations"}]},
        {"matcher": {"id": "byName", "options": "Last report"}, "properties": [
            {"id": "unit", "value": "s"}, {"id": "decimals", "value": 0},
            {"id": "thresholds", "value": {"mode": "absolute", "steps": [{"color": "green", "value": None}, {"color": "red", "value": 180}]}},
            {"id": "custom.cellOptions", "value": {"type": "color-text"}}]}]},
    "options": {"showHeader": True, "cellHeight": "sm", "sortBy": [{"displayName": "CPU busy", "desc": True}]}})
y += 7

# ---- Device performance
row("Device performance", y); y += 1
ts("CPU busy", [target(f'1 - system_cpu_utilization_ratio{{cpu_mode="idle", {H}}}', "{{host_name}}")], 0, y, 12, "percentunit",
   "Share of CPU capacity in use (user + system), averaged over each collection interval.", maxv=1)
ts("Memory used", [target(f'system_memory_utilization_ratio{{system_memory_state="used", {H}}}', "{{host_name}}")], 12, y, 12, "percentunit",
   "Memory held by apps and the OS (macOS: app + wired + compressed). Reclaimable file cache is excluded.", maxv=1)
y += 8
ts("Memory by state", [target(f'sum by (system_memory_state) (system_memory_usage_bytes{{{H}}})', "{{system_memory_state}}")], 0, y, 12, "bytes",
   "Physical memory of the selected hosts split into used, cached (reclaimable file cache) and free.", stack=True)
bargauge("Disk space used", f'system_filesystem_utilization_ratio{{{H}}}', "{{host_name}} {{system_filesystem_mountpoint}}", 12, y, 12, "percentunit",
   "Used share of each reported volume (macOS: the Data volume and external drives).", h=8,
   thresholds=status_thresholds(0.8, 0.9), maxv=1)
y += 8
ts("Disk throughput", [target(f'sum by (host_name, disk_io_direction) (rate(system_disk_io_bytes_total{{{H}}}[$__rate_interval]))', "{{host_name}} {{disk_io_direction}}")],
   0, y, 12, "Bps", "Bytes read from and written to physical disks per second.")
ts("Network throughput", [target(f'sum by (host_name, network_io_direction) (rate(system_network_io_bytes_total{{{H}}}[$__rate_interval]))', "{{host_name}} {{network_io_direction}}")],
   12, y, 12, "Bps", "Bytes received and transmitted per second, all interfaces (including VM bridges).")
y += 8

# ---- App performance
row("App performance", y); y += 1
bargauge("Top apps by CPU", f'topk(10, endpoint_process_cpu_utilization_ratio{{{H}}})', "{{process_executable_name}} · {{host_name}}", 0, y, 12, "percentunit",
   "Share of total machine CPU capacity used by all processes of each app, averaged over the last collection interval.")
bargauge("Top apps by memory", f'topk(10, endpoint_process_memory_usage_bytes{{{H}}})', "{{process_executable_name}} · {{host_name}}", 12, y, 12, "bytes",
   "Memory of all processes of each app (macOS: physical footprint, as in Activity Monitor).")
y += 9
ts("App CPU over time (top 5)", [target(f'topk(5, endpoint_process_cpu_utilization_ratio{{{H}}})', "{{process_executable_name}} · {{host_name}}")], 0, y, 12, "percentunit",
   "The five busiest apps at each point in time.")
bargauge("Top apps by disk I/O", f'topk(10, sum by (host_name, process_executable_name) (endpoint_process_disk_throughput_bytes_per_second{{{H}}}))',
   "{{process_executable_name}} · {{host_name}}", 12, y, 12, "Bps", "Disk bytes read plus written per second by each app over the last collection interval.", h=8)
y += 8

# ---- Agent health
row("Agent health", y); y += 1
ts("Collector duration (p95)", [target(f'histogram_quantile(0.95, sum by (le, collector_name) (rate(endpoint_agent_collector_duration_seconds_bucket{{{H}}}[$__rate_interval])))', "{{collector_name}}")],
   0, y, 8, "s", "95th percentile run time of each collector.", h=7)
ts("Failed collection cycles", [target(f'sum by (host_name) (increase(endpoint_agent_collection_cycles_total{{outcome="error", {H}}}[$__rate_interval])) or sum by (host_name) (endpoint_agent_uptime_seconds{{{H}}}) * 0', "{{host_name}}")],
   8, y, 8, "none", "Cycles in which at least one collector failed or timed out. Details are in the agent logs below.", h=7, decimals=0, softmax=1)
ts("Agent CPU", [target(f'sum by (host_name) (rate(process_cpu_time_seconds_total{{job="endpoint-agent", {H}}}[$__rate_interval]))', "{{host_name}}")],
   16, y, 8, "percentunit", "CPU used by the agent process itself (1.0 = one full core).", h=7)
y += 7
panels.append({"type": "logs", "title": "Agent logs", "id": nid(), "datasource": LOKI,
    "description": "Logs from the agents, newest first. Lines written during a collection cycle link to its trace in Tempo.",
    "gridPos": {"h": 10, "w": 24, "x": 0, "y": y},
    "targets": [{"datasource": LOKI, "expr": '{service_name="endpoint-agent", host_name=~"$host", os_type=~"$os"}', "refId": "A"}],
    "options": {"showTime": True, "wrapLogMessage": True, "sortOrder": "Descending", "enableLogDetails": True, "dedupStrategy": "none"}})

def var(name, label, query):
    # No custom allValue: "All" expands to the listed values, so "All hosts" means the hosts of the
    # selected OS rather than every host.
    return {"name": name, "label": label, "type": "query", "datasource": PROM,
            "query": {"query": query, "refId": f"{name}-var", "qryType": 1},
            "definition": query, "refresh": 2, "includeAll": True, "multi": True,
            "current": {"selected": True, "text": ["All"], "value": ["$__all"]}, "sort": 1}

dashboard = {
    "uid": "endpoint-overview", "title": "Endpoint Overview",
    "description": "Device and app performance reported by the endpoint agent.",
    "tags": ["endpoint", "opentelemetry"], "timezone": "browser", "editable": True,
    "refresh": "1m", "time": {"from": "now-1h", "to": "now"}, "schemaVersion": 41, "version": 1,
    "graphTooltip": 1,
    "templating": {"list": [
        var("os", "OS", 'label_values(system_uptime_seconds, os_type)'),
        var("host", "Host", 'label_values(system_uptime_seconds{os_type=~"$os"}, host_name)'),
    ]},
    "annotations": {"list": []},
    "links": [],
    "panels": panels,
}
print(json.dumps(dashboard, indent=2))
