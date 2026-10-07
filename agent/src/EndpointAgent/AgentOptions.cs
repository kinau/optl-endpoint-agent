using OpenTelemetry.Exporter;

namespace EndpointAgent;

/// <summary>Settings bound from the "Agent" configuration section.</summary>
public sealed class AgentOptions
{
    public const string SectionName = "Agent";

    public string ServiceName { get; set; } = "endpoint-agent";

    /// <summary>How often collectors run.</summary>
    public TimeSpan CollectionInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How often metrics are exported and trace/log batches are flushed.</summary>
    public TimeSpan ExportInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A single collector that runs longer than this is cancelled.</summary>
    public TimeSpan CollectorTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Names of collectors to skip, e.g. ["process"].</summary>
    public string[] DisabledCollectors { get; set; } = [];

    public OtlpSettings Otlp { get; set; } = new();

    public sealed class OtlpSettings
    {
        /// <summary>
        /// Collector base URL, e.g. http://localhost:4317 (gRPC) or http://localhost:4318 (HTTP).
        /// When unset, the SDK default applies, which honors OTEL_EXPORTER_OTLP_ENDPOINT.
        /// </summary>
        public Uri? Endpoint { get; set; }

        public OtlpExportProtocol Protocol { get; set; } = OtlpExportProtocol.Grpc;
    }
}
