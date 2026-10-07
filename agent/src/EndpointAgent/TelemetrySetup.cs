using System.Reflection;
using EndpointAgent.Collectors;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace EndpointAgent;

public static class TelemetrySetup
{
    /// <summary>
    /// Wires the OpenTelemetry SDK: resource attributes, the agent's own source and meter,
    /// and OTLP exporters for metrics, traces and logs that report every ExportInterval.
    /// </summary>
    public static IHostApplicationBuilder AddAgentTelemetry(
        this IHostApplicationBuilder builder,
        AgentOptions options,
        IEnumerable<KeyValuePair<string, object>> extraResourceAttributes)
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        var exportIntervalMs = (int)options.ExportInterval.TotalMilliseconds;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                // A stable instance id keeps Prometheus series continuous across agent restarts.
                .AddService(options.ServiceName, serviceVersion: version, serviceInstanceId: Environment.MachineName)
                .AddHostDetector()
                .AddOperatingSystemDetector()
                .AddAttributes(extraResourceAttributes))
            .WithMetrics(metrics => metrics
                .AddMeter(AgentDiagnostics.SourceName)
                .AddRuntimeInstrumentation()
                .AddProcessInstrumentation()
                .AddOtlpExporter((exporter, reader) =>
                {
                    ConfigureExporter(exporter, options.Otlp, "v1/metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = exportIntervalMs;
                    // Prometheus's OTLP receiver expects cumulative temporality.
                    reader.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative;
                }))
            .WithTracing(tracing => tracing
                .AddSource(AgentDiagnostics.SourceName)
                .AddOtlpExporter(exporter =>
                {
                    ConfigureExporter(exporter, options.Otlp, "v1/traces");
                    exporter.BatchExportProcessorOptions.ScheduledDelayMilliseconds = exportIntervalMs;
                }))
            .WithLogging(
                logging => logging.AddOtlpExporter((exporter, processor) =>
                {
                    ConfigureExporter(exporter, options.Otlp, "v1/logs");
                    processor.BatchExportProcessorOptions.ScheduledDelayMilliseconds = exportIntervalMs;
                }),
                logOptions =>
                {
                    logOptions.IncludeFormattedMessage = true;
                    logOptions.IncludeScopes = true;
                });

        return builder;
    }

    private static void ConfigureExporter(OtlpExporterOptions exporter, AgentOptions.OtlpSettings settings, string httpSignalPath)
    {
        exporter.Protocol = settings.Protocol;
        if (settings.Endpoint is null)
        {
            return;
        }

        // A programmatic HTTP endpoint is used as-is, so the per-signal path must be appended here.
        exporter.Endpoint = settings.Protocol == OtlpExportProtocol.HttpProtobuf
            ? new Uri(settings.Endpoint, httpSignalPath)
            : settings.Endpoint;
    }
}
