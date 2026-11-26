using System.Net.Http.Headers;
using Migrator.Core.Abstractions;
using Migrator.Core.Models;

namespace Migrator.Sitecore;

public static class SitecoreHttpFactory
{
    public static HttpClient Create(
        SitecoreConnectionSettings settings,
        TimeSpan? timeout = null,
        bool includeApiKeyHeader = true)
    {
        var handler = new HttpClientHandler
        {
            // Required for SSC username/password login cookies.
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };

        if (settings.IgnoreSslErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var http = new HttpClient(handler)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
            BaseAddress = new Uri(NormalizeBaseUrl(settings.BaseUrl))
        };

        // Layout Service on some CMs rejects/dupes when sc_apikey is both header + query.
        // Username/password uses SSC /auth/login + cookies (see SitecoreSscLogin), not HTTP Basic.
        ApplyAuth(http, settings, includeApiKeyHeader);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http;
    }

    public static ISitecoreClient CreateClient(SitecoreConnectionSettings settings, TimeSpan? timeout = null)
    {
        var transport = settings.Transport;
        if (transport == SitecoreTransport.Auto)
            transport = SitecoreTransport.LayoutService; // safest default when SSC returns 403

        // Always put sc_apikey on the query string only. Header + query returns HTTP 400 on ricohcm.
        return transport switch
        {
            SitecoreTransport.ItemService => new SitecoreItemServiceClient(
                Create(settings, timeout, includeApiKeyHeader: false), settings),
            _ => new SitecoreLayoutServiceClient(
                Create(settings, timeout, includeApiKeyHeader: false), settings)
        };
    }

    public static string NormalizeBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new InvalidOperationException("Sitecore Base URL is required (e.g. https://cm.mysite.com).");

        var url = baseUrl.Trim().TrimEnd('/');

        // Users often paste .../sitecore or .../sitecore/api — strip back to host root.
        foreach (var suffix in new[]
                 {
                     "/sitecore/api/ssc/item",
                     "/sitecore/api/ssc",
                     "/sitecore/api",
                     "/sitecore"
                 })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length];
                break;
            }
        }

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        return url.EndsWith('/') ? url : url + "/";
    }

    public static string NormalizeApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return string.Empty;

        var trimmed = apiKey.Trim();
        if (Guid.TryParse(trimmed.Trim('{', '}'), out var g))
            return "{" + g.ToString("D").ToUpperInvariant() + "}";

        return trimmed;
    }

    private static void ApplyAuth(HttpClient http, SitecoreConnectionSettings settings, bool includeApiKeyHeader)
    {
        var key = NormalizeApiKey(settings.ApiKey);
        var useApiKey = settings.AuthMode is SitecoreAuthMode.ApiKey or SitecoreAuthMode.ApiKeyAndBasic;
        var useBasic = settings.AuthMode is SitecoreAuthMode.Basic or SitecoreAuthMode.ApiKeyAndBasic;

        if (includeApiKeyHeader && useApiKey && !string.IsNullOrEmpty(key))
        {
            // Sitecore Services Client / Item Service — do NOT also send Bearer for API keys.
            http.DefaultRequestHeaders.Remove("sc_apikey");
            http.DefaultRequestHeaders.Add("sc_apikey", key);
        }

        // Intentionally no HTTP Basic here. Identity Server CMs ignore Basic for SSC and
        // still treat the caller as anonymous. Use SitecoreSscLogin instead.
        _ = useBasic;
    }
}
