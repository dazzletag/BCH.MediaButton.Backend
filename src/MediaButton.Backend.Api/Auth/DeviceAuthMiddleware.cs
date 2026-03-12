using System.Security.Cryptography;
using System.Text;
using MediaButtonBackend.Data;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Auth;

public class DeviceAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;
    private readonly IServiceScopeFactory _scopeFactory;

    public DeviceAuthMiddleware(RequestDelegate next, IConfiguration config, IServiceScopeFactory scopeFactory)
    {
        _next = next;
        _config = config;
        _scopeFactory = scopeFactory;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (!path.StartsWith("/api/device/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var headerName = _config["DeviceAuth:HeaderName"] ?? "X-DEVICE-KEY";

        if (!context.Request.Headers.TryGetValue(headerName, out var providedKey) ||
            string.IsNullOrWhiteSpace(providedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing device key.");
            return;
        }

        // Extract {device_id} from /api/device/{device_id}/...
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Malformed device route.");
            return;
        }

        var deviceId = segments[2];

        // Look up key: DB first, then config fallback (for devices not yet migrated)
        string? expectedKey = null;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var device = await db.Devices.FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            expectedKey = device?.DeviceKey;
        }

        if (string.IsNullOrWhiteSpace(expectedKey))
            expectedKey = _config[$"DeviceAuth:Devices:{deviceId}"];

        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unknown device.");
            return;
        }

        // Constant-time compare to reduce key-guessing side channels
        if (!ConstantTimeEquals(providedKey.ToString(), expectedKey))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Invalid device key.");
            return;
        }

        // Stash device id for controllers to use (trusted identity)
        context.Items["DeviceId"] = deviceId;

        await _next(context);
    }

    private static bool ConstantTimeEquals(string a, string b)
    {
        var aBytes = Encoding.UTF8.GetBytes(a);
        var bBytes = Encoding.UTF8.GetBytes(b);
        return aBytes.Length == bBytes.Length &&
               CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }
}
