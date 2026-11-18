namespace Sounding.Dashboard.Api;

// ASP.NET Core leaves "%2F" escaped in a route value, so a key holding "/" (a Kubernetes container's
// "namespace/pod/container" id, or its "namespace/workload" name) reaches the handler escaped. This decodes it first.
public static class RouteKeys
{
    public static RouteGroupBuilder DecodeSlashes(this RouteGroupBuilder group, string parameter)
    {
        group.AddEndpointFilter(async (context, next) =>
        {
            if (context.HttpContext.Request.RouteValues[parameter] is string raw && raw.Contains("%2F", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = raw.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
                for (var i = 0; i < context.Arguments.Count; i++)
                {
                    if (context.Arguments[i] is string value && value == raw) context.Arguments[i] = decoded;
                }
            }

            return await next(context);
        });
        return group;
    }
}
