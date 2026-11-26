using System.Text.Json;
using Migrator.Core.Models;

namespace Migrator.Core.Config;

public static class OptimizelyCredentialsLoader
{
    public const string FileName = "optimizely.secrets.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static OptimizelyCredentials Load(string? appRoot = null)
    {
        var path = Find(appRoot);
        if (path is null)
        {
            throw new FileNotFoundException(
                "Optimizely Client ID and Client Secret must be set in Config/optimizely.secrets.json. " +
                "Copy Config/optimizely.secrets.sample.json and fill in clientId and clientSecret.");
        }

        var json = File.ReadAllText(path);
        var credentials = JsonSerializer.Deserialize<OptimizelyCredentials>(json, JsonOptions)
                          ?? throw new InvalidOperationException($"Optimizely credentials file is empty: {path}");

        credentials.ClientId = credentials.ClientId.Trim();
        credentials.ClientSecret = credentials.ClientSecret.Trim();
        credentials.SourcePath = path;

        if (string.IsNullOrWhiteSpace(credentials.ClientId) ||
            string.IsNullOrWhiteSpace(credentials.ClientSecret))
        {
            throw new InvalidOperationException(
                $"Optimizely clientId and clientSecret must be non-empty in {path}.");
        }

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
