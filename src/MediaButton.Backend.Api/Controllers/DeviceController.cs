using Microsoft.AspNetCore.Mvc;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/device/{deviceId}")]
public class DeviceController : ControllerBase
{
    [HttpGet("ping")]
    public IActionResult Ping(string deviceId)
    {
        // This is what middleware authenticated:
        var authedDeviceId = HttpContext.Items["DeviceId"] as string;

        return Ok(new
        {
            routeDeviceId = deviceId,
            authenticatedAs = authedDeviceId,
            ok = authedDeviceId == deviceId
        });
    }
}
