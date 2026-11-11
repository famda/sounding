using Npgsql;

namespace Sounding.Agent.Reporting;

// The agent's one database session: every write and the host role locks go through it, so each agent costs
// the database a single backend. A failed statement leaves it open; once broken it is reopened on the next use
// (and the locks are gone). It also hears the dashboard's capture requests (LISTEN, from RegisterAsync).
public sealed class AgentConnection(NpgsqlDataSource dataSource, HostIdentity identity) : IAsyncDisposable
{
    private NpgsqlConnection? _connection;
    private volatile bool _captureRequested;

    public bool IsOpen => _connection is { State: System.Data.ConnectionState.Open };
    public long Generation { get; private set; }

    public async Task<NpgsqlConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { State: System.Data.ConnectionState.Open })
        {
            return _connection;
        }

        await DisposeAsync();
        _connection = await dataSource.OpenConnectionAsync(cancellationToken);
        Generation++;
        _connection.Notification += (_, e) =>
        {
            if (e.Channel == DatabaseSchema.CaptureChannel && e.Payload == identity.ContainerId)
            {
                _captureRequested = true;
            }
        };
        return _connection;
    }

    // Whether the dashboard asked this container for a capture since the last call.
    public bool TakeCaptureRequest()
    {
        var requested = _captureRequested;
        _captureRequested = false;
        return requested;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}
