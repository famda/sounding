using Microsoft.Extensions.Options;
using Npgsql;

// No appsettings file or reload watchers: configuration comes from the environment only.
var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { Args = args });
builder.Configuration.AddEnvironmentVariables(prefix: "SOUNDING_");
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
builder.Logging.AddConsole();

builder.Services.Configure<AgentOptions>(builder.Configuration);

builder.Services.AddSingleton(sp => HostIdentity.Create(sp.GetRequiredService<IOptions<AgentOptions>>().Value));
builder.Services.AddSingleton(_ =>
{
    var database = new NpgsqlSlimDataSourceBuilder(builder.Configuration["CONNECTION_STRING"]
        ?? throw new InvalidOperationException("SOUNDING_CONNECTION_STRING is not configured"));
    // The same few statements run every tick: prepared once, then only their parameters are sent.
    if (database.ConnectionStringBuilder.MaxAutoPrepare == 0)
    {
        database.ConnectionStringBuilder.MaxAutoPrepare = 16;
    }

    return database.Build();
});

builder.Services.AddSingleton<CpuSampler>();
builder.Services.AddSingleton<MemorySampler>();
builder.Services.AddSingleton<DiskSampler>();
builder.Services.AddSingleton<NetworkSampler>();
builder.Services.AddSingleton<GpuSampler>();
builder.Services.AddSingleton<SystemSampler>();
builder.Services.AddSingleton<TemperatureSampler>();
builder.Services.AddSingleton<HostSampler>();
builder.Services.AddSingleton<CgroupSampler>();
builder.Services.AddSingleton<StorageSampler>();
builder.Services.AddSingleton<GpuMeterSampler>();
builder.Services.AddSingleton<ProcessSampler>();
builder.Services.AddSingleton<TrafficSampler>();
builder.Services.AddSingleton<LogSampler>();
builder.Services.AddSingleton<AgentConnection>();
builder.Services.AddSingleton<HostLeadership>();
builder.Services.AddSingleton<AgentRepository>();
builder.Services.AddSingleton<ExitReport>();

builder.Services.AddHostedService<AgentService>();

builder.Build().Run();
