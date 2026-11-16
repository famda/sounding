using Microsoft.AspNetCore.Diagnostics;

namespace Sounding.Dashboard.Api;

// Every failure leaves the API as Problem Details: a parameter that cannot be read (a number that is not one, a time
// that is not a time) is a 400 that says which, and anything unexpected is a 500 without internals.
public sealed class ProblemExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception is BadHttpRequestException bad
            ? (bad.StatusCode, "Bad request", bad.Message)
            : (StatusCodes.Status500InternalServerError, "Unexpected error", "The request failed unexpectedly. The dashboard's log has the details.");

        httpContext.Response.StatusCode = status;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = { Status = status, Title = title, Detail = detail },
        });
    }
}
