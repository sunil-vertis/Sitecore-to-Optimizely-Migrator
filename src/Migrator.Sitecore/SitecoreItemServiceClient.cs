using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Migrator.Core.Abstractions;
using Migrator.Core.Models;
using Serilog;

namespace Migrator.Sitecore;

/// <summary>
/// Sitecore Item Service REST client (ServicesClient / itemservice).
/// Inherited template fields are flattened onto the concrete template and appear as
/// direct fields on each item DTO.
/// </summary>
public sealed class SitecoreItemServiceClient : ISitecoreClient
{
    /// <summary>Sitecore Standard Template — excluded from inheritance flatten to avoid system noise.</summary>
    public static readonly Guid StandardTemplateId = Guid.Parse("1930BBEB-7805-471A-A3BE-4858AC7CF696");

    private readonly HttpClient _http;
    private readonly SitecoreConnectionSettings _settings;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, SitecoreFlattenedTemplate> _templateCache = new();
    private bool _sessionReady;

    public SitecoreItemServiceClient(HttpClient http, SitecoreConnectionSettings settings, ILogger? logger = null)
    {
        _http = http;
        _settings = settings;
        _log = logger ?? Log.ForContext<SitecoreItemServiceClient>();

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(settings.BaseUrl))
            _http.BaseAddress = new Uri(SitecoreHttpFactory.NormalizeBaseUrl(settings.BaseUrl));
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl))
            return ConnectionTestResult.Fail("Sitecore Base URL is empty.");

        if (_settings.AuthMode is SitecoreAuthMode.ApiKey or SitecoreAuthMode.ApiKeyAndBasic
            && string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return ConnectionTestResult.Fail(
                "API key is empty. Use the Item ID of an API Key under /sitecore/system/Settings/Services/API Keys.");
        }

        if (_settings.AuthMode is SitecoreAuthMode.Basic or SitecoreAuthMode.ApiKeyAndBasic
            && string.IsNullOrWhiteSpace(_settings.Username))
        {
            return ConnectionTestResult.Fail(
                "Username is required for session login (e.g. sitecore\\admin in Config/sitecore.secrets.json).");
        }

        var login = await EnsureSessionAsync(cancellationToken);
        if (!login.Success)
            return login;

        var probePaths = new[] { "/sitecore/content", "/sitecore", "/sitecore/content/Home" };
        ConnectionTestResult? lastFail = null;

        foreach (var path in probePaths)
        {
            var url = BuildItemByPathUrl(path);
            try
            {
                using var response = await _http.GetAsync(url, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var snippet = body.Length > 400 ? body[..400] + "…" : body;
                var absolute = _http.BaseAddress is null
                    ? url
                    : new Uri(_http.BaseAddress, url).ToString();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                    var item = MapItem(doc.RootElement, path);
                    if (item is not null)
                    {
                        return ConnectionTestResult.Ok(
                            $"Connected. Read '{item.Path}' ({item.Name}) from database '{_settings.Database}'.",
                            absolute);
                    }

                    lastFail = ConnectionTestResult.Fail(
                        $"HTTP {(int)response.StatusCode} but could not parse item JSON for path '{path}'.",
                        (int)response.StatusCode, absolute, snippet);
                    continue;
                }

                var hint = response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                        " Anonymous Item Service is disabled on this CM. A browser call works only while you are logged in. Deploy Config/sitecore/zzz.AllowServicesClient.config (sets Sitecore.Services.AllowItemServiceAnonymousUser to true) and recycle the CM app pool.",
                    System.Net.HttpStatusCode.NotFound =>
                        " Route/path not found — Base URL must be CM host only (https://cm.site.com), and Sitecore Services Client must be enabled.",
                    System.Net.HttpStatusCode.BadRequest =>
                        " Bad request — often caused by sending sc_apikey in both header and query. Check database/language if the key is only sent once.",
                    _ => string.Empty
                };

                lastFail = ConnectionTestResult.Fail(
                    $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for '{path}'.{hint}",
                    (int)response.StatusCode, absolute, snippet);

                if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
                    return lastFail;
            }
            catch (HttpRequestException ex)
            {
                var sslHint = ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                              || ex.Message.Contains("certificate", StringComparison.OrdinalIgnoreCase)
                    ? " Tip: enable 'Ignore SSL errors' for local/self-signed CM certificates."
                    : string.Empty;
                lastFail = ConnectionTestResult.Fail(
                    $"Network error: {ex.Message}.{sslHint}" +
                    (ex.InnerException is null ? string.Empty : $" ({ex.InnerException.Message})"),
                    requestUrl: _http.BaseAddress?.ToString());
                _log.Warning(ex, "Sitecore connection network error");
            }
            catch (TaskCanceledException)
            {
                lastFail = ConnectionTestResult.Fail(
                    "Request timed out — check Base URL, VPN/firewall, and CM reachability.",
                    requestUrl: _http.BaseAddress?.ToString());
            }
            catch (Exception ex)
            {
                lastFail = ConnectionTestResult.Fail($"Unexpected error: {ex.Message}");
                _log.Warning(ex, "Sitecore connection test failed");
            }
        }

        return lastFail ?? ConnectionTestResult.Fail("Sitecore connection failed.");
    }

    private async Task<ConnectionTestResult> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (_sessionReady || !SitecoreSscLogin.RequiresSession(_settings))
        {
            _sessionReady = true;
            return ConnectionTestResult.Ok("Session ready.");
        }

        var login = await SitecoreSscLogin.EnsureLoggedInAsync(_http, _settings, _log, cancellationToken);
        if (login.Success)
            _sessionReady = true;
        return login;
    }

    private async Task EnsureSessionOrThrowAsync(CancellationToken cancellationToken)
    {
        var login = await EnsureSessionAsync(cancellationToken);
        if (!login.Success)
            throw new InvalidOperationException(login.Message);
    }

    public Task<SitecoreItemDto?> GetItemByPathAsync(string path, CancellationToken cancellationToken = default) =>
        GetItemByPathAsync(path, enrich: true, cancellationToken);

    private async Task<SitecoreItemDto?> GetItemByPathAsync(
        string path,
        bool enrich,
        CancellationToken cancellationToken)
    {
        await EnsureSessionOrThrowAsync(cancellationToken);
        var url = BuildItemByPathUrl(path);

        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Sitecore GET item by path failed ({(int)response.StatusCode}): {Truncate(body)}");

        using var doc = JsonDocument.Parse(body);
        var item = MapItem(doc.RootElement, path);
        if (item is null)
            return null;
        return enrich ? await EnrichWithInheritedFieldsAsync(item, cancellationToken) : item;
    }

    private string BuildItemByPathUrl(string path)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? "/sitecore" : path.Trim();
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        return WithApiKey(
            "sitecore/api/ssc/item?path=" + Uri.EscapeDataString(normalized) +
            "&database=" + Uri.EscapeDataString(_settings.Database) +
            "&language=" + Uri.EscapeDataString(_settings.Language) +
            // Do not append fields=* — on this CM it returns empty {} while omitting it returns all fields.
            "&includeStandardTemplateFields=true");
    }

    private string WithApiKey(string relativeUrl)
    {
        if (_settings.AuthMode is not (SitecoreAuthMode.ApiKey or SitecoreAuthMode.ApiKeyAndBasic))
            return relativeUrl;

        var key = SitecoreHttpFactory.NormalizeApiKey(_settings.ApiKey).Trim('{', '}');
        if (string.IsNullOrEmpty(key))
            return relativeUrl;

        var separator = relativeUrl.Contains('?') ? "&" : "?";
        return relativeUrl + separator + "sc_apikey=" + Uri.EscapeDataString(key);
    }

    private static string Truncate(string value, int max = 500) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= max ? value : value[..max] + "…";

    public async Task<SitecoreItemDto?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureSessionOrThrowAsync(cancellationToken);
        var url = WithApiKey(
            $"sitecore/api/ssc/item/{id:D}?database={Uri.EscapeDataString(_settings.Database)}&language={Uri.EscapeDataString(_settings.Language)}&includeStandardTemplateFields=true");

        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var item = MapItem(doc.RootElement, null);
        return item is null ? null : await EnrichWithInheritedFieldsAsync(item, cancellationToken);
    }

    public async Task<IReadOnlyList<SitecoreItemDto>> GetChildrenAsync(Guid parentId, CancellationToken cancellationToken = default)
    {
        await EnsureSessionOrThrowAsync(cancellationToken);
        var url = WithApiKey(
            $"sitecore/api/ssc/item/{parentId:D}/children?database={Uri.EscapeDataString(_settings.Database)}&language={Uri.EscapeDataString(_settings.Language)}&includeStandardTemplateFields=true");

        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        // Do not enrich here — subtree walks can hit hundreds of items; inheritance flatten
        // is applied later only for items that are actually migrated.
        var list = new List<SitecoreItemDto>();
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var mapped = MapItem(el, null);
                if (mapped is not null)
                    list.Add(mapped);
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<SitecoreItemDto>> GetSubtreeAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null)
    {
        progress?.Report($"Loading root '{path}'...");
        // Root without inheritance flatten — same as children; migrate path re-fetches mapped items.
        var root = await GetItemByPathAsync(path, enrich: false, cancellationToken);
        if (root is null)
            return [];

        var results = new List<SitecoreItemDto> { root };
        var queue = new Queue<SitecoreItemDto>();
        queue.Enqueue(root);
        progress?.Report($"Loaded 1 item: {root.Path}");

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = queue.Dequeue();
            progress?.Report($"Listing children of '{current.Name}' ({results.Count} loaded, {queue.Count} queued)...");
            var children = await GetChildrenAsync(current.Id, cancellationToken);
            foreach (var child in children)
            {
                results.Add(child);
                // Descend into containers; skip walking under product-like leaves that already have
                // a concrete commerce template (their configurator children are usually unmapped).
                var looksLikeLeafProduct = child.TemplateName.Contains("Product", StringComparison.OrdinalIgnoreCase)
                    && !child.TemplateName.Contains("Folder", StringComparison.OrdinalIgnoreCase)
                    && !child.TemplateName.Contains("Category", StringComparison.OrdinalIgnoreCase);
                if (!looksLikeLeafProduct)
                    queue.Enqueue(child);
            }

            if (results.Count % 10 == 0 || children.Count == 0)
                progress?.Report($"Loaded {results.Count} item(s) under '{path}'...");
        }

        progress?.Report($"Subtree complete: {results.Count} item(s).");
        return results;
    }

    public async Task<SitecoreMediaBinary?> DownloadMediaAsync(Guid mediaItemId, CancellationToken cancellationToken = default)
    {
        var item = await GetItemByIdAsync(mediaItemId, cancellationToken);
        if (item is null)
            return null;

        var mediaUrl = item.MediaUrl;
        if (string.IsNullOrWhiteSpace(mediaUrl))
            mediaUrl = $"-/media/{mediaItemId:N}.ashx";

        if (!Uri.TryCreate(mediaUrl, UriKind.Absolute, out _))
            mediaUrl = mediaUrl.TrimStart('/');

        using var response = await _http.GetAsync(mediaUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? item.MimeType ?? "application/octet-stream";
        var fileName = item.Name;
        if (!Path.HasExtension(fileName) && item.Fields.TryGetValue("Extension", out var ext) && !string.IsNullOrWhiteSpace(ext.Value))
            fileName = $"{fileName}.{ext.Value.Trim('.')}";

        return new SitecoreMediaBinary(mediaItemId, fileName, contentType, bytes);
    }

    public async Task<SitecoreFlattenedTemplate?> GetFlattenedTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default)
    {
        if (templateId == Guid.Empty)
            return null;

        if (_templateCache.TryGetValue(templateId, out var cached))
            return cached;

        var flattened = await BuildFlattenedTemplateAsync(templateId, new HashSet<Guid>(), cancellationToken);
        if (flattened is not null)
            _templateCache[templateId] = flattened;
        return flattened;
    }

    /// <summary>
    /// Merges flattened (own + inherited) template fields onto the item as direct fields.
    /// Existing values win; missing inherited fields are added with null value so mapping can target them.
    /// </summary>
    private async Task<SitecoreItemDto> EnrichWithInheritedFieldsAsync(
        SitecoreItemDto item,
        CancellationToken cancellationToken)
    {
        if (item.TemplateId == Guid.Empty)
            return item;

        var flattened = await GetFlattenedTemplateAsync(item.TemplateId, cancellationToken);
        if (flattened is null || flattened.Fields.Count == 0)
            return item with { BaseTemplateIds = flattened?.BaseTemplateIds };

        var fields = new Dictionary<string, SitecoreFieldValue>(item.Fields, StringComparer.OrdinalIgnoreCase);
        var byName = flattened.Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Mark existing fields that come from a base template, and fill gaps.
        foreach (var def in byName.Values)
        {
            if (fields.TryGetValue(def.Name, out var existing))
            {
                fields[def.Name] = existing with
                {
                    IsInherited = def.IsInherited,
                    DeclaringTemplateName = def.DeclaringTemplateName,
                    Type = string.IsNullOrWhiteSpace(existing.Type) || existing.Type == "Unknown"
                        ? def.Type
                        : existing.Type
                };
            }
            else
            {
                fields[def.Name] = new SitecoreFieldValue(
                    def.Name,
                    def.Type,
                    Value: null,
                    RawValue: null,
                    IsInherited: def.IsInherited,
                    DeclaringTemplateName: def.DeclaringTemplateName);
            }
        }

        _log.Debug(
            "Template {Template} flattened: {FieldCount} field(s), {BaseCount} base template(s), {InheritedCount} inherited",
            item.TemplateName,
            flattened.Fields.Count,
            flattened.BaseTemplateIds.Count,
            flattened.Fields.Count(f => f.IsInherited));

        return item with
        {
            Fields = fields,
            BaseTemplateIds = flattened.BaseTemplateIds,
            TemplateName = string.IsNullOrWhiteSpace(item.TemplateName) ? flattened.TemplateName : item.TemplateName
        };
    }

    private async Task<SitecoreFlattenedTemplate?> BuildFlattenedTemplateAsync(
        Guid templateId,
        HashSet<Guid> visited,
        CancellationToken cancellationToken)
    {
        if (templateId == Guid.Empty || templateId == StandardTemplateId)
            return null;

        if (!visited.Add(templateId))
            return null; // cycle

        var templateItem = await GetRawItemByIdAsync(templateId, cancellationToken);
        if (templateItem is null)
        {
            _log.Warning("Could not load Sitecore template {TemplateId}", templateId);
            return null;
        }

        var templateName = templateItem.Name;
        var baseIds = ParseBaseTemplateIds(templateItem);
        var fields = new List<SitecoreTemplateFieldDefinition>();

        // Bases first, then own fields override by name (Sitecore effective fields).
        var orderedBases = new List<Guid>();
        foreach (var baseId in baseIds)
        {
            if (baseId == StandardTemplateId)
                continue;

            var baseFlat = await BuildFlattenedTemplateAsync(baseId, visited, cancellationToken);
            if (baseFlat is null)
                continue;

            orderedBases.Add(baseId);
            orderedBases.AddRange(baseFlat.BaseTemplateIds);

            foreach (var field in baseFlat.Fields)
            {
                fields.Add(field with { IsInherited = true });
            }
        }

        // Own fields (template sections → field items)
        var ownFields = await GetOwnTemplateFieldsAsync(templateId, templateName, cancellationToken);
        foreach (var own in ownFields)
        {
            fields.RemoveAll(f => string.Equals(f.Name, own.Name, StringComparison.OrdinalIgnoreCase));
            fields.Add(own);
        }

        // Deduplicate base id list while preserving order
        var uniqueBases = orderedBases.Distinct().ToList();

        return new SitecoreFlattenedTemplate(templateId, templateName, uniqueBases, fields);
    }

    private async Task<IReadOnlyList<SitecoreTemplateFieldDefinition>> GetOwnTemplateFieldsAsync(
        Guid templateId,
        string templateName,
        CancellationToken cancellationToken)
    {
        var result = new List<SitecoreTemplateFieldDefinition>();

        // Template children = sections; section children = fields
        var sections = await GetRawChildrenAsync(templateId, cancellationToken);
        foreach (var section in sections)
        {
            // Skip __Standard Values and other non-section system children
            if (section.Name.StartsWith("__", StringComparison.Ordinal))
                continue;

            var fieldItems = await GetRawChildrenAsync(section.Id, cancellationToken);
            foreach (var fieldItem in fieldItems)
            {
                if (fieldItem.Name.StartsWith("__", StringComparison.Ordinal))
                    continue;

                var type = "Unknown";
                if (fieldItem.Fields.TryGetValue("Type", out var typeField) && !string.IsNullOrWhiteSpace(typeField.Value))
                    type = typeField.Value!;

                result.Add(new SitecoreTemplateFieldDefinition(
                    fieldItem.Name,
                    type,
                    templateId,
                    templateName,
                    IsInherited: false));
            }
        }

        return result;
    }

    private static List<Guid> ParseBaseTemplateIds(SitecoreItemDto templateItem)
    {
        var ids = new List<Guid>();

        // Prefer __Base template field
        if (!templateItem.Fields.TryGetValue("__Base template", out var baseField) ||
            string.IsNullOrWhiteSpace(baseField.Value))
        {
            // Some Item Service payloads expose BaseTemplates as a property-like field
            templateItem.Fields.TryGetValue("BaseTemplates", out baseField);
        }

        if (baseField is null || string.IsNullOrWhiteSpace(baseField.Value))
            return ids;

        foreach (Match match in Regex.Matches(
                     baseField.Value,
                     @"\{?[0-9A-Fa-f]{8}(?:-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}?"))
        {
            if (Guid.TryParse(match.Value.Trim('{', '}'), out var g) && g != Guid.Empty)
                ids.Add(g);
        }

        return ids;
    }

    /// <summary>Loads an item without inheritance enrichment (used for template meta walks).</summary>
    private async Task<SitecoreItemDto?> GetRawItemByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        var url = WithApiKey(
            $"sitecore/api/ssc/item/{id:D}?database={Uri.EscapeDataString(_settings.Database)}&language={Uri.EscapeDataString(_settings.Language)}&includeStandardTemplateFields=true");

        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return MapItem(doc.RootElement, null);
    }

    private async Task<IReadOnlyList<SitecoreItemDto>> GetRawChildrenAsync(Guid parentId, CancellationToken cancellationToken)
    {
        var url = WithApiKey(
            $"sitecore/api/ssc/item/{parentId:D}/children?database={Uri.EscapeDataString(_settings.Database)}&language={Uri.EscapeDataString(_settings.Language)}&includeStandardTemplateFields=true");

        using var response = await _http.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return [];

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var list = new List<SitecoreItemDto>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var mapped = MapItem(el, null);
            if (mapped is not null)
                list.Add(mapped);
        }

        return list;
    }

    private SitecoreItemDto? MapItem(JsonElement el, string? fallbackPath)
    {
        if (el.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadGuid(el, "ItemID", "ID", "Id") ?? Guid.Empty;
        if (id == Guid.Empty)
            return null;

        var name = ReadString(el, "ItemName", "Name") ?? id.ToString("D");
        var displayName = ReadString(el, "DisplayName") ?? name;
        var path = ReadString(el, "Path", "ItemPath") ?? fallbackPath ?? string.Empty;
        var templateId = ReadGuid(el, "TemplateID", "TemplateId") ?? Guid.Empty;
        var templateName = ReadString(el, "TemplateName") ?? string.Empty;
        var language = ReadString(el, "Language") ?? _settings.Language;

        var fields = new Dictionary<string, SitecoreFieldValue>(StringComparer.OrdinalIgnoreCase);

        if (el.TryGetProperty("Fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in fieldsEl.EnumerateObject())
            {
                var value = ExtractFieldValue(prop.Value);
                var type = prop.Value.ValueKind == JsonValueKind.Object && prop.Value.TryGetProperty("Type", out var t)
                    ? t.GetString() ?? "Unknown"
                    : "Unknown";
                fields[prop.Name] = new SitecoreFieldValue(prop.Name, type, value, value);
            }
        }
        else
        {
            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ItemID", "ID", "Id", "ItemName", "Name", "DisplayName", "Path", "ItemPath",
                "TemplateID", "TemplateId", "TemplateName", "Language", "ParentID", "HasChildren",
                "Version", "Database", "Fields", "Children"
            };

            foreach (var prop in el.EnumerateObject())
            {
                if (skip.Contains(prop.Name))
                    continue;

                var value = ExtractFieldValue(prop.Value);
                fields[prop.Name] = new SitecoreFieldValue(prop.Name, "Unknown", value, value);
            }
        }

        var childIds = new List<Guid>();
        if (el.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in children.EnumerateArray())
            {
                if (c.ValueKind == JsonValueKind.String && Guid.TryParse(c.GetString()?.Trim('{', '}'), out var childFromString))
                {
                    childIds.Add(childFromString);
                }
                else if (c.ValueKind == JsonValueKind.Object)
                {
                    var childFromObject = ReadGuid(c, "ItemID", "ID", "Id");
                    if (childFromObject.HasValue)
                        childIds.Add(childFromObject.Value);
                }
            }
        }

        var isMedia = templateName.Contains("media", StringComparison.OrdinalIgnoreCase)
                      || path.Contains("/media library/", StringComparison.OrdinalIgnoreCase);
        var mime = fields.TryGetValue("Mime Type", out var mimeField) ? mimeField.Value : null;
        var mediaUrl = fields.TryGetValue("Url", out var urlField) ? urlField.Value : null;

        return new SitecoreItemDto(
            id,
            path,
            name,
            displayName,
            templateId,
            templateName,
            language,
            fields,
            childIds,
            isMedia,
            mediaUrl,
            mime);
    }

    private static string? ExtractFieldValue(JsonElement el) =>
        el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.ToString(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            JsonValueKind.Object when el.TryGetProperty("Value", out var v) => ExtractFieldValue(v),
            JsonValueKind.Null => null,
            _ => el.ToString()
        };

    private static string? ReadString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            if (el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String)
                return p.GetString();
        }
        return null;
    }

    private static Guid? ReadGuid(JsonElement el, params string[] names)
    {
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

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
