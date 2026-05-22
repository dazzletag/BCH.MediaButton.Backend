using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace MediaButtonBackend.Services;

public record ResidentMobizioProfile(
    string Name,
    string? Dob,
    string? Gender,
    IReadOnlyDictionary<string, string> FormFields);

public record MobizioResidentSummary(string Name, string CaseId, string? TenantCaseId, string? Dob);

public class MobizioService(IConfiguration configuration, IHttpClientFactory httpFactory, ILogger<MobizioService> logger)
{
    private const string ApiBase = "https://cloud7.mobizio.com/rest";

    private int FormId => configuration.GetValue("Mobizio:ThisIsMeFormId", 1021596);
    private int ActivityRecordFormId => configuration.GetValue("Mobizio:ActivityRecordFormId", 1024129);
    private int WellBeingFormId => configuration.GetValue("Mobizio:WellBeingFormId", 1034032);
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

            var caseId       = item?["id"]?.ToString() ?? "";
            var tenantCaseId = item?["tenantCaseId"]?.GetValue<string>();
            var dob          = customer?["dob"]?.GetValue<string>();
            residents.Add(new MobizioResidentSummary(name, caseId, tenantCaseId, dob));
        }

        return residents.OrderBy(r => r.Name).ToList();
    }

    public async Task<ResidentMobizioProfile?> GetResidentProfileAsync(string residentName)
    {
        var token = await GetTokenAsync();

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (caseId, tenantCaseId, dob, gender) = await FindCaseAsync(http, residentName);
        if (caseId is null || tenantCaseId is null) return null;

        var fields = await GetThisIsMeFieldsAsync(http, caseId, tenantCaseId);
        return new ResidentMobizioProfile(residentName, dob, gender, fields);
    }

    /// <summary>
    /// Fetch the This Is Me profile using a known Mobizio case ID, bypassing the
    /// full-list name search. Used when the case ID was captured during device setup.
    /// </summary>
    public async Task<ResidentMobizioProfile?> GetResidentProfileByCaseIdAsync(
        string caseId, string tenantCaseId, string residentName)
    {
        var token = await GetTokenAsync();

        var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var fields = await GetThisIsMeFieldsAsync(http, caseId, tenantCaseId);
        if (fields.Count == 0) return null;
        return new ResidentMobizioProfile(residentName, null, null, fields);
    }

    internal async Task<string> GetTokenForDebugAsync() => await GetTokenAsync();

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

    private static async Task<(string? CaseId, string? TenantCaseId, string? Dob, string? Gender)>
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
                    item?["tenantCaseId"]?.GetValue<string>(),
                    customer?["dob"]?.GetValue<string>(),
                    customer?["gender"]?.GetValue<string>()
                );
            }
        }

        return (null, null, null, null);
    }

    /// <summary>
    /// Returns up to <see cref="ActivityPhotoLimit"/> (elementId, downloadUrl) pairs from
    /// the most recent activity record form submissions for the named resident.
    /// </summary>
    public async Task<IReadOnlyList<(int ElementId, byte[] Data, string ContentType)>> GetActivityPhotoUrlsAsync(
        string residentName, List<string> diag, HashSet<int>? skipElementIds = null, int? maxPhotos = null,
        string? knownCaseId = null, string? knownTenantCaseId = null)
    {
        var limit = maxPhotos ?? ActivityPhotoLimit;
        var token = await GetTokenAsync();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        string? caseId = knownCaseId;
        string? tenantCaseId = knownTenantCaseId;

        if (string.IsNullOrWhiteSpace(caseId) || string.IsNullOrWhiteSpace(tenantCaseId))
        {
            (caseId, tenantCaseId, _, _) = await FindCaseAsync(http, residentName);
        }
        else
        {
            diag.Add($"Using stored Mobizio IDs: caseId={caseId}, tenantCaseId={tenantCaseId}");
        }

        if (caseId is null || tenantCaseId is null)
        {
            diag.Add($"Could not find Mobizio case for resident '{residentName}'");
            return [];
        }

        // Scan each form type independently so neither crowds out the other.
        var perTypeLimit = Math.Max(1, limit / 2);
        diag.Add($"Scanning Activity Record (form {ActivityRecordFormId}) + Well-being (form {WellBeingFormId}), {perTypeLimit} slots each");

        var output = new List<(int ElementId, byte[] Data, string ContentType)>();
        // Activity Record lives in the Activities section
        await CollectSectionPhotosAsync(
            http, tenantCaseId, ["*Activities*"], ActivityRecordFormId,
            perTypeLimit, skipElementIds, diag, output);
        // Well-being is also in the Activities section (confirmed via Mobizio API)
        await CollectSectionPhotosAsync(
            http, tenantCaseId, ["*Activities*"], WellBeingFormId,
            perTypeLimit, skipElementIds, diag, output);

        diag.Add($"Collected {output.Count} photo(s) total");
        return output;
    }

    private async Task CollectSectionPhotosAsync(
        HttpClient http, string tenantCaseId,
        string[] sectionMqls, int formId,
        int limit, HashSet<int>? skipElementIds,
        List<string> diag, List<(int, byte[], string)> output)
    {
        string? sectionId = null;
        string? sectionLabel = null;
        foreach (var mql in sectionMqls)
        {
            var escapedMql = Uri.EscapeDataString($"serviceDataGroup.label~{mql}");
            var sectionsResp = await http.GetAsync(
                $"{ApiBase}/v3/cases/{tenantCaseId}/sections?start=0&limit=10&column=id&direction=1&mql={escapedMql}");
            if (!sectionsResp.IsSuccessStatusCode) continue;

            var sectionsJson = await sectionsResp.Content.ReadFromJsonAsync<JsonObject>();
            var sectionResults = sectionsJson?["results"]?.AsArray() ?? [];
            if (sectionResults.Count == 0) continue;

            sectionId = sectionResults[0]?["id"]?.ToString();
            sectionLabel = sectionResults[0]?["serviceDataGroup"]?["label"]?.ToString()
                        ?? sectionResults[0]?["label"]?.ToString() ?? mql;
            if (!string.IsNullOrWhiteSpace(sectionId)) break;
        }

        if (string.IsNullOrWhiteSpace(sectionId))
        {
            diag.Add($"[form {formId}] No section found for patterns: {string.Join(", ", sectionMqls)}");
            return;
        }
        diag.Add($"[form {formId}] Using section '{sectionLabel}' (id={sectionId})");

        // Use the form-specific _overview endpoint so we only get submissions for this form ID,
        // with no version-ID filtering needed.
        var overviewResp = await http.GetAsync(
            $"{ApiBase}/v3/cases/{tenantCaseId}/sections/{sectionId}/forms/{formId}/submittedForms/_overview?start=0&limit=100&column=createdDateTime&direction=-1");
        if (!overviewResp.IsSuccessStatusCode)
        {
            diag.Add($"[form {formId}] _overview endpoint failed: {overviewResp.StatusCode}");
            return;
        }

        var overviewJson = await overviewResp.Content.ReadFromJsonAsync<JsonObject>();
        var submissions = overviewJson?["results"]?.AsArray() ?? [];
        diag.Add($"[form {formId}] {submissions.Count} submission(s) found");

        foreach (var submission in submissions)
        {
            if (output.Count >= limit) break;

            var submittedFormId = submission?["id"]?.ToString();
            if (submittedFormId is null) continue;

            var elemResp = await http.GetAsync(
                $"{ApiBase}/v3/submittedForms/{submittedFormId}/elements");
            if (!elemResp.IsSuccessStatusCode) continue;

            var elemBody = await elemResp.Content.ReadAsStringAsync();
            var elemContent = System.Text.Json.JsonSerializer.Deserialize<JsonNode>(elemBody);
            var elements = elemContent is JsonArray arr
                ? arr
                : elemContent?["results"]?.AsArray() ?? [];

            foreach (var elem in elements)
            {
                if (output.Count >= limit) break;

                var label = elem?["componentLabel"]?.GetValue<string>() ?? "";
                var elemType = elem?["type"]?.GetValue<string>() ?? "";

                var isPhotoElem = elemType.Equals("photo", StringComparison.OrdinalIgnoreCase)
                    || label.StartsWith("Photo", StringComparison.OrdinalIgnoreCase);
                if (!isPhotoElem) continue;

                var elemId = (int)(elem?["id"]?.GetValue<long?>() ?? 0);

                var rawValue = elem?["encodedValue"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(rawValue))
                    rawValue = elem?["value"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(rawValue))
                    rawValue = elem?["valueSmallText"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(rawValue)) continue;

                if (skipElementIds?.Contains(elemId) == true)
                {
                    diag.Add($"  elem {elemId}: already imported");
                    continue;
                }

                byte[] bytes;
                string contentType = "image/jpeg";

                if (rawValue.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    rawValue.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    var photoResp = await http.GetAsync(rawValue);
                    if (!photoResp.IsSuccessStatusCode)
                    {
                        diag.Add($"  elem {elemId}: URL fetch failed {photoResp.StatusCode}");
                        continue;
                    }
                    bytes = await photoResp.Content.ReadAsByteArrayAsync();
                    contentType = photoResp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    diag.Add($"  elem {elemId}: fetched from URL, {bytes.Length} bytes, {contentType}");
                }
                else
                {
                    if (rawValue.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = rawValue.IndexOf(',');
                        if (comma > 0)
                        {
                            contentType = rawValue[5..comma].Split(';')[0];
                            rawValue = rawValue[(comma + 1)..];
                        }
                    }
                    try
                    {
                        bytes = Convert.FromBase64String(rawValue);
                        diag.Add($"  elem {elemId}: base64 decoded, {bytes.Length} bytes, {contentType}");
                    }
                    catch (FormatException)
                    {
                        diag.Add($"  elem {elemId}: base64 decode failed");
                        continue;
                    }
                }

                output.Add((elemId, bytes, contentType));
            }
        }
    }

    internal async Task<IReadOnlyDictionary<string, string>> GetThisIsMeFieldsAsync(
        HttpClient http, string caseId, string tenantCaseId, List<string>? diag = null)
    {
        // Get version IDs for the This Is Me form so we can match submitted forms
        var versionsResp = await http.GetAsync(
            $"{ApiBase}/v3/forms/{FormId}/versions?start=0&limit=100&column=id&direction=1");
        versionsResp.EnsureSuccessStatusCode();
        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonObject>();
        var versionIds = new HashSet<string>(
            (versionsJson?["results"]?.AsArray() ?? [])
                .Select(v => v?["id"]?.ToString())
                .OfType<string>());
        diag?.Add($"Form {FormId} has {versionIds.Count} version(s)");

        // Find the case section that contains This Is Me / Activities forms
        var sectionMql = Uri.EscapeDataString("serviceDataGroup.label~*Activities*");
        var sectionUrl = $"{ApiBase}/v3/cases/{tenantCaseId}/sections?start=0&limit=10&column=id&direction=1&mql={sectionMql}";
        diag?.Add($"Section lookup: {sectionUrl}");
        var sectionResp = await http.GetAsync(sectionUrl);
        diag?.Add($"Section response: {sectionResp.StatusCode}");
        if (!sectionResp.IsSuccessStatusCode) return new Dictionary<string, string>();

        var sectionJson = await sectionResp.Content.ReadFromJsonAsync<JsonObject>();
        var sectionResults = sectionJson?["results"]?.AsArray() ?? [];
        diag?.Add($"Sections found: {sectionResults.Count}");
        var sectionId = sectionResults.Select(s => s?["id"]?.ToString()).OfType<string>().FirstOrDefault();
        if (sectionId is null) return new Dictionary<string, string>();
        diag?.Add($"Using section id={sectionId}");

        // Get all submitted forms for that section and filter to This Is Me versions
        var formsUrl = $"{ApiBase}/v3/cases/{tenantCaseId}/sections/{sectionId}/submittedForms?start=0&limit=500&column=id&direction=1";
        var formsResp = await http.GetAsync(formsUrl);
        diag?.Add($"Forms response: {formsResp.StatusCode}");
        if (!formsResp.IsSuccessStatusCode) return new Dictionary<string, string>();

        var formsJson = await formsResp.Content.ReadFromJsonAsync<JsonObject>();
        var allForms = (formsJson?["results"]?.AsArray() ?? []).ToList();
        diag?.Add($"Total forms in section: {allForms.Count}");
        var matchingForms = allForms
            .Where(f => versionIds.Contains(f?["formVersionId"]?.ToString() ?? ""))
            .ToList();
        diag?.Add($"This Is Me forms matched: {matchingForms.Count}");

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var form in matchingForms)
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

        return fields;
    }
}
