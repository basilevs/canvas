using System.Security.Cryptography;

namespace Canvas.Middleware;

public sealed class UserIdentityMiddleware
{
    // The cookie stores a high-entropy secret known only to the client. The
    // user's public identity is a one-way hash of that secret, so the
    // identifiers we broadcast to other clients (rosters, cursors, stroke
    // authorship) and return from the history API can never be replayed into
    // this cookie to impersonate someone: recovering the secret from the public
    // id would require inverting SHA-256. The secret itself is never exposed.
    private const string CookieName = "uid";
    private const string UserIdItemKey = "UserId";
    private const int SecretByteLength = 32;
    private const string ContentSecurityPolicy =
        "default-src 'self'; " +
        "script-src 'self' 'sha384-/taWmisziXYpcfnYsumSUmNaiMvG/fF/OJOUCLnqCIYTrpOZy7WbFF6FfIxwOrfL'; " +
        "style-src 'self' https://cdn.jsdelivr.net/npm/@picocss/pico@2.1.1/css/; " +
        "connect-src 'self'; " +
        "img-src 'self' data:; " +
        "object-src 'none'; " +
        "base-uri 'none'; " +
        "form-action 'self'; " +
        "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public UserIdentityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!TryDecodeSecret(context.Request.Cookies[CookieName], out var secret))
        {
            secret = RandomNumberGenerator.GetBytes(SecretByteLength);
            context.Response.Cookies.Append(
                CookieName,
                Convert.ToHexString(secret),
                new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Path = "/",
                    SameSite = SameSiteMode.Strict,
                    Secure = context.Request.IsHttps
                });
        }

        context.Items[UserIdItemKey] = DeriveUserId(secret);
        context.Response.Headers["Content-Security-Policy"] = ContentSecurityPolicy;
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";

        await _next(context);
    }

    private static bool TryDecodeSecret(string? cookieValue, out byte[] secret)
    {
        secret = [];
        if (string.IsNullOrEmpty(cookieValue) || cookieValue.Length != SecretByteLength * 2)
        {
            return false;
        }

        try
        {
            secret = Convert.FromHexString(cookieValue);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string DeriveUserId(byte[] secret)
    {
        // SHA-256 of the secret, truncated to a 128-bit GUID. The hash is
        // preimage-resistant, so the broadcast id cannot be turned back into the
        // cookie secret that authenticates the user.
        var hash = SHA256.HashData(secret);
        return new Guid(hash.AsSpan(0, 16)).ToString("D");
    }
}
