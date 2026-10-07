using EndpointAgent;
using EndpointAgent.Collectors;

// Load appsettings.json from the agent's folder, not the working directory: services
// (Windows Service, launchd) start with a working directory unrelated to the install path.
// "--check" is a flag, not a setting: remove it so the configuration parser does not consume
// the next argument as its value.
var selfCheck = args.Contains("--check");
var settingArgs = args.Where(a => a != "--check").ToArray();

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = settingArgs,
    ContentRootPath = AppContext.BaseDirectory,
});

var section = builder.Configuration.GetSection(AgentOptions.SectionName);
var agentOptions = section.Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddOptions<AgentOptions>()
    .Bind(section)
    .Validate(o => o.CollectionInterval > TimeSpan.Zero, "Agent:CollectionInterval must be positive.")
    .Validate(o => o.ExportInterval > TimeSpan.Zero, "Agent:ExportInterval must be positive.")
    .Validate(o => o.CollectorTimeout > TimeSpan.Zero, "Agent:CollectorTimeout must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<ProcessCollectorOptions>()
    .Bind(builder.Configuration.GetSection(ProcessCollectorOptions.SectionName))
    .Validate(o => o.TopCount > 0, "Agent:Processes:TopCount must be positive.")
    .ValidateOnStart();
builder.Services.AddOptions<InventoryCollectorOptions>()
    .Bind(builder.Configuration.GetSection(InventoryCollectorOptions.SectionName));

if (selfCheck)
{
    return await SelfCheck.RunAsync(agentOptions, Console.Out);
}

var inventory = PlatformServices.AddCollectors(builder.Services);
builder.AddAgentTelemetry(agentOptions, PlatformServices.ResourceAttributes(inventory));

builder.Services.AddSingleton<CollectionRunner>();
builder.Services.AddHostedService<CollectionWorker>();

await builder.Build().RunAsync();
return 0;
