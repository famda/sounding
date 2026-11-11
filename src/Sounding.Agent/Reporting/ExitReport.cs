using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Sounding.Agent.Reporting;

public sealed class ExitReport
{
    private readonly TaskCompletionSource<ExitStatus?> _status = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ExitReport(ILogger<ExitReport> logger)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SOUNDING_EXIT_FD"), CultureInfo.InvariantCulture, out var fd) || fd < 0)
        {
            _status.SetResult(null);
            return;
        }

        new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, 1));
                _status.TrySetResult(Parse(reader.ReadLine()));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                logger.LogDebug("No exit report: {Message}", ex.Message);
                _status.TrySetResult(null);
            }
        }) { IsBackground = true, Name = "exit-report" }.Start();
    }

    public async Task<ExitStatus?> WaitAsync(TimeSpan wait)
    {
        var done = await Task.WhenAny(_status.Task, Task.Delay(wait));
        return done == _status.Task ? await _status.Task : null;
    }

    private static ExitStatus? Parse(string? line) =>
        line?.Split(' ') is [var code, var signal, var ms]
        && int.TryParse(code, CultureInfo.InvariantCulture, out var c) && int.TryParse(signal, CultureInfo.InvariantCulture, out var s)
        && long.TryParse(ms, CultureInfo.InvariantCulture, out var m)
            ? new ExitStatus(c >= 0 ? c : null, s > 0 ? s : null, m >= 0 ? m / 1000.0 : null)
            : null;
}

public sealed record ExitStatus(int? Code, int? Signal, double? StopSeconds);
