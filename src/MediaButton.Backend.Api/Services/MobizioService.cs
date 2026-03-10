using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace MediaButtonBackend.Services;

public record ResidentMobizioProfile(
    string Name,
    string? Dob,
    string? Gender,
    IReadOnlyDictionary<string, string> FormFields);

public record MobizioResidentSummary(string Name, string CaseId, string? Dob);

public class MobizioService(IConfiguration configuration, IHttpClientFactory httpFactory, ILogger<MobizioService> logger)
{
    private const string ApiBase = "https://cloud7.mobizio.com/rest";

    private int FormId => configuration.GetValue("Mobizio:ThisIsMeFormId", 1021596);
    private int ActivityRecordFormId => configuration.GetValue("Mobizio:ActivityRecordFormId", 1024129);
    private int ActivityPhotoLimit => configuration.GetValue("Mobizio:ActivityPhotoLimit", 5);
    private string Username => configuration["Mobizio:Username"] ?? "";
    private string Password => configuration["Mobizio:Password"] ?? "";

    public async Task<IReadOnlyList<MobizioResidentSummary>> ListActiveResidentsAsync()
    {
        var token = await GetTokenAsync();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await http.GetAsync(
            $"{ApiBase}/v3/cases?start=0&limit=5000&column=id&direction=1&mql=archived%3Dfalse");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var results = json?["results"]?.AsArray() ?? [];

        var residents = new List<MobizioResidentSummary>();
        foreach (var item in results)
        {
            var customer = item?["customer"];
            var firstName = customer?["firstName"]?.GetValue<string>()?.Trim() ?? "";
            var lastName  = customer?["lastName"]?.GetValue<string>()?.Trim() ?? "";
            var name = $"{firstName} {lastName}".Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var caseId = item?["id"]?.ToString() ?? "";
            var dob    = customer?["dob"]?.GetValue<string>();
            residents.Add(new MobizioResidentSummary(name, caseId, dob));
        }

        return residents.OrderBy(r => r.Name).ToList();
    }

    public async Task<ResidentMobizioProfile?> GetResidentProfileAsync(string residentName)
    {
        var token = await GetTokenAsync();

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (caseId, dob, gender) = await FindCaseAsync(http, residentName);
        if (caseId is null) return null;

        var fields = await GetThisIsMeFieldsAsync(http, caseId);
        return new ResidentMobizioProfile(residentName, dob, gender, fields);
    }

    private async Task<string> GetTokenAsync()
    {
        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        var resp = await http.PostAsync(
            $"{ApiBase}/oauth/token?grant_type=password&client_id=web-console" +
            $"&username={Uri.EscapeDataString(Username)}&password={Uri.EscapeDataString(Password)}",
            null);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        return json?["access_token"]?.GetValue<string>()
               ?? throw new InvalidOperationException("No access_token in Mobizio auth response");
    }

    private static async Task<(string? CaseId, string? Dob, string? Gender)>
        FindCaseAsync(HttpClient http, string residentName)
    {
        var resp = await http.GetAsync(
            $"{ApiBase}/v3/cases?start=0&limit=5000&column=id&direction=1&mql=archived%3Dfalse");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonObject>();
        var results = json?["results"]?.AsArray() ?? [];

        var search = residentName.Trim();
        var searchParts = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var searchLast = searchParts.Last();
        var searchFirst = searchParts.First();

        // Pass 1: exact full name / combined name match
        // Pass 2: all search parts found as substrings in full name
        // Pass 3: last name matches exactly + first name starts with same letters (handles Nicolas/Nicholas etc.)
        foreach (var pass in new[] { 1, 2, 3 })
        {
            foreach (var item in results)
            {
                var customer = item?["customer"];
                var fullName = customer?["fullName"]?.GetValue<string>()?.Trim() ?? "";
                var firstName = customer?["firstName"]?.GetValue<string>()?.Trim() ?? "";
                var lastName = customer?["lastName"]?.GetValue<string>()?.Trim() ?? "";
                var combined = $"{firstName} {lastName}".Trim();

                bool match = pass switch
                {
                    1 => string.Equals(fullName, search, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(combined, search, StringComparison.OrdinalIgnoreCase),
                    2 => searchParts.All(p =>
                             fullName.Contains(p, StringComparison.OrdinalIgnoreCase)
                             || combined.Contains(p, StringComparison.OrdinalIgnoreCase)),
                    _ => string.Equals(lastName, searchLast, StringComparison.OrdinalIgnoreCase)
                         && (firstName.StartsWith(searchFirst[..Math.Min(3, searchFirst.Length)],
                             StringComparison.OrdinalIgnoreCase)),
                };

                if (!match) continue;

                return (
                    item?["id"]?.ToString(),
                    customer?["dob"]?.GetValue<string>(),
                    customer?["gender"]?.GetValue<string>()
                );
            }
        }

        return (null, null, null);
    }

    /// <summary>
    /// Returns up to <see cref="ActivityPhotoLimit"/> (elementId, downloadUrl) pairs from
    /// the most recent activity record form submissions for the named resident.
    /// </summary>
    public async Task<IReadOnlyList<(int ElementId, string Url)>> GetActivityPhotoUrlsAsync(
        string residentName, int? maxPhotos = null)
    {
        var limit = maxPhotos ?? ActivityPhotoLimit;
        var token = await GetTokenAsync();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (caseId, _, _) = await FindCaseAsync(http, residentName);
        if (caseId is null)
        {
            logger.LogWarning("[ActivityPhotos] Could not find case ID for resident: {Resident}", residentName);
            return [];
        }
        logger.LogInformation("[ActivityPhotos] Found case ID {CaseId} for resident {Resident}", caseId, residentName);

        // Get form versions for the activity record form
        var versionsResp = await http.GetAsync(
            $"{ApiBase}/v3/forms/{ActivityRecordFormId}/versions?start=0&limit=100&column=id&direction=1");
        if (!versionsResp.IsSuccessStatusCode)
        {
            logger.LogWarning("[ActivityPhotos] Failed to get form versions for form {FormId}: {Status}",
                ActivityRecordFormId, versionsResp.StatusCode);
            return [];
        }

        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonObject>();
        var versionIds = (versionsJson?["results"]?.AsArray() ?? [])
            .Select(v => v?["id"]?.ToString())
            .OfType<string>()
            .ToList();
        logger.LogInformation("[ActivityPhotos] Form {FormId} has {Count} version(s): {Ids}",
            ActivityRecordFormId, versionIds.Count, string.Join(", ", versionIds));

        // Collect element IDs that have a valueBlobRef, across recent submissions
        var photoElementIds = new List<int>();

        foreach (var versionId in versionIds)
        {
            if (photoElementIds.Count >= limit) break;

            var mql = Uri.EscapeDataString($"formVersionId={versionId},caseDataGroupCaseId={caseId}");
            var formsResp = await http.GetAsync(
                $"{ApiBase}/v3/submittedForms?start=0&limit=20&column=createdDateTime&direction=-1&mql={mql}");
            if (!formsResp.IsSuccessStatusCode)
            {
                logger.LogWarning("[ActivityPhotos] submittedForms query failed for version {VersionId}: {Status}",
                    versionId, formsResp.StatusCode);
                continue;
            }

            var formsJson = await formsResp.Content.ReadFromJsonAsync<JsonObject>();
            var formResults = formsJson?["results"]?.AsArray() ?? [];
            logger.LogInformation("[ActivityPhotos] Version {VersionId}: {Count} submitted form(s) found for case {CaseId}",
                versionId, formResults.Count, caseId);
            foreach (var form in formResults)
            {
                if (photoElementIds.Count >= limit) break;

                var submittedFormId = form?["id"]?.ToString();
                if (submittedFormId is null) continue;

                var elemResp = await http.GetAsync(
                    $"{ApiBase}/v3/submittedForms/{submittedFormId}/elements");
                if (!elemResp.IsSuccessStatusCode) continue;

                var elemContent = await elemResp.Content.ReadFromJsonAsync<JsonNode>();
                var elements = elemContent is JsonArray arr
                    ? arr
                    : elemContent?["results"]?.AsArray() ?? [];

                foreach (var elem in elements)
                {
                    if (photoElementIds.Count >= limit) break;
                    var blobRef = elem?["valueBlobRef"];
                    if (blobRef is null || blobRef.GetValueKind() == System.Text.Json.JsonValueKind.Null)
                        continue;
                    if (elem?["id"]?.GetValue<int?>() is int elemId)
                        photoElementIds.Add(elemId);
                }
            }
        }

        logger.LogInformation("[ActivityPhotos] Collected {Count} photo element ID(s) with valueBlobRef", photoElementIds.Count);
        if (photoElementIds.Count == 0) return [];

        // Generate download URLs for the collected element IDs
        var dlResp = await http.PostAsJsonAsync(
            $"{ApiBase}/v3/mobile-sync/generate-blob-storage-url-for-download",
            photoElementIds);

        if (!dlResp.IsSuccessStatusCode)
        {
            var body = await dlResp.Content.ReadAsStringAsync();
            logger.LogWarning("[ActivityPhotos] generate-blob-storage-url-for-download returned {Status}: {Body}",
                dlResp.StatusCode, body[..Math.Min(500, body.Length)]);
            return [];
        }

        var dlRawBody = await dlResp.Content.ReadAsStringAsync();
        logger.LogInformation("[ActivityPhotos] generate-blob-storage-url-for-download response: {Body}",
            dlRawBody[..Math.Min(1000, dlRawBody.Length)]);
        var dlJson = System.Text.Json.JsonSerializer.Deserialize<JsonNode>(dlRawBody);
        var results = dlJson is JsonArray topArr
            ? topArr
            : dlJson?["results"]?.AsArray() ?? [];

        var output = new List<(int, string)>();
        for (int i = 0; i < results.Count && i < photoElementIds.Count; i++)
        {
            var item = results[i];
            string? url = null;

            if (item is JsonValue jv && jv.TryGetValue<string>(out var s))
            {
                url = s;
            }
            else if (item is JsonObject jo)
            {
                url = jo["url"]?.GetValue<string>()
                      ?? jo["downloadUrl"]?.GetValue<string>()
                      ?? jo["blobUrl"]?.GetValue<string>()
                      ?? jo["sasUrl"]?.GetValue<string>();
            }

            if (!string.IsNullOrWhiteSpace(url))
                output.Add((photoElementIds[i], url));
        }

        return output;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetThisIsMeFieldsAsync(
        HttpClient http, string caseId)
    {
        var versionsResp = await http.GetAsync(
            $"{ApiBase}/v3/forms/{FormId}/versions?start=0&limit=100&column=id&direction=1");
        versionsResp.EnsureSuccessStatusCode();

        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonObject>();
        var versionIds = (versionsJson?["results"]?.AsArray() ?? [])
            .Select(v => v?["id"]?.ToString())
            .OfType<string>()
            .ToList();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var versionId in versionIds)
        {
            var mql = Uri.EscapeDataString($"formVersionId={versionId},caseDataGroupCaseId={caseId}");
            var formsResp = await http.GetAsync(
                $"{ApiBase}/v3/submittedForms?start=0&limit=100&column=id&direction=1&mql={mql}");
            if (!formsResp.IsSuccessStatusCode) continue;

            var formsJson = await formsResp.Content.ReadFromJsonAsync<JsonObject>();
            foreach (var form in formsJson?["results"]?.AsArray() ?? [])
            {
                var submittedFormId = form?["id"]?.ToString();
                if (submittedFormId is null) continue;

                var elemResp = await http.GetAsync(
                    $"{ApiBase}/v3/submittedForms/{submittedFormId}/elements");
                if (!elemResp.IsSuccessStatusCode) continue;

                // Elements may come back as an array or as { results: [...] }
                var elemContent = await elemResp.Content.ReadFromJsonAsync<JsonNode>();
                var elements = elemContent is JsonArray arr
                    ? arr
                    : elemContent?["results"]?.AsArray() ?? [];

                foreach (var elem in elements)
                {
                    var label = elem?["componentLabel"]?.GetValue<string>();
                    var value = elem?["valueText"]?.GetValue<string>()
                                ?? elem?["valueAsString"]?.GetValue<string>();

                    if (label is not null && value is not null
                        && !string.IsNullOrWhiteSpace(value) && !fields.ContainsKey(label))
                    {
                        fields[label] = value;
                    }
                }
            }
        }

        return fields;
    }
}
