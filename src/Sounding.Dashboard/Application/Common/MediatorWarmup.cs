using Sounding.Dashboard.Application.Hosts;
using Wolverine;

namespace Sounding.Dashboard.Application.Common;

// Wolverine compiles a handler's adapter on its first message, and the very first compilation also loads the compiler
// (most of a second). One cheap query after start pays that before a person or an assistant asks anything.
public sealed class MediatorWarmup(IServiceProvider services, ILogger<MediatorWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync<Result<PaginatedList<HostOverview>>>(new GetHostsQuery(), stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "The query handlers could not be warmed up; the first request compiles them instead");
        }
    }
}
