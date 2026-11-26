using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Migrator.Core.Models;
using Serilog;

namespace Migrator.Sitecore;

/// <summary>
/// Logs into Sitecore Services Client via POST /sitecore/api/ssc/auth/login and keeps the session cookie.
/// HTTP Basic alone does not authenticate Item Service on Identity Server CMs.
/// </summary>
public static class SitecoreSscLogin
{
    public static bool RequiresSession(SitecoreConnectionSettings settings) =>
        settings.AuthMode is SitecoreAuthMode.Basic or SitecoreAuthMode.ApiKeyAndBasic
        && !string.IsNullOrWhiteSpace(settings.Username);

    public static async Task<ConnectionTestResult> EnsureLoggedInAsync(
        HttpClient http,
        SitecoreConnectionSettings settings,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        if (!RequiresSession(settings))
            return ConnectionTestResult.Ok("Session login not required for this auth mode.");

        var log = logger ?? Log.ForContext(typeof(SitecoreSscLogin));
        var (domain, username) = SplitUser(settings.Username);
        var loginUrl = "sitecore/api/ssc/auth/login";
        if (settings.AuthMode is SitecoreAuthMode.ApiKeyAndBasic)
        {
            var key = SitecoreHttpFactory.NormalizeApiKey(settings.ApiKey).Trim('{', '}');
            if (!string.IsNullOrEmpty(key))
                loginUrl += "?sc_apikey=" + Uri.EscapeDataString(key);
        }

        var absolute = http.BaseAddress is null
            ? loginUrl
            : new Uri(http.BaseAddress, loginUrl).ToString();

        var body = JsonSerializer.Serialize(new
        {
            domain,
            username,
            password = settings.Password
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            var snippet = payload.Length > 400 ? payload[..400] + "…" : payload;

            if (response.IsSuccessStatusCode)
            {
                log.Information("Sitecore SSC login OK for {Domain}\\{User}", domain, username);
                return ConnectionTestResult.Ok(
                    $"Logged in as {domain}\\{username}.",
                    absolute);
            }

            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden =>
                    " SSC login is blocked or credentials are wrong. Production often keeps ServicesLocalOnlyPolicy / anonymous Item Service off — username/password alone may still not be enough.",
                System.Net.HttpStatusCode.NotFound =>
                    " SSC auth/login route not found — Services Client may be disabled.",
                _ => string.Empty
            };

            return ConnectionTestResult.Fail(
                $"SSC login HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {domain}\\{username}.{hint}",
                (int)response.StatusCode,
                absolute,
                snippet);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Sitecore SSC login failed");
            return ConnectionTestResult.Fail($"SSC login error: {ex.Message}", requestUrl: absolute);
        }
    }

    public static (string Domain, string Username) SplitUser(string raw)
    {
        var value = raw.Trim();
        var idx = value.IndexOf('\\');
        if (idx > 0 && idx < value.Length - 1)
            return (value[..idx], value[(idx + 1)..]);

        idx = value.IndexOf('/');
        if (idx > 0 && idx < value.Length - 1)
            return (value[..idx], value[(idx + 1)..]);

        return ("sitecore", value);
    }
}
