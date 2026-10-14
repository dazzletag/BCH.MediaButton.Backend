using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace MediaButtonBackend.Services;

/// <summary>
/// Generates pictures through Azure OpenAI, on the subscription this service
/// already runs in. No separate vendor account, and in production no stored
/// key either: App Service's own identity is used, so there is nothing to leak
/// from configuration.
/// </summary>
public class AzureOpenAiImageService(
    IConfiguration configuration,
    IHttpClientFactory httpFactory,
    ILogger<AzureOpenAiImageService> logger)
{
    private static readonly TokenRequestContext CognitiveScope =
        new(["https://cognitiveservices.azure.com/.default"]);

    // One credential for the process: DefaultAzureCredential probes several
    // sources and caches tokens, and building it per request is slow.
    private static readonly Lazy<DefaultAzureCredential> Credential =
        new(() => new DefaultAzureCredential());

    private string? Endpoint => configuration["AzureOpenAI:Endpoint"]?.TrimEnd('/');
    private string Deployment => configuration["AzureOpenAI:ImageDeployment"] ?? "gpt-image-1";
    private string ApiVersion => configuration["AzureOpenAI:ImageApiVersion"] ?? "2025-04-01-preview";
    private string? ApiKey => configuration["AzureOpenAI:ApiKey"];

    /// <summary>
    /// dall-e-3 took 1792x1024 for widescreen; gpt-image-1 rejects that and
    /// offers 1536x1024 as its landscape size. Both are close enough to 16:9
    /// that the overlay step crops to 1600x900 without anything important
    /// being lost.
    /// </summary>
    private string Size => configuration["AzureOpenAI:ImageSize"]
        ?? (IsDallE ? "1792x1024" : "1536x1024");

    /// <summary>
    /// The two families take different parameters, and sending one family's to
    /// the other is rejected rather than ignored. There is no API to ask which
    /// a deployment is, so it is inferred from the name and can be overridden.
    /// </summary>
    private bool IsDallE =>
        (configuration["AzureOpenAI:ImageModelFamily"]
         ?? Deployment).Contains("dall", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>One generated picture, and what the model was actually asked.</summary>
    public record Picture(byte[] Bytes, string? RevisedPrompt);

    /// <summary>
    /// Generate up to <paramref name="count"/> pictures. Returns what succeeded;
    /// an empty list means none did, and the reason is in the log rather than an
    /// exception, because a failure here must not take down the page.
    /// </summary>
    public async Task<List<Picture>> GenerateAsync(
        string prompt, int count, CancellationToken ct = default)
    {
        var results = new List<Picture>();
        if (!IsConfigured)
            return results;

        // dall-e-3 refuses n > 1, so candidates are separate requests. Done in
        // sequence on purpose: three at once invites a rate-limit response that
        // loses all three rather than two of them.
        for (var i = 0; i < count; i++)
        {
            try
            {
                var picture = await GenerateOneAsync(prompt, ct);
                if (picture is not null)
                    results.Add(picture);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Azure OpenAI image {Index} of {Count} failed", i + 1, count);
                // A later one may still succeed; one picture is better than none.
            }
        }
        return results;
    }

    private async Task<Picture?> GenerateOneAsync(string prompt, CancellationToken ct)
    {
        var url = $"{Endpoint}/openai/deployments/{Deployment}/images/generations"
                  + $"?api-version={ApiVersion}";

        var body = new JsonObject
        {
            ["prompt"] = prompt,
            ["n"] = 1,
            ["size"] = Size,
        };

        if (IsDallE)
        {
            // "vivid" is dall-e-3's default and pushes dramatic, hyper-real
            // results — the opposite of what belongs on the screen a resident
            // meets. "natural" is the plainer treatment.
            body["style"] = "natural";
            body["quality"] = "standard";
            body["response_format"] = "b64_json";
        }
        else
        {
            // gpt-image-1 always returns base64 and rejects response_format and
            // style outright. Its quality scale is low/medium/high.
            body["quality"] = configuration["AzureOpenAI:ImageQuality"] ?? "medium";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            request.Headers.Add("api-key", ApiKey);
        }
        else
        {
            var token = await Credential.Value.GetTokenAsync(CognitiveScope, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(120);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            // Azure returns a useful message here — a refused prompt, a missing
            // deployment, a quota — so it is worth surfacing rather than "502".
            logger.LogWarning("Azure OpenAI returned {Status}: {Body}",
                (int)response.StatusCode, Clip(text));
            throw new InvalidOperationException(
                $"Azure OpenAI returned {(int)response.StatusCode}: {Clip(text)}");
        }

        var parsed = JsonNode.Parse(text) as JsonObject;
        var first = (parsed?["data"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var b64 = first?["b64_json"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(b64))
        {
            logger.LogWarning("Azure OpenAI answered without image data: {Body}", Clip(text));
            return null;
        }

        // dall-e-3 rewrites the prompt before drawing and returns what it used,
        // which is worth keeping: when a picture comes back wrong, that rewrite
        // is what produced it. gpt-image-1 does not echo one, so this is often
        // legitimately absent.
        var revised = first?["revised_prompt"]?.GetValue<string>();

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            logger.LogWarning("Azure OpenAI image data was not valid base64");
            return null;
        }

        if (bytes.Length < 1024)
        {
            logger.LogWarning("Azure OpenAI returned {Bytes} bytes — too small to be a picture",
                bytes.Length);
            return null;
        }

        return new Picture(bytes, revised);
    }

    private static string Clip(string s) => s.Length <= 500 ? s : s[..500] + "…";
}
