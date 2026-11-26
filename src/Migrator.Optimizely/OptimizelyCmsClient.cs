using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Migrator.Core.Abstractions;
using Migrator.Core.Models;
using Serilog;

namespace Migrator.Optimizely;

/// <summary>
/// Optimizely CMS SaaS Content Management REST API client.
/// Auth: OAuth2 client_credentials against /oauth/token with scope api:admin.
/// Docs: https://docs.optimizely.com/cms-saas/docs/manage-content-using-the-rest-api
/// </summary>
public sealed class OptimizelyCmsClient : IOptimizelyClient
{
    private readonly HttpClient _http;
    private readonly OptimizelyConnectionSettings _settings;
    private readonly ILogger _log;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public OptimizelyCmsClient(HttpClient http, OptimizelyConnectionSettings settings, ILogger? logger = null)
    {
        _http = http;
        _settings = settings;
        _log = logger ?? Log.ForContext<OptimizelyCmsClient>();

        if (_http.BaseAddress is null && !string.IsNullOrWhiteSpace(settings.ApiBaseUrl))
            _http.BaseAddress = new Uri(EnsureTrailingSlash(settings.ApiBaseUrl));
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureTokenAsync(cancellationToken);
            return !string.IsNullOrEmpty(_accessToken);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Optimizely connection test failed");
            return false;
        }
    }

    public async Task<OptimizelyContentResult> CreateContentAsync(
        OptimizelyCreateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        await EnsureTokenAsync(cancellationToken);

        var properties = new Dictionary<string, object?>();
        foreach (var kv in request.Properties)
            properties[kv.Key] = new Dictionary<string, object?> { ["value"] = kv.Value };

        var body = new Dictionary<string, object?>
        {
            ["contentType"] = request.ContentType,
            ["container"] = request.Container,
            ["initialVersion"] = new Dictionary<string, object?>
            {
                ["displayName"] = request.DisplayName,
                ["locale"] = request.Locale,
                ["properties"] = properties
            }
        };

        if (!string.IsNullOrWhiteSpace(request.Key))
            body["key"] = request.Key;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "content")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        httpRequest.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Create content failed ({(int)response.StatusCode}): {payload}");

        using var doc = JsonDocument.Parse(payload);
        var key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"Create content response missing key: {payload}");

        return new OptimizelyContentResult
        {
            Key = key,
            ContentType = request.ContentType,
            DisplayName = request.DisplayName
        };
    }

    public async Task<OptimizelyContentResult> UpdateContentAsync(
        string contentKey,
        string locale,
        string displayName,
        Dictionary<string, object?> properties,
        CancellationToken cancellationToken = default)
    {
        await EnsureTokenAsync(cancellationToken);

        var props = new Dictionary<string, object?>();
        foreach (var kv in properties)
            props[kv.Key] = new Dictionary<string, object?> { ["value"] = kv.Value };

        var body = new Dictionary<string, object?>
        {
            ["displayName"] = displayName,
            ["locale"] = locale,
            ["properties"] = props
        };

        // Create a new draft version then (optionally) rely on CMS UI/workflow to publish.
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"content/{contentKey}/versions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        httpRequest.Headers.TryAddWithoutValidation("Prefer", "return=representation");

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Update content failed ({(int)response.StatusCode}): {payload}");

        return new OptimizelyContentResult
        {
            Key = contentKey,
            DisplayName = displayName
        };
    }

    public async Task<OptimizelyMediaUploadResult> UploadMediaAsync(
        string fileName,
        string contentType,
        byte[] data,
        string containerKey,
        CancellationToken cancellationToken = default)
    {
        await EnsureTokenAsync(cancellationToken);

        // CMS SaaS media is typically created as content with a binary property.
        // Strategy: create an Image/media content type under container, then attach blob if endpoint available.
        // Fallback path used here: create content with displayName = fileName; blob upload endpoint may vary by instance.
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(containerKey), "container");
        form.Add(new StringContent(fileName), "displayName");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "media")
        {
            Content = form
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var response = await _http.SendAsync(httpRequest, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Fallback: create as generic content so migration can continue; operator can re-upload media later.
            _log.Warning("Media upload endpoint failed ({Status}): {Body}. Falling back to content create.", (int)response.StatusCode, payload);

            var created = await CreateContentAsync(new OptimizelyCreateContentRequest
            {
                ContentType = "Image",
                Container = containerKey,
                DisplayName = fileName,
                Locale = _settings.Locale,
                Properties = new Dictionary<string, object?>
                {
                    ["altText"] = fileName
                }
            }, cancellationToken);

            return new OptimizelyMediaUploadResult { Key = created.Key };
        }

        using var doc = JsonDocument.Parse(payload);
        var key = doc.RootElement.TryGetProperty("key", out var k) ? k.GetString() : null;
        if (string.IsNullOrEmpty(key))
            throw new InvalidOperationException($"Media upload response missing key: {payload}");
        var url = doc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;

        return new OptimizelyMediaUploadResult { Key = key, Url = url };
    }

    private async Task EnsureTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiresAt.AddMinutes(-2))
            return;

        if (string.IsNullOrWhiteSpace(_settings.ClientId) || string.IsNullOrWhiteSpace(_settings.ClientSecret))
            throw new InvalidOperationException("Optimizely ClientId and ClientSecret are required.");

        using var tokenClient = new HttpClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _settings.ClientId,
            ["client_secret"] = _settings.ClientSecret,
            ["scope"] = _settings.Scope
        });

        using var response = await tokenClient.PostAsync(_settings.TokenUrl, content, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OAuth token request failed ({(int)response.StatusCode}): {payload}");

        using var doc = JsonDocument.Parse(payload);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

        if (string.IsNullOrEmpty(_accessToken))
            throw new InvalidOperationException("OAuth response did not include access_token.");
    }

    private static string EnsureTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";
}
