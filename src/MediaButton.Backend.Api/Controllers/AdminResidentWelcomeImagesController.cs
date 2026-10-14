using System.Text.Json.Nodes;
using MediaButtonBackend.Data;
using MediaButtonBackend.Models;
using MediaButtonBackend.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MediaButtonBackend.Controllers;

/// <summary>
/// Generate and approve the picture on a resident's welcome screen.
///
/// The device used to make this image itself, on first use, which meant nobody
/// ever saw it before the resident did. For someone who may be unable to tell
/// us they find a picture upsetting, that is the wrong way round. So pictures
/// are generated here, a person chooses one, and only the chosen one reaches a
/// device — where it replaces the plain lettered card.
///
/// The image model is reached through a configured flow, the same arrangement
/// the AI playlist suggestions already use. That keeps the choice of model in
/// the flow, where it can be changed without a deployment.
/// </summary>
[ApiController]
[Route("api/admin/residents")]
[Authorize(Policy = "AdminOrRelative")]
public class AdminResidentWelcomeImagesController(
    AppDbContext db,
    MobizioService mobizio,
    StorageSasService sas,
    UserAccessService access,
    IConfiguration configuration,
    IHttpClientFactory httpFactory,
    ILogger<AdminResidentWelcomeImagesController> logger) : ControllerBase
{
    private string PhotosContainer => configuration["Storage:ContainerPhotos"] ?? "photos";

    public record WelcomeImageView(
        Guid Id,
        string Status,
        string? Subject,
        string? Prompt,
        DateTimeOffset GeneratedAtUtc,
        string? ApprovedBy,
        string Url,
        DateTimeOffset UrlExpiresAt);

    // ---------------------------------------------------------------- list

    [HttpGet("{resident}/welcome-images")]
    public async Task<IActionResult> List([FromRoute] string resident)
    {
        if (!await access.CanAccessResidentAsync(User, resident))
            return Forbid();

        var rows = await db.ResidentWelcomeImages
            .Where(w => w.Resident == resident && w.Status != WelcomeImageStatus.Rejected)
            .OrderByDescending(w => w.Status == WelcomeImageStatus.Approved)
            .ThenByDescending(w => w.GeneratedAtUtc)
            .ToListAsync();

        return Ok(new { items = rows.Select(View).ToList() });
    }

    // ------------------------------------------------------------ generate

    [HttpPost("{resident}/welcome-image/generate")]
    public async Task<IActionResult> Generate([FromRoute] string resident, [FromQuery] int count = 3)
    {
        if (!await access.CanAccessResidentAsync(User, resident))
            return Forbid();

        var flowUrl = configuration["WelcomeImageFlow:Url"];
        if (string.IsNullOrWhiteSpace(flowUrl))
            return StatusCode(503,
                "No image flow configured. Set WelcomeImageFlow:Url to a flow that takes "
                + "{ residentName, prompt, count } and returns { images: [ { base64 } ] }.");

        count = Math.Clamp(count, 1, 4);

        // The prompt is built from the care record, but the record itself is not
        // sent on: it contains diagnoses, bereavements and safeguarding notes,
        // none of which belong in a picture or in a third-party service.
        ResidentMobizioProfile? profile = null;
        try
        {
            profile = await mobizio.GetResidentProfileAsync(resident);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read This is Me for {Resident}", resident);
        }

        var built = WelcomeImagePrompt.Build(resident, profile?.FormFields);

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(180);

        JsonObject? result;
        try
        {
            var resp = await http.PostAsJsonAsync(flowUrl, new
            {
                residentName = resident,
                prompt = built.Prompt,
                subject = built.Subject,
                count,
                aspectRatio = "16:9",
            });
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return StatusCode(502, $"Image flow returned {(int)resp.StatusCode}: {Trim(body)}");
            }
            result = await resp.Content.ReadFromJsonAsync<JsonObject>();
        }
        catch (Exception ex)
        {
            return StatusCode(502, $"Image flow request failed: {ex.Message}");
        }

        var pictures = ExtractImages(result);
        if (pictures.Count == 0)
            return StatusCode(502,
                "The image flow answered but contained no image. Expected "
                + "{ images: [ { base64 } ] } or { images: [ \"<base64>\" ] }.");

        var who = access.GetEmail(User);
        var saved = new List<ResidentWelcomeImage>();
        foreach (var bytes in pictures.Take(count))
        {
            var blobPath = $"welcome/{Slug(resident)}/{Guid.NewGuid():N}.png";
            try
            {
                using var stream = new MemoryStream(bytes);
                await sas.UploadBlobAsync(PhotosContainer, blobPath, stream, "image/png");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not store a welcome image for {Resident}", resident);
                continue;
            }

            var row = new ResidentWelcomeImage
            {
                Resident = resident,
                BlobPath = blobPath,
                ContentType = "image/png",
                Status = WelcomeImageStatus.Candidate,
                Prompt = built.Prompt,
                Subject = built.Subject,
                GeneratedBy = who,
            };
            db.ResidentWelcomeImages.Add(row);
            saved.Add(row);
        }

        if (saved.Count == 0)
            return StatusCode(502, "Images were generated but none could be stored.");

        await db.SaveChangesAsync();

        return Ok(new
        {
            items = saved.Select(View).ToList(),
            basis = built.Basis,
            prompt = built.Prompt,
        });
    }

    // ------------------------------------------------------------- approve

    [HttpPost("{resident}/welcome-image/{id:guid}/approve")]
    public async Task<IActionResult> Approve([FromRoute] string resident, [FromRoute] Guid id)
    {
        if (!await access.CanAccessResidentAsync(User, resident))
            return Forbid();

        var chosen = await db.ResidentWelcomeImages
            .FirstOrDefaultAsync(w => w.Id == id && w.Resident == resident);
        if (chosen is null)
            return NotFound();

        // Exactly one approved picture at a time: a device asking "which picture"
        // must never have to choose.
        var others = await db.ResidentWelcomeImages
            .Where(w => w.Resident == resident && w.Status == WelcomeImageStatus.Approved && w.Id != id)
            .ToListAsync();
        foreach (var other in others)
        {
            other.Status = WelcomeImageStatus.Rejected;
            other.ApprovedBy = null;
            other.ApprovedAtUtc = null;
        }

        chosen.Status = WelcomeImageStatus.Approved;
        chosen.ApprovedBy = access.GetEmail(User);
        chosen.ApprovedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        logger.LogInformation("Welcome image {Id} approved for {Resident} by {Who}",
            id, resident, chosen.ApprovedBy);
        return Ok(View(chosen));
    }

    [HttpPost("{resident}/welcome-image/{id:guid}/reject")]
    public async Task<IActionResult> Reject([FromRoute] string resident, [FromRoute] Guid id)
    {
        if (!await access.CanAccessResidentAsync(User, resident))
            return Forbid();

        var row = await db.ResidentWelcomeImages
            .FirstOrDefaultAsync(w => w.Id == id && w.Resident == resident);
        if (row is null)
            return NotFound();

        row.Status = WelcomeImageStatus.Rejected;
        await db.SaveChangesAsync();
        return NoContent();
    }

    // ---------------------------------------------------------------- bits

    private WelcomeImageView View(ResidentWelcomeImage w)
    {
        var (uri, expires) = sas.GetReadSasUri(PhotosContainer, w.BlobPath);
        return new WelcomeImageView(
            w.Id,
            w.Status.ToString(),
            w.Subject,
            w.Prompt,
            w.GeneratedAtUtc,
            w.ApprovedBy,
            uri.AbsoluteUri,
            expires);
    }

    /// <summary>
    /// Pull image bytes out of whatever shape the flow returns. Power Automate
    /// output is shaped by whoever built the flow, so several spellings are
    /// accepted rather than demanding one and failing opaquely.
    /// </summary>
    private static List<byte[]> ExtractImages(JsonObject? result)
    {
        var found = new List<byte[]>();
        if (result is null)
            return found;

        void TryAdd(JsonNode? node)
        {
            if (node is null)
                return;

            if (node is JsonValue value && value.TryGetValue<string>(out var text))
            {
                var bytes = Decode(text);
                if (bytes is not null)
                    found.Add(bytes);
                return;
            }

            if (node is JsonObject obj)
            {
                foreach (var key in new[] { "base64", "b64_json", "data", "content", "image", "url" })
                {
                    if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s))
                    {
                        var bytes = Decode(s);
                        if (bytes is not null)
                        {
                            found.Add(bytes);
                            return;
                        }
                    }
                }
            }
        }

        foreach (var key in new[] { "images", "image", "data", "result", "output" })
        {
            var node = result[key];
            if (node is JsonArray array)
            {
                foreach (var item in array)
                    TryAdd(item);
            }
            else
            {
                TryAdd(node);
            }
            if (found.Count > 0)
                return found;
        }

        TryAdd(result["base64"]);
        return found;
    }

    private static byte[]? Decode(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        // A data: URI carries the payload after the comma.
        var comma = text.IndexOf(',');
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
            text = text[(comma + 1)..];

        // An http URL is a reference, not bytes; the flow should return the
        // image itself so the picture does not depend on a link expiring.
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var bytes = Convert.FromBase64String(text.Trim());
            // A PNG or JPEG under a kilobyte is an error message, not a picture.
            return bytes.Length > 1024 ? bytes : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string Slug(string resident)
    {
        var chars = resident.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-');
        var slug = new string(chars.ToArray()).Trim('-');
        while (slug.Contains("--"))
            slug = slug.Replace("--", "-");
        return slug.Length == 0 ? "resident" : slug;
    }

    private static string Trim(string body) =>
        body.Length <= 400 ? body : body[..400] + "…";
}
