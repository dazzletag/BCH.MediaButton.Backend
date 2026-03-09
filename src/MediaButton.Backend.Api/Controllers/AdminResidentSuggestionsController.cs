using System.Text.Json.Nodes;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MediaButtonBackend.Controllers;

[ApiController]
[Route("api/admin/residents")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminResidentSuggestionsController(
    MobizioService mobizio,
    IConfiguration configuration,
    IHttpClientFactory httpFactory) : ControllerBase
{
    [HttpPost("{resident}/ai-suggestions")]
    public async Task<IActionResult> GenerateSuggestions([FromRoute] string resident)
    {
        var flowUrl = configuration["PlaylistFlow:Url"];
        if (string.IsNullOrWhiteSpace(flowUrl))
            return StatusCode(503, "Playlist flow URL not configured.");

        ResidentMobizioProfile? profile;
        try
        {
            profile = await mobizio.GetResidentProfileAsync(resident);
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Failed to fetch resident profile from Mobizio: {ex.Message}");
        }

        if (profile is null)
            return NotFound($"Resident '{resident}' not found in Mobizio.");

        var payload = new
        {
            residentName = profile.Name,
            surveyRow = profile.FormFields,
            thisIsMeProfile = profile.FormFields,
            demographics = new
            {
                dob = profile.Dob,
                gender = profile.Gender,
                age_hint = "Use dob to infer approximate age and life era."
            }
        };

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(120);

        HttpResponseMessage flowResp;
        try
        {
            flowResp = await http.PostAsJsonAsync(flowUrl, payload);
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Playlist flow request failed: {ex.Message}");
        }

        if (!flowResp.IsSuccessStatusCode)
        {
            var body = await flowResp.Content.ReadAsStringAsync();
            return StatusCode(502, $"Playlist flow returned {(int)flowResp.StatusCode}: {body}");
        }

        var result = await flowResp.Content.ReadFromJsonAsync<JsonObject>();
        var items = (result?["playlist"]?.AsArray() ?? [])
            .Select(i => i?.GetValue<string>())
            .OfType<string>()
            .ToList();

        return Ok(new { items });
    }
}
