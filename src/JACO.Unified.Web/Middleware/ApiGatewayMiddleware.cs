using System.Diagnostics;
using System.Text;
using System.Text.Json;
using JACO.Unified.Core.Models;
using JACO.Unified.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace JACO.Unified.Web.Middleware;

// Everything an external call to /api/** needs before it reaches a controller action:
// the master enable/disable switch (ApiSettings), API-key authentication against
// ApiClients, and a full request/response audit trail (ApiRequestLog) -- including calls
// that never got past authentication, since a rejected/forged attempt is exactly what a
// security review needs visibility into. One place for all of it, rather than an action
// filter (auth) plus a second middleware (logging) that could drift out of sync.
public sealed class ApiGatewayMiddleware(RequestDelegate next, ILogger<ApiGatewayMiddleware> logger)
{
    const int MaxLoggedBodyLength = 8000;

    public async Task InvokeAsync(HttpContext context, UnifiedDbContext db)
    {
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await next(context);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var settings = await db.ApiSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1);

        context.Request.EnableBuffering();
        var requestBody = "";
        if (context.Request.ContentLength is > 0)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
            requestBody = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0;
        }

        ApiClient? client;
        string responseBody;
        int statusCode;

        if (settings is not { Enabled: true })
        {
            statusCode = StatusCodes.Status503ServiceUnavailable;
            responseBody = await WriteJsonErrorAsync(context, statusCode, "The external API is currently disabled.");
            client = null;
        }
        else
        {
            var providedKey = context.Request.Headers["X-Api-Key"].FirstOrDefault();
            client = string.IsNullOrEmpty(providedKey) ? null : await FindClientAsync(db, providedKey);

            if (client is null)
            {
                statusCode = StatusCodes.Status401Unauthorized;
                responseBody = await WriteJsonErrorAsync(context, statusCode, "Missing, invalid, or inactive API key.");
            }
            else
            {
                client.LastUsedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
                context.Items["ApiClient"] = client;

                var originalBody = context.Response.Body;
                await using var buffer = new MemoryStream();
                context.Response.Body = buffer;
                try
                {
                    await next(context);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unhandled exception handling external API call {Method} {Path}", context.Request.Method, context.Request.Path);
                    buffer.SetLength(0);
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    context.Response.ContentType = "application/json";
                    await buffer.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = "Internal server error." })));
                }
                finally
                {
                    buffer.Position = 0;
                    responseBody = await new StreamReader(buffer).ReadToEndAsync();
                    buffer.Position = 0;
                    await buffer.CopyToAsync(originalBody);
                    context.Response.Body = originalBody;
                }
                statusCode = context.Response.StatusCode;
            }
        }

        stopwatch.Stop();

        if (settings?.LogRequests ?? true)
        {
            db.ApiRequestLog.Add(new ApiRequestLog
            {
                ApiClientId = client?.Id,
                ClientName = client?.Name,
                Method = context.Request.Method,
                Path = context.Request.Path.Value ?? "",
                QueryString = context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
                RequestBody = Truncate(requestBody),
                StatusCode = statusCode,
                ResponseBody = Truncate(responseBody),
                RemoteIp = context.Connection.RemoteIpAddress?.ToString(),
                DurationMs = (int)stopwatch.ElapsedMilliseconds,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
    }

    static async Task<ApiClient?> FindClientAsync(UnifiedDbContext db, string providedKey)
    {
        var prefix = providedKey.Length >= 16 ? providedKey[..16] : providedKey;
        var candidates = await db.ApiClients.Where(c => c.KeyPrefix == prefix && c.Active).ToListAsync();
        return candidates.FirstOrDefault(c => ApiKeyService.Verify(providedKey, c.KeyHash));
    }

    static async Task<string> WriteJsonErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { error = message });
        await context.Response.WriteAsync(json);
        return json;
    }

    // Never expected to be huge (this is a JSON API, not a file upload endpoint) -- capped
    // defensively so one oversized payload can't bloat ApiRequestLog indefinitely.
    static string? Truncate(string? s) => string.IsNullOrEmpty(s) || s.Length <= MaxLoggedBodyLength ? s : s[..MaxLoggedBodyLength] + "...(truncated)";
}
