namespace Sounding.Dashboard.Application.Common;

public enum ResultStatus
{
    Ok,
    Invalid,
    NotFound,
    NotReady,
    Conflict,
}

// The outcome of a query or command, independent of the transport (REST or MCP) that maps it.
public sealed class Result<T>
{
    private Result(ResultStatus status, T? value, string? error, IReadOnlyDictionary<string, string[]>? errors)
    {
        Status = status;
        Value = value;
        Error = error;
        Errors = errors;
    }

    public ResultStatus Status { get; }
    public T? Value { get; }
    public string? Error { get; }
    public IReadOnlyDictionary<string, string[]>? Errors { get; }
    public bool IsSuccess => Status == ResultStatus.Ok;

    public static Result<T> Ok(T value) => new(ResultStatus.Ok, value, null, null);

    public static Result<T> NotFound(string message) => new(ResultStatus.NotFound, default, message, null);

    public static Result<T> NotReady(string message) => new(ResultStatus.NotReady, default, message, null);

    public static Result<T> Conflict(string message) => new(ResultStatus.Conflict, default, message, null);

    public static Result<T> Invalid(string field, string message) =>
        new(ResultStatus.Invalid, default, message, new Dictionary<string, string[]> { [field] = [message] });

    public static Result<T> Failed(Failure failure) => new(failure.Status, default, failure.Message, failure.Errors);

    // This result's failure, to return it from a handler with another response type.
    public Failure AsFailure() => new(Status, Error ?? Status.ToString(), Errors);

    public static implicit operator Result<T>(T value) => Ok(value);

    public static implicit operator Result<T>(Failure failure) => Failed(failure);
}

// A failure that converts to any Result<T>, so shared checks need not know the response type.
public sealed record Failure(ResultStatus Status, string Message, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    public static Failure Invalid(string field, string message) =>
        new(ResultStatus.Invalid, message, new Dictionary<string, string[]> { [field] = [message] });

    public static Failure NotFound(string message) => new(ResultStatus.NotFound, message);
}
