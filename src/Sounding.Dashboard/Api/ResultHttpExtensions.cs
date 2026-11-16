using Sounding.Dashboard.Application.Captures;
using Sounding.Dashboard.Application.Common;

namespace Sounding.Dashboard.Api;

public static class ResultHttpExtensions
{
    public static IResult ToHttpResult<T>(this Result<T> result) =>
        result.IsSuccess ? TypedResults.Ok(result.Value) : result.ToProblem();

    // Work that was started and finishes later: 202 with where its outcome will be.
    public static IResult ToAcceptedResult<T>(this Result<T> result, Func<T, string> location) =>
        result is { IsSuccess: true, Value: { } value } ? TypedResults.Accepted(location(value), value) : result.ToProblem();

    public static IResult ToFileResult(this Result<CaptureFile> result) =>
        result is { IsSuccess: true, Value: { } file }
            ? TypedResults.File(file.Data, CaptureFile.ContentType, file.FileName)
            : result.ToProblem();

    private static IResult ToProblem<T>(this Result<T> result) => result.Status switch
    {
        ResultStatus.Invalid => TypedResults.ValidationProblem(
            result.Errors ?? new Dictionary<string, string[]>(), detail: result.Error),
        ResultStatus.NotFound => TypedResults.Problem(result.Error, statusCode: StatusCodes.Status404NotFound, title: "Not found"),
        ResultStatus.Conflict => TypedResults.Problem(result.Error, statusCode: StatusCodes.Status409Conflict, title: "Conflict"),
        ResultStatus.NotReady => TypedResults.Problem(result.Error, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Not ready"),
        _ => throw new InvalidOperationException($"Unmapped result status {result.Status}"),
    };
}

// A browser sends Origin with requests a page makes from script or a form. One from another site must not change
// state here or talk to the MCP server: the dashboard has no sign-in, so nothing else stops a page the user happens to
// have open from doing so. Requests without Origin (scripts, assistants, the dashboard's own GETs) pass.
public static class SameOrigin
{
    public static IApplicationBuilder UseSameOriginWrites(this IApplicationBuilder app) => app.Use(static async (context, next) =>
    {
        var request = context.Request;
        var guarded = request.Path.StartsWithSegments("/mcp")
            || request.Path.StartsWithSegments("/api") && !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method);
        if (guarded && request.Headers.Origin is [{ Length: > 0 } origin] && !Matches(origin, request))
        {
            await TypedResults.Problem("Requests from another site are not accepted.", statusCode: StatusCodes.Status403Forbidden, title: "Forbidden")
                .ExecuteAsync(context);
            return;
        }

        await next(context);
    });

    // Behind a reverse proxy that rewrites Host, the name the browser used arrives as X-Forwarded-Host.
    private static bool Matches(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            || request.Headers["X-Forwarded-Host"] is [{ } forwarded] && string.Equals(uri.Authority, forwarded, StringComparison.OrdinalIgnoreCase));
}
