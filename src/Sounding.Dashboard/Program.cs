using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using Npgsql;
using Sounding.Dashboard.Api.Endpoints;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Network;
using Sounding.Dashboard.Mcp;
using Wolverine;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables(prefix: "SOUNDING_");

builder.Services.Configure<DashboardOptions>(builder.Configuration);
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(
    builder.Configuration["CONNECTION_STRING"]
    ?? throw new InvalidOperationException("SOUNDING_CONNECTION_STRING is not configured")));
builder.Services.AddSingleton<FleetRepository>();
builder.Services.AddSingleton<Broadcaster<FleetView>>();
builder.Services.AddSingleton<FleetSnapshot>();
builder.Services.AddSingleton<NetworkReader>();
builder.Services.AddSingleton(new SoundingDatabase(builder.Configuration["CONNECTION_STRING"]));
builder.Services.AddSingleton<FleetFreshness>();
builder.Services.AddSingleton<DatabaseState>();
builder.Services.AddSingleton<DatabaseMaintenance>();
builder.Services.AddHostedService<DatabaseSetup>();
builder.Services.AddHostedService<FleetMonitor>();

// Wolverine only dispatches queries and commands in process: no transports, inbox, outbox or node persistence.
builder.Services.AddWolverine(options => options.Durability.Mode = DurabilityMode.MediatorOnly);
builder.Services.AddHostedService<MediatorWarmup>();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemExceptionHandler>();
// Parameters that cannot be bound throw, so the exception handler answers with Problem Details instead of an empty 400.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi(options => options.AddOperationTransformer(EndpointMetadata.ListChoices));
builder.Services.AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "sounding",
            Title = "Sounding",
            Version = "1.0.0",
            Description = "Container and host resource monitoring: diagnose problems and check resource usage.",
            Icons = SoundingIcon.From(builder.Environment.WebRootFileProvider),
        };
        options.ServerInstructions = SoundingTools.Instructions;
    })
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<SoundingTools>()
    .WithPrompts([typeof(SoundingPrompts)])
    .WithResources<SoundingResources>()
    .WithCompleteHandler(SoundingCompletions.Complete);
builder.Services.PostConfigure<McpServerOptions>(PortableSchemas.Apply);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSameOriginWrites();
// Empty error responses (unknown route, wrong method) get a Problem Details body for clients that accept JSON.
app.UseStatusCodePages();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGroup("/api/v1")
    .MapFleetEndpoints()
    .MapHostEndpoints()
    .MapContainerEndpoints();

app.MapOpenApi("/openapi/{documentName}.json");
// The API's own reference, served from the dashboard with no outside assets: it works on a private network.
app.UseSwaggerUI(options =>
{
    options.RoutePrefix = "api/docs";
    options.SwaggerEndpoint("/openapi/v1.json", "Sounding API v1");
    options.DocumentTitle = "Sounding API";
});
app.MapMcp("/mcp");

app.MapGet("/healthz", () => Results.Ok("ok"));
app.MapGet("/readyz", (FleetFreshness freshness) => freshness.IsReady
    ? Results.Ok("ready") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.Run();
