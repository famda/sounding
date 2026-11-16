using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Application.Events;

public static class EventLevel
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
    public static readonly string[] All = [Info, Warning, Error];

    // Levels at or above the minimum, so "warning" also returns errors.
    public static bool AtLeast(string level, string minimum) => Rank(level) >= Rank(minimum);

    // The same, as the list of levels an event filter keeps: null keeps every level.
    public static string[]? AtOrAbove(string? minimum) => minimum switch
    {
        Error => [Error],
        Warning => [Warning, Error],
        _ => null,
    };

    private static int Rank(string level) => level switch
    {
        Error => 2,
        Warning => 1,
        _ => 0,
    };
}

public static class EventKind
{
    public const string Lifecycle = "lifecycle";
    public const string Oom = "oom";
    public const string Memory = "memory";
    public const string Cpu = "cpu";
    public const string Process = "process";
    public const string Port = "port";
    public const string Destination = "destination";
    public const string Gpu = "gpu";
    public static readonly string[] All = [Lifecycle, Oom, Memory, Cpu, Process, Port, Destination, Gpu];
}

public sealed class EventLevelsAttribute() : ChoicesAttribute(EventLevel.All);

public sealed class EventKindsAttribute() : ChoicesAttribute(EventKind.All);

// Without a host or container, every host in the current fleet is read. Events are rare, so the default window is an
// hour rather than the minute other windows default to.
public sealed record GetEventsQuery(
    string? HostId = null,
    string? ContainerId = null,
    int? Seconds = null,
    string? MinimumLevel = null,
    int? Limit = null,
    string? Kind = null);

// The same events with repeats folded: one entry per container, kind, level and message, with how often it happened
// and when first and last. Measurements that only differ in their figure (CPU throttling) fold by kind.
public sealed record GetEventDigestQuery(
    string? HostId = null,
    string? ContainerId = null,
    int? Seconds = null,
    string? MinimumLevel = null,
    string? Kind = null);

public sealed record EventGroup(
    DateTimeOffset Time,
    DateTimeOffset? FirstTime,
    int Count,
    string? Container,
    string? ContainerId,
    string Kind,
    string Level,
    string Message);

// Truncated: more events matched than were read (the newest MaxLimit), so Events and the counts leave the oldest out.
public sealed record EventDigest(int WindowSeconds, int Events, int Omitted, IReadOnlyList<EventGroup> Groups, string? Note = null, bool? Truncated = null);

public static class GetEventsHandler
{
    public const int DefaultSeconds = 3600;
    public const int DefaultLimit = 100;
    public const int MaxLimit = 1000;

    public static async Task<Result<IReadOnlyList<HostEvent>>> Handle(
        GetEventsQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        var read = await ReadAsync(query, skipRoutineProcesses: false, snapshot, repository, cancellationToken);
        return read.IsSuccess ? Result<IReadOnlyList<HostEvent>>.Ok(read.Value!.Page.Events) : read.AsFailure();
    }

    internal static async Task<Result<(EventPage Page, Scope Scope)>> ReadAsync(
        GetEventsQuery query, bool skipRoutineProcesses, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (Validate.OneOf(query.MinimumLevel, "level", EventLevel.All) is { } badLevel) return badLevel;
        if (Validate.OneOf(query.Kind, "kind", EventKind.All) is { } badKind) return badKind;
        if (Validate.Limit(query.Limit, MaxLimit, out var limit, DefaultLimit) is { } badLimit) return badLimit;
        if (Validate.Window(query.Seconds, out var window, DefaultSeconds) is { } badWindow) return badWindow;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;
        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, query.HostId, query.ContainerId, false, cancellationToken);
        if (failure is not null) return failure;

        var hosts = scope.Hosts(fleet);
        var hostIds = new string[hosts.Count];
        for (var i = 0; i < hostIds.Length; i++) hostIds[i] = hosts[i].HostId;
        var page = await repository.GetEventsAsync(new EventFilter(hostIds, window, limit, scope.Container?.ContainerId,
            EventLevel.AtOrAbove(query.MinimumLevel), query.Kind is { } kind ? [kind] : null, SkipRoutineProcesses: skipRoutineProcesses), cancellationToken);

        var events = page.Events as List<HostEvent> ?? [.. page.Events];
        var labels = await ContainerLabels.ResolveAsync(fleet, repository, events.Select(e => e.ContainerId).OfType<string>(), cancellationToken);
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].ContainerId is { } id && labels.TryGetValue(id, out var label)) events[i] = events[i] with { ContainerName = label.Label };
        }

        return Result<(EventPage Page, Scope Scope)>.Ok((page with { Events = events }, scope));
    }
}

public static class GetEventDigestHandler
{
    private const int MaxGroups = 40;

    public static async Task<Result<EventDigest>> Handle(
        GetEventDigestQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        // Processes that start and end are routine (a database starts one per connection) and read like crashes: they
        // are listed only when asked for by kind, and are left out before the limit so they cannot crowd others out.
        var read = await GetEventsHandler.ReadAsync(
            new GetEventsQuery(query.HostId, query.ContainerId, query.Seconds, query.MinimumLevel, GetEventsHandler.MaxLimit, query.Kind),
            skipRoutineProcesses: query.Kind is null, snapshot, repository, cancellationToken);
        if (!read.IsSuccess) return read.AsFailure();

        var ((events, truncated), scope) = read.Value;
        var groups = new Dictionary<(string? ContainerId, string Kind, string Level, string Message), (HostEvent Latest, DateTimeOffset First, int Count)>();
        foreach (var e in events)
        {
            var key = (e.ContainerId, e.Kind, e.Level, e.Kind == EventKind.Cpu ? "" : e.Message);
            groups[key] = groups.TryGetValue(key, out var group) ? (group.Latest, e.Time, group.Count + 1) : (e, e.Time, 1);
        }

        var folded = new List<EventGroup>(groups.Count);
        foreach (var (latest, first, count) in groups.Values)
        {
            folded.Add(new EventGroup(latest.Time, count > 1 ? first : null, count, latest.ContainerName, latest.ContainerId, latest.Kind, latest.Level, latest.Message));
        }

        folded.Sort(static (a, b) => b.Time.CompareTo(a.Time));
        var omitted = Math.Max(0, folded.Count - MaxGroups);
        if (omitted > 0) folded.RemoveRange(MaxGroups, omitted);

        var windowSeconds = query.Seconds ?? GetEventsHandler.DefaultSeconds;
        var notes = new List<string>(3);
        // Silence is not a stop: a container that stopped reporting has no lifecycle event, and its absence says
        // nothing about whether it still runs.
        if (scope.Container is { Status: ContainerStatus.Offline } silent)
        {
            notes.Add($"This container stopped reporting at {silent.LastSeen:u} and no stop, exit or kill was recorded: events cannot tell whether it still runs.");
        }

        if (truncated)
        {
            notes.Add($"More than {GetEventsHandler.MaxLimit} events matched: only the newest {GetEventsHandler.MaxLimit} were read, so older ones in this window are not counted. Narrow it with kind, level or fewer minutes.");
        }

        // An empty list reads as "nothing came back": the note says what was looked for, and nothing beyond it.
        if (folded.Count == 0) notes.Add($"No events{Filter(query)} in the last {windowSeconds / 60} min for the {scope.Describe()}.");
        return new EventDigest(windowSeconds, events.Count, omitted, folded, notes.Count == 0 ? null : string.Join(" ", notes), truncated ? true : null);
    }

    private static string Filter(GetEventDigestQuery query) =>
        (query.Kind is { } kind ? $" of kind {kind}" : "") + (query.MinimumLevel is { } level and not EventLevel.Info ? $" at level {level} or above" : "");
}
