using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migrator.Core.Abstractions;
using Migrator.Core.Models;
using Serilog;

namespace Migrator.Sitecore;

/// <summary>
/// Reads content via Sitecore Headless Layout Service (JSS), which accepts standard API keys.
/// Use this when Item Service (/sitecore/api/ssc) returns 403.
///
/// Limitations vs Item Service:
/// - Subtree walk discovers the requested route plus datasource/component items in placeholders
///   (not arbitrary folder children under /sitecore/content).
/// - Template inheritance metadata is not available (fields come from the layout payload as-is).
/// </summary>
public sealed class SitecoreLayoutServiceClient : ISitecoreClient
{
    private readonly HttpClient _http;
    private readonly SitecoreConnectionSettings _settings;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, SitecoreItemDto> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SitecoreLayoutServiceClient(HttpClient http, SitecoreConnectionSettings settings, ILogger? logger = null)
    {
        _http = http;
        _settings = settings;
        _log = logger ?? Log.ForContext<SitecoreLayoutServiceClient>();

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(settings.BaseUrl))
            _http.BaseAddress = new Uri(SitecoreHttpFactory.NormalizeBaseUrl(settings.BaseUrl));
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            return ConnectionTestResult.Fail("Sitecore Base URL is empty.");
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
            return ConnectionTestResult.Fail("API key is required for Layout Service.");

        var url = BuildLayoutUrl("/");
        var absolute = ToAbsolute(url);
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = body.Length > 400 ? body[..400] + "…" : body;

            if (!response.IsSuccessStatusCode)
            {
                return ConnectionTestResult.Fail(
                    $"Layout Service HTTP {(int)response.StatusCode} {response.ReasonPhrase}. " +
                    "Confirm API key and that Headless Services are installed.",
                    (int)response.StatusCode,
                    absolute,
                    snippet);
            }

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("sitecore", out _))
            {
                return ConnectionTestResult.Fail(
                    "Layout Service returned JSON without a 'sitecore' root.",
                    (int)response.StatusCode,
                    absolute,
                    snippet);
            }

            return ConnectionTestResult.Ok(
                $"Connected via Layout Service (JSS). Database='{_settings.Database}'. " +
                "Note: full folder trees need Item Service — see Config/sitecore/zzz.AllowServicesClient.config.",
                absolute);
        }
        catch (HttpRequestException ex)
        {
            var ssl = ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                ? " Tip: enable Ignore SSL errors."
                : string.Empty;
            return ConnectionTestResult.Fail($"Network error: {ex.Message}.{ssl}", requestUrl: absolute);
        }
        catch (Exception ex)
        {
            return ConnectionTestResult.Fail($"Layout Service test failed: {ex.Message}", requestUrl: absolute);
        }
    }

    public async Task<SitecoreItemDto?> GetItemByPathAsync(string path, CancellationToken cancellationToken = default)
    {
        var layoutPath = ToLayoutItemParam(path);
        return await GetByLayoutParamAsync(layoutPath, path, cancellationToken);
    }

    public async Task<SitecoreItemDto?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await GetByLayoutParamAsync(id.ToString("B").ToUpperInvariant(), null, cancellationToken);
    }

    public Task<IReadOnlyList<SitecoreItemDto>> GetChildrenAsync(Guid parentId, CancellationToken cancellationToken = default)
    {
        // Layout Service cannot list children; subtree extraction handles components instead.
        return Task.FromResult<IReadOnlyList<SitecoreItemDto>>([]);
    }

    public async Task<IReadOnlyList<SitecoreItemDto>> GetSubtreeAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        progress?.Report($"Loading Layout Service item '{path}'...");
        var root = await GetItemByPathAsync(path, cancellationToken);
        if (root is null)
            return [];

        var results = new Dictionary<Guid, SitecoreItemDto> { [root.Id] = root };
        await CollectDatasourceItemsAsync(root, results, cancellationToken);
        progress?.Report($"Layout subtree complete: {results.Count} item(s).");
        return results.Values.ToList();
    }

    public async Task<SitecoreMediaBinary?> DownloadMediaAsync(Guid mediaItemId, CancellationToken cancellationToken = default)
    {
        var key = SitecoreHttpFactory.NormalizeApiKey(_settings.ApiKey).Trim('{', '}');
        var url = $"-/media/{mediaItemId:N}.ashx?sc_database={Uri.EscapeDataString(_settings.Database)}&sc_apikey={Uri.EscapeDataString(key)}";

        try
        {
            using var response = await _http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            var fileName = mediaItemId.ToString("N");
            var disposition = response.Content.Headers.ContentDisposition?.FileName?.Trim('"');
            if (!string.IsNullOrWhiteSpace(disposition))
                fileName = disposition;

            return new SitecoreMediaBinary(mediaItemId, fileName, contentType, bytes);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Media download failed for {Id}", mediaItemId);
            return null;
        }
    }

    public Task<SitecoreFlattenedTemplate?> GetFlattenedTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        // Layout Service does not expose template inheritance definitions.
        return Task.FromResult<SitecoreFlattenedTemplate?>(null);
    }

    private async Task CollectDatasourceItemsAsync(
        SitecoreItemDto parent,
        Dictionary<Guid, SitecoreItemDto> results,
        CancellationToken cancellationToken)
    {
        if (!parent.Fields.TryGetValue("__LayoutDatasources", out var dsField) ||
            string.IsNullOrWhiteSpace(dsField.Value))
            return;

        foreach (Match match in Regex.Matches(dsField.Value, @"\{?[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?"))
        {
            if (!Guid.TryParse(match.Value.Trim('{', '}'), out var id))
                continue;
            if (results.ContainsKey(id))
                continue;

            var item = await GetItemByIdAsync(id, cancellationToken);
            if (item is null)
                continue;

            results[id] = item;
            await CollectDatasourceItemsAsync(item, results, cancellationToken);
        }
    }

    private async Task<SitecoreItemDto?> GetByLayoutParamAsync(
        string itemParam,
        string? fallbackPath,
        CancellationToken cancellationToken)
    {
        var cacheKey = itemParam;
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var url = BuildLayoutUrl(itemParam);
        using var response = await _http.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Layout Service failed ({(int)response.StatusCode}) for item '{itemParam}': {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("sitecore", out var sitecore))
            return null;

        if (!sitecore.TryGetProperty("route", out var route) || route.ValueKind == JsonValueKind.Null)
            return null;

        var mapped = MapRoute(route, sitecore, fallbackPath);
        if (mapped is not null)
            _cache[cacheKey] = mapped;
        return mapped;
    }

    private SitecoreItemDto? MapRoute(JsonElement route, JsonElement sitecore, string? fallbackPath)
    {
        var name = ReadString(route, "name") ?? "item";
        var displayName = ReadString(route, "displayName") ?? name;
        var itemId = ReadGuid(route, "itemId", "itemID", "id") ?? Guid.NewGuid();
        var templateName = ReadString(route, "templateName") ?? "Unknown";
        var templateId = ReadGuid(route, "templateId", "templateID") ?? Guid.Empty;

        var path = fallbackPath
                   ?? ReadString(sitecore.GetProperty("context"), "itemPath")
                   ?? name;
        if (sitecore.TryGetProperty("context", out var ctx))
        {
            var ctxPath = ReadString(ctx, "itemPath");
            if (!string.IsNullOrWhiteSpace(ctxPath))
            {
                // Layout context itemPath is often site-relative ("/"); keep Sitecore-style when possible.
                path = ctxPath == "/" ? (fallbackPath ?? "/sitecore/content/Home") : ctxPath;
            }
        }

        var fields = new Dictionary<string, SitecoreFieldValue>(StringComparer.OrdinalIgnoreCase);
        if (route.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
                fields[prop.Name] = MapLayoutField(prop.Name, prop.Value);
        }

        // Collect datasource IDs from placeholders for subtree expansion.
        var datasourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (route.TryGetProperty("placeholders", out var placeholders))
            CollectDatasourceIds(placeholders, datasourceIds, fields);

        if (datasourceIds.Count > 0)
        {
            fields["__LayoutDatasources"] = new SitecoreFieldValue(
                "__LayoutDatasources",
                "text",
                string.Join("|", datasourceIds));
        }

        return new SitecoreItemDto(
            itemId,
            path,
            name,
            displayName,
            templateId,
            templateName,
            _settings.Language,
            fields,
            ChildIds: []);
    }

    private static void CollectDatasourceIds(
        JsonElement placeholders,
        HashSet<string> datasourceIds,
        Dictionary<string, SitecoreFieldValue> parentFields)
    {
        if (placeholders.ValueKind != JsonValueKind.Object)
            return;

        foreach (var ph in placeholders.EnumerateObject())
        {
            if (ph.Value.ValueKind != JsonValueKind.Array)
                continue;

            var index = 0;
            foreach (var component in ph.Value.EnumerateArray())
            {
                index++;
                var componentName = ReadString(component, "componentName") ?? $"component{index}";
                var uid = ReadString(component, "uid");
                var dataSource = ReadString(component, "dataSource");

                if (!string.IsNullOrWhiteSpace(dataSource) &&
                    Guid.TryParse(dataSource.Trim('{', '}'), out var dsId))
                {
                    datasourceIds.Add(dsId.ToString("B").ToUpperInvariant());
                }

                // Flatten component fields onto parent with a unique prefix so values aren't lost
                // when datasource fetch isn't possible.
                if (component.TryGetProperty("fields", out var cFields) && cFields.ValueKind == JsonValueKind.Object)
                {
                    foreach (var f in cFields.EnumerateObject())
                    {
                        var key = $"{ph.Name}:{componentName}:{f.Name}";
                        if (!parentFields.ContainsKey(key))
                            parentFields[key] = MapLayoutField(key, f.Value);
                    }
                }

                if (component.TryGetProperty("placeholders", out var nested))
                    CollectDatasourceIds(nested, datasourceIds, parentFields);
            }
        }
    }

    private static SitecoreFieldValue MapLayoutField(string name, JsonElement el)
    {
        // Layout fields are usually { "value": ... } and may be objects (image, link, etc.).
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty("value", out var valueEl))
        {
            var type = InferType(valueEl);
            var value = valueEl.ValueKind switch
            {
                JsonValueKind.String => valueEl.GetString(),
                JsonValueKind.Number => valueEl.ToString(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                JsonValueKind.Object or JsonValueKind.Array => valueEl.GetRawText(),
                _ => valueEl.ToString()
            };

            // Image fields often have { value: { src, mediaid, ... } }
            if (valueEl.ValueKind == JsonValueKind.Object && valueEl.TryGetProperty("mediaid", out var mediaId))
                value = mediaId.GetString() ?? value;
            if (valueEl.ValueKind == JsonValueKind.Object && valueEl.TryGetProperty("id", out var id) &&
                string.IsNullOrWhiteSpace(value))
                value = id.GetString();

            return new SitecoreFieldValue(name, type, value, value);
        }

        var raw = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
        return new SitecoreFieldValue(name, "Unknown", raw, raw);
    }

    private static string InferType(JsonElement valueEl) =>
        valueEl.ValueKind switch
        {
            JsonValueKind.Object when valueEl.TryGetProperty("src", out _) => "Image",
            JsonValueKind.Object when valueEl.TryGetProperty("href", out _) || valueEl.TryGetProperty("url", out _) => "General Link",
            JsonValueKind.True or JsonValueKind.False => "Checkbox",
            JsonValueKind.Number => "Number",
            _ => "text"
        };

    private string BuildLayoutUrl(string itemParam)
    {
        var key = SitecoreHttpFactory.NormalizeApiKey(_settings.ApiKey).Trim('{', '}');
        // Query-string API key is what succeeded against ricohcm.dev.local
        return
            "sitecore/api/layout/render/jss" +
            "?item=" + Uri.EscapeDataString(itemParam) +
            "&sc_apikey=" + Uri.EscapeDataString(key) +
            "&sc_database=" + Uri.EscapeDataString(_settings.Database) +
            "&sc_lang=" + Uri.EscapeDataString(_settings.Language);
    }

    /// <summary>
    /// Layout Service expects site-relative paths ("/About") or IDs, not always full Sitecore paths.
    /// </summary>
    private static string ToLayoutItemParam(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var p = path.Trim();
        if (Guid.TryParse(p.Trim('{', '}'), out var g))
            return g.ToString("B").ToUpperInvariant();

        // /sitecore/content/Home -> /
        // /sitecore/content/Home/About -> /About
        const string homePrefix = "/sitecore/content/home";
        if (p.Equals("/sitecore/content/home", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("/sitecore/content/Home", StringComparison.OrdinalIgnoreCase))
            return "/";

        if (p.StartsWith(homePrefix + "/", StringComparison.OrdinalIgnoreCase))
            return "/" + p[(homePrefix.Length + 1)..];

        if (p.StartsWith("/sitecore/content/", StringComparison.OrdinalIgnoreCase))
        {
            // /sitecore/content/Site/Home/X -> try /X after last Home segment if present
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var homeIdx = Array.FindIndex(parts, s => s.Equals("Home", StringComparison.OrdinalIgnoreCase));
            if (homeIdx >= 0 && homeIdx < parts.Length - 1)
                return "/" + string.Join('/', parts.Skip(homeIdx + 1));
            if (homeIdx >= 0)
                return "/";
        }

        return p.StartsWith('/') ? p : "/" + p;
    }

    private string ToAbsolute(string relative) =>
        _http.BaseAddress is null ? relative : new Uri(_http.BaseAddress, relative).ToString();

    private static string? ReadString(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        return null;
    }

    private static Guid? ReadGuid(JsonElement el, params string[] names)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!el.TryGetProperty(name, out var p))
                continue;
            var s = p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString();
            if (Guid.TryParse(s?.Trim('{', '}'), out var g))
                return g;
        }
        return null;
    }

    private static string Truncate(string value, int max = 500) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max] + "…";
}
