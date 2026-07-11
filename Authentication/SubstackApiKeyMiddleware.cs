using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace RSS_API.Authentication;

public sealed class SubstackApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiKeyOptions _options;

    public SubstackApiKeyMiddleware(RequestDelegate next, IOptions<ApiKeyOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/substack", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.Key))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "The Substack API key is not configured." });
            return;
        }

        if (!context.Request.Headers.TryGetValue(_options.HeaderName, out var providedKeys) ||
            providedKeys.Count != 1 ||
            !KeysMatch(providedKeys[0], _options.Key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "ApiKey";
            await context.Response.WriteAsJsonAsync(new { error = "A valid API key is required." });
            return;
        }

        await _next(context);
    }

    private static bool KeysMatch(string? providedKey, string expectedKey)
    {
        if (providedKey is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(providedKey),
            Encoding.UTF8.GetBytes(expectedKey));
    }
}
