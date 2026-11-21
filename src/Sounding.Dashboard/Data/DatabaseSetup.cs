using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Dashboard.Data;

// Whether the dashboard has brought the database to its schema: nothing reads it before.
public sealed class DatabaseState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Ready => _ready.Task;

    public void MarkReady() => _ready.TrySetResult();
}

// Migrates the database when the dashboard starts (waiting for it while it's unreachable), grants the agents' role,
// then runs the maintenance every minute. A database the dashboard must not run against stops it.
public sealed class DatabaseSetup(
    NpgsqlDataSource dataSource,
    DatabaseMaintenance maintenance,
    DatabaseState state,
    IOptions<DashboardOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<DatabaseSetup> logger) : BackgroundService
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                await DatabaseSchema.MigrateAsync(dataSource, stoppingToken);
                break;
            }
            catch (SchemaException ex)
            {
                logger.LogCritical("{Message}", ex.Message);
                Environment.ExitCode = 1;
                lifetime.StopApplication();
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Database not ready: {Message}. Retrying in {Delay}", ex.Message, Retry);
                await Task.Delay(Retry, stoppingToken);
            }
        }

        logger.LogInformation("Database schema at version {Version}", DatabaseSchema.Version);
        if (options.Value.AgentRole is { Length: > 0 } role)
        {
            try
            {
                await DatabaseSchema.GrantAgentAsync(dataSource, role, stoppingToken);
                logger.LogInformation("Granted the agents' role {Role} what agents need", role);
            }
            catch (PostgresException ex)
            {
                logger.LogError("Could not grant the agents' role {Role}: {Message}", role, ex.MessageText);
            }
        }

        using var timer = new PeriodicTimer(Every);
        do
        {
            try
            {
                await maintenance.RunAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Database maintenance failed: {Message}", ex.Message);
            }

            state.MarkReady();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
