namespace EndpointAgent.Collectors;

/// <summary>Settings bound from "Agent:Processes".</summary>
public sealed class ProcessCollectorOptions
{
    public const string SectionName = "Agent:Processes";

    /// <summary>
    /// Applications reported per cycle: the top N by CPU plus the top N by memory. Keeps the number
    /// of metric series bounded no matter how many processes run.
    /// </summary>
    public int TopCount { get; set; } = 10;
}

/// <summary>Settings bound from "Agent:Inventory".</summary>
public sealed class InventoryCollectorOptions
{
    public const string SectionName = "Agent:Inventory";

    /// <summary>How often the machine inventory log record is repeated after startup.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);
}
