using System.ComponentModel.DataAnnotations;

namespace Sounding.Dashboard.Application.Common;

public static class Validate
{
    public const int MaxIdLength = 256;
    public const int DefaultWindowSeconds = 60;
    public const int MinWindowSeconds = 10;
    public const int MaxWindowSeconds = 86400;

    public static Failure? Id(string? value, string field) =>
        string.IsNullOrWhiteSpace(value) ? Failure.Invalid(field, $"'{field}' is required.")
        : value.Length > MaxIdLength ? Failure.Invalid(field, $"'{field}' must be at most {MaxIdLength} characters.")
        : null;

    public static Failure? OptionalId(string? value, string field) => value is null ? null : Id(value, field);

    public static Failure? Window(int? seconds, out TimeSpan window, int defaultSeconds = DefaultWindowSeconds)
    {
        var value = seconds ?? defaultSeconds;
        window = TimeSpan.FromSeconds(value);
        return value is < MinWindowSeconds or > MaxWindowSeconds
            ? Failure.Invalid("seconds", $"'seconds' must be between {MinWindowSeconds} and {MaxWindowSeconds}.")
            : null;
    }

    public static Failure? Page(int? page, int? pageSize, out PageRequest request)
    {
        request = new PageRequest(page ?? 1, pageSize ?? PageRequest.DefaultPageSize);
        return request.Page < 1 ? Failure.Invalid("page", "'page' must be 1 or more.")
            : request.PageSize is < 1 or > PageRequest.MaxPageSize
                ? Failure.Invalid("pageSize", $"'pageSize' must be between 1 and {PageRequest.MaxPageSize}.")
            : null;
    }

    public static Failure? Limit(int? limit, int max, out int value, int defaultLimit = 20)
    {
        value = limit ?? Math.Min(defaultLimit, max);
        return value is < 1 || value > max ? Failure.Invalid("limit", $"'limit' must be between 1 and {max}.") : null;
    }

    public static Failure? OneOf(string? value, string field, IReadOnlyCollection<string> allowed) =>
        value is null || allowed.Contains(value) ? null
            : Failure.Invalid(field, $"'{field}' must be one of: {string.Join(", ", allowed)}.");
}

// The values a text parameter accepts, declared once: the handlers validate against Values, and the attribute puts
// the same list into the OpenAPI document and the MCP tool schemas as an enum.
[AttributeUsage(AttributeTargets.Parameter)]
public class ChoicesAttribute(params string[] values) : AllowedValuesAttribute([.. values])
{
    public string[] Choices { get; } = values;
}
