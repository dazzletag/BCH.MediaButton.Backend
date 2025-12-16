using System.Security.Cryptography;
using System.Text;

namespace MediaButtonBackend.Auth;

public class DeviceAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IConfiguration _config;

    public DeviceAuthMiddleware(RequestDelegate next, IConfiguration config)
    {
        _next = next;
        _config = config;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only protect the device API surface
        // (Adjust this prefix to match your actual routes.)
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
        // We assume your route pattern is /api/device/{deviceId}/...
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // segments: ["api","device","{deviceId}", ...]
        if (segments.Length < 3)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Malformed device route.");
            return;
        }

        var deviceId = segments[2];

        var expectedKey = _config[$"DeviceAuth:Devices:{deviceId}"];
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
