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
            $"{ApiBase}/v3/cases?start=0&limit=5000&column=id&direction=1&mql=status%3Dactive");
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

        var (caseId, _, dob, gender) = await FindCaseAsync(http, residentName);
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
        string residentName, List<string> diag, HashSet<int>? skipElementIds = null, int? maxPhotos = null)
    {
        var limit = maxPhotos ?? ActivityPhotoLimit;
        var token = await GetTokenAsync();

        using var http = httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var (caseId, tenantCaseId, _, _) = await FindCaseAsync(http, residentName);
        if (caseId is null || tenantCaseId is null)
        {
            diag.Add($"Could not find Mobizio case for resident '{residentName}'");
            return [];
        }
        // Look up the caseDataGroupId for "Social History/Activities" section
        var sectionsMql = Uri.EscapeDataString("serviceDataGroupLabel=Social History/Activities");
        var sectionsResp = await http.GetAsync(
            $"{ApiBase}/v3/cases/{tenantCaseId}/sections/_lite?start=0&limit=10&column=id&direction=1&mql={sectionsMql}");

        if (!sectionsResp.IsSuccessStatusCode)
        {
            // Retry with numeric caseId
            sectionsResp = await http.GetAsync(
                $"{ApiBase}/v3/cases/{caseId}/sections/_lite?start=0&limit=10&column=id&direction=1&mql={sectionsMql}");
        }

        var sectionsBody = await sectionsResp.Content.ReadAsStringAsync();
        var sectionsJson = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(sectionsBody);
        var sectionResults = sectionsJson?["results"]?.AsArray() ?? [];

        if (sectionResults.Count == 0)
        {
            diag.Add($"No 'Social History/Activities' section found for {tenantCaseId}");
            return [];
        }

        var caseDataGroupId = sectionResults[0]?["id"]?.ToString();
        if (string.IsNullOrWhiteSpace(caseDataGroupId))
        {
            diag.Add("caseDataGroupId missing from section result");
            return [];
        }

        // Get form versions for the activity record form
        var versionsResp = await http.GetAsync(
            $"{ApiBase}/v3/forms/{ActivityRecordFormId}/versions?start=0&limit=100&column=id&direction=1");
        if (!versionsResp.IsSuccessStatusCode)
        {
            diag.Add($"Failed to get form versions for form {ActivityRecordFormId}: {versionsResp.StatusCode}");
            return [];
        }

        var versionsJson = await versionsResp.Content.ReadFromJsonAsync<JsonObject>();
        var versionIds = (versionsJson?["results"]?.AsArray() ?? [])
            .Select(v => v?["id"]?.ToString())
            .OfType<string>()
            .ToList();
        diag.Add($"Form {ActivityRecordFormId} has {versionIds.Count} version(s): {string.Join(", ", versionIds)}");

        // Collect photo elements (componentLabel = "Photo"/"Photos" with encodedValue)
        var output = new List<(int ElementId, byte[] Data, string ContentType)>();

        foreach (var versionId in versionIds)
        {
            if (output.Count >= limit) break;

            var mql = Uri.EscapeDataString($"formVersionId={versionId},caseDataGroupId={caseDataGroupId}");
            var formsUrl = $"{ApiBase}/v3/submittedForms?start=0&limit=20&column=createdDateTime&direction=-1&mql={mql}";
            var formsResp = await http.GetAsync(formsUrl);
            var formsBody = await formsResp.Content.ReadAsStringAsync();
            if (!formsResp.IsSuccessStatusCode) continue;

            var formsJson = System.Text.Json.JsonSerializer.Deserialize<JsonObject>(formsBody);
            var formResults = formsJson?["results"]?.AsArray() ?? [];
            if (formResults.Count > 0)
                diag.Add($"v{versionId}: {formResults.Count} form(s)");

            foreach (var form in formResults)
            {
                if (output.Count >= limit) break;

                var submittedFormId = form?["id"]?.ToString();
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
                    if (!label.Equals("Photo", StringComparison.OrdinalIgnoreCase) &&
                        !label.Equals("Photos", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var encoded = elem?["encodedValue"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(encoded)) continue;

                    // Strip data URL prefix if present (e.g. "data:image/jpeg;base64,...")
                    string contentType = "image/jpeg";
                    if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var comma = encoded.IndexOf(',');
                        if (comma > 0)
                        {
                            var header = encoded[5..comma]; // e.g. "image/png;base64"
                            contentType = header.Split(';')[0];
                            encoded = encoded[(comma + 1)..];
                        }
                    }

                    try
                    {
                        var elemId = elem?["id"]?.GetValue<long?>() ?? 0;
                        if (skipElementIds?.Contains((int)elemId) == true)
                        {
                            diag.Add($"  form {submittedFormId} elem {elemId}: skipping (already imported)");
                            continue;
                        }
                        var bytes = Convert.FromBase64String(encoded);
                        diag.Add($"  form {submittedFormId} elem {elemId}: photo {bytes.Length} bytes, {contentType}");
                        output.Add(((int)elemId, bytes, contentType));
                    }
                    catch (FormatException)
                    {
                        diag.Add($"  form {submittedFormId}: base64 decode failed");
                    }
                }
            }
        }

        diag.Add($"Collected {output.Count} photo(s) from encodedValue");
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
