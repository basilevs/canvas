namespace Canvas.Middleware;

/// <summary>
/// Rejects WebSocket/negotiate requests to /hub/** whose Origin header does not
/// match the app's own scheme+host. Defense-in-depth against cross-site WebSocket
/// hijacking (CSWSH); the primary protection is SameSite=Strict on the identity
/// cookie. Non-browser clients that send no Origin header are admitted.
/// </summary>
public sealed class HubOriginValidationMiddleware
{
    private readonly RequestDelegate _next;

    public HubOriginValidationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/hub", StringComparison.OrdinalIgnoreCase))
        {
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin))
            {
                var expectedOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
                if (!string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }
            }
        }

        await _next(context);
    }
}
