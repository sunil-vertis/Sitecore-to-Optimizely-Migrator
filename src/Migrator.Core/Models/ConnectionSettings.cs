namespace Migrator.Core.Models;

/// <summary>
/// How to authenticate to Sitecore Item Service / Services Client.
/// </summary>
public enum SitecoreAuthMode
{
    /// <summary>Header sc_apikey only (typical for Sitecore API Key items).</summary>
    ApiKey = 0,

    /// <summary>SSC /auth/login with Sitecore username/password (domain\user supported), then cookie session.</summary>
    Basic = 1,

    /// <summary>Both sc_apikey and SSC username/password login.</summary>
    ApiKeyAndBasic = 2
}

/// <summary>
/// Which Sitecore HTTP API to use for reading content.
/// </summary>
public enum SitecoreTransport
{
    /// <summary>Try Item Service first; if 403, fall back to Layout Service (JSS).</summary>
    Auto = 0,

    /// <summary>Sitecore Services Client Item Service (/sitecore/api/ssc).</summary>
    ItemService = 1,

    /// <summary>Headless Layout Service (/sitecore/api/layout/render/jss) — works with JSS API keys.</summary>
    LayoutService = 2
}

public sealed class SitecoreConnectionSettings
{
    /// <summary>CM host only, e.g. https://cm.mysite.com — no /sitecore path suffix.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>API Key item ID from /sitecore/system/Settings/Services/API Keys (with or without braces).</summary>
    public string ApiKey { get; set; } = string.Empty;

    public string Database { get; set; } = "master";
    public string Language { get; set; } = "en";

    public SitecoreAuthMode AuthMode { get; set; } = SitecoreAuthMode.ApiKey;

    public SitecoreTransport Transport { get; set; } = SitecoreTransport.LayoutService;

    /// <summary>Sitecore user from Config/sitecore.secrets.json, e.g. sitecore\admin.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Sitecore password from Config/sitecore.secrets.json.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Allow self-signed / invalid TLS certs (local/dev CM only).</summary>
    public bool IgnoreSslErrors { get; set; }
}

/// <summary>
/// Sitecore Basic-auth credentials. Loaded only from Config/sitecore.secrets.json.
/// </summary>
public sealed class SitecoreCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>Absolute path of the file these values were read from. Not part of the JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;
}

/// <summary>
/// Optimizely CMS SaaS OAuth credentials. Loaded only from Config/optimizely.secrets.json.
/// </summary>
public sealed class OptimizelyCredentials
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Absolute path of the file these values were read from. Not part of the JSON.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string SourcePath { get; set; } = string.Empty;
}

public sealed class OptimizelyConnectionSettings
{
    public string ApiBaseUrl { get; set; } = "https://api.cms.optimizely.com/v1";
    public string TokenUrl { get; set; } = "https://api.cms.optimizely.com/oauth/token";
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string Scope { get; set; } = "api:admin";
    public string Locale { get; set; } = "en";
}

public sealed class PathSettings
{
    public string SitecoreSourcePath { get; set; } = "/sitecore/content";
    public string OptimizelyTargetContainerKey { get; set; } = string.Empty;
    public string OptimizelyTargetPathLabel { get; set; } = string.Empty;

    /// <summary>When true, only the Sitecore path item is migrated — children are not walked.</summary>
    public bool SingleItemOnly { get; set; }
}
