using System.Threading.Channels;
using System.Diagnostics;
using Npgsql;

namespace Sounding.Dashboard.Live;

public sealed class FleetMonitor(
    NpgsqlDataSource dataSource,
    FleetRepository repository,
    Broadcaster<FleetView> broadcaster,
    FleetFreshness freshness,
    ILogger<FleetMonitor> logger) : BackgroundService
{
    private static readonly TimeSpan FallbackRefresh = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(150);

    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(ListenAsync(stoppingToken), RefreshAsync(stoppingToken));

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken);
                connection.Notification += (_, _) => _changed.Writer.TryWrite(true);

                await using (var listen = new NpgsqlCommand($"listen {DatabaseSchema.NotifyChannel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                logger.LogInformation("Listening for resource samples");
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Notification listener disconnected: {Message}. Retrying in {Delay}", ex.Message, ReconnectDelay);
                await Task.Delay(ReconnectDelay, stoppingToken);
            }
        }
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        var schemaReady = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            {
                wait.CancelAfter(FallbackRefresh);
                try
                {
                    await _changed.Reader.ReadAsync(wait.Token);
                    await Task.Delay(Coalesce, stoppingToken);
                    _changed.Reader.TryRead(out _);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                }
            }

            try
            {
                if (!schemaReady)
                {
                    await DatabaseSchema.EnsureAsync(dataSource, stoppingToken);
                    schemaReady = true;
                }

                var startedAt = Stopwatch.GetTimestamp();
                var fleet = await repository.GetFleetAsync(stoppingToken);
                broadcaster.Publish(new FleetView(fleet, startedAt));
                freshness.Refreshed(startedAt);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (DatabaseSchema.IsMissing(ex)) schemaReady = false;
                logger.LogWarning("Fleet refresh failed: {Message}", ex.Message);
            }
        }
    }
}
