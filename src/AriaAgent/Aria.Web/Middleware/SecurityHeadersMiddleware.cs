namespace Aria.Web.Middleware;

/// <summary>
/// Adds baseline hardening response headers to every response. Deliberately conservative: the CSP
/// only sets <c>frame-ancestors 'none'</c> (clickjacking protection) and does NOT restrict
/// script/style sources, because Blazor Server relies on its own injected scripts and a stricter
/// policy would need nonces/hashes to avoid breaking the circuit. X-Frame-Options is the legacy
/// equivalent for older browsers.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        // Set before the response starts. Use indexer assignment so we overwrite rather than append.
        context.Response.OnStarting(() =>
        {
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"]        = "DENY";
            headers["Referrer-Policy"]         = "no-referrer";
            if (!headers.ContainsKey("Content-Security-Policy"))
                headers["Content-Security-Policy"] = "frame-ancestors 'none'";
            return Task.CompletedTask;
        });
        return _next(context);
    }
}

public static class SecurityHeadersMiddlewareExtensions
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
        => app.UseMiddleware<SecurityHeadersMiddleware>();
}
