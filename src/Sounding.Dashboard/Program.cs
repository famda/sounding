using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "SOUNDING_");

builder.Services.Configure<DashboardOptions>(builder.Configuration);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration["CONNECTION_STRING"]
    ?? throw new InvalidOperationException("SOUNDING_CONNECTION_STRING is not configured")));
builder.Services.AddSingleton<FleetRepository>();
builder.Services.AddSingleton<Broadcaster<FleetView>>();
builder.Services.AddSingleton<FleetFreshness>();
builder.Services.AddHostedService<FleetMonitor>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

api.MapGet("/fleet/stream", (string? hosts, string? containers, Broadcaster<FleetView> fleet, CancellationToken cancellationToken) =>
    TypedResults.ServerSentEvents(ForHosts(fleet.Subscribe(cancellationToken), HostIds(hosts), HostIds(containers)), eventType: "fleet"));

api.MapGet("/fleet/history", async (int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetFleetHistoryAsync(Window(seconds), cancellationToken)));

api.MapGet("/hosts/{hostId}/history", async (string hostId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostHistoryAsync(hostId, Window(seconds), cancellationToken)));

api.MapGet("/containers/{containerId}/history", async (string containerId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetContainerHistoryAsync(containerId, Window(seconds), cancellationToken)));

api.MapGet("/hosts/{hostId}/detail", async (string hostId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostDetailHistoryAsync(hostId, Window(seconds), cancellationToken)));

api.MapGet("/containers/{containerId}/detail", async (string containerId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetContainerDetailHistoryAsync(containerId, Window(seconds), cancellationToken)));

api.MapGet("/hosts/{hostId}/processes", async (string hostId, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostProcessesAsync(hostId, cancellationToken)));

api.MapGet("/hosts/{hostId}/processes/window", async (string hostId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostProcessWindowAsync(hostId, Window(seconds), cancellationToken)));

api.MapGet("/hosts/{hostId}/processes/at", async (string hostId, long t, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostProcessesAtAsync(hostId, DateTimeOffset.FromUnixTimeMilliseconds(t), cancellationToken)));

api.MapGet("/hosts/{hostId}/network", async (string hostId, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetNetworkAsync(hostId, cancellationToken)));

api.MapGet("/network", async (FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetNetworkAsync(null, cancellationToken)));

api.MapGet("/hosts/{hostId}/events", async (string hostId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetHostEventsAsync(hostId, Window(seconds), cancellationToken)));

api.MapPost("/containers/{containerId}/captures", async (string containerId, int? seconds, FleetRepository repository, CancellationToken cancellationToken) =>
    await repository.RequestCaptureAsync(containerId, Math.Clamp(seconds ?? 10, 1, 60), cancellationToken) is { } id
        ? Results.Ok(id)
        : Results.Conflict("The container isn't running, or a capture is already in progress."));

api.MapGet("/containers/{containerId}/captures", async (string containerId, FleetRepository repository, CancellationToken cancellationToken) =>
    Results.Ok(await repository.GetCapturesAsync(containerId, cancellationToken)));

api.MapGet("/captures/{id:long}", async (long id, FleetRepository repository, CancellationToken cancellationToken) =>
    await repository.GetCaptureFileAsync(id, cancellationToken) is { } capture
        ? Results.File(capture.Data, "application/vnd.tcpdump.pcap", $"{capture.ContainerId}-{capture.At:yyyyMMdd-HHmmss}.pcap")
        : Results.NotFound());

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/readyz", (FleetFreshness freshness) => freshness.IsReady
    ? Results.Ok("ready") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();

static TimeSpan Window(int? seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds ?? 60, 10, 86400));

static IReadOnlySet<string> HostIds(string? hosts) =>
    (hosts ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(32).ToHashSet();

static async IAsyncEnumerable<FleetUpdate> ForHosts(IAsyncEnumerable<FleetView> views, IReadOnlySet<string> hostIds, IReadOnlySet<string> containerIds)
{
    await foreach (var view in views)
    {
        yield return view.ForHosts(hostIds, containerIds);
    }
}
