using System.Diagnostics;

namespace EndpointAgent.Collectors;

/// <summary>
/// The agent's telemetry sources. Every collector reports spans through <see cref="ActivitySource"/>
/// and creates its instruments on the meter named <see cref="SourceName"/> (via IMeterFactory).
/// </summary>
public static class AgentDiagnostics
{
    public const string SourceName = "EndpointAgent";

    public static readonly ActivitySource ActivitySource =
        new(SourceName, typeof(AgentDiagnostics).Assembly.GetName().Version?.ToString());
}
