namespace Canvas.Middleware;

public sealed class UserIdentityMiddleware
{
    private const string CookieName = "uid";
    private const string UserIdItemKey = "UserId";
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' https://cdnjs.cloudflare.com; " +
        "style-src 'self' https://cdn.jsdelivr.net; " +
        "connect-src 'self'; " +
        "img-src 'self' data:; " +
        "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public UserIdentityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var userId = context.Request.Cookies[CookieName];
        if (!Guid.TryParse(userId, out var parsedUserId))
        {
            parsedUserId = Guid.NewGuid();
            context.Response.Cookies.Append(
                CookieName,
                parsedUserId.ToString(),
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Strict,
                    Secure = context.Request.IsHttps
                });
        }

        context.Items[UserIdItemKey] = parsedUserId.ToString();
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await _next(context);
    }
}
