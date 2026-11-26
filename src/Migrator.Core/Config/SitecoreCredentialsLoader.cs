using System.Text.Json;
using Migrator.Core.Models;

namespace Migrator.Core.Config;

public static class SitecoreCredentialsLoader
{
    public const string FileName = "sitecore.secrets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static SitecoreCredentials Load(string? appRoot = null)
    {
        var path = Find(appRoot);
        if (path is null)
        {
            throw new FileNotFoundException(
                "Sitecore username and password must be set in Config/sitecore.secrets.json. " +
                "Copy Config/sitecore.secrets.sample.json and fill in username and password.");
        }

        var json = File.ReadAllText(path);
        var credentials = JsonSerializer.Deserialize<SitecoreCredentials>(json, JsonOptions)
                          ?? throw new InvalidOperationException($"Sitecore credentials file is empty: {path}");

        credentials.Username = credentials.Username.Trim();
        credentials.SourcePath = path;
        return credentials;
    }

    public static string? Find(string? appRoot = null)
    {
        var start = string.IsNullOrWhiteSpace(appRoot) ? AppContext.BaseDirectory : appRoot;
        var dir = new DirectoryInfo(start);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, "Config", FileName);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        return null;
    }
}
