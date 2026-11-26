using System.Text.Json;
using Migrator.Core.Models;

namespace Migrator.Core.Mapping;

public static class MappingLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    public static MappingConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Mapping file not found: {path}", path);

        var json = File.ReadAllText(path);
        return LoadFromJson(json);
    }

    public static MappingConfig LoadFromJson(string json)
    {
        var config = JsonSerializer.Deserialize<MappingConfig>(json, JsonOptions)
                     ?? throw new InvalidOperationException("Mapping JSON deserialized to null.");

        Normalize(config);
        return config;
    }

    public static void SaveToFile(MappingConfig config, string path)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    private static void Normalize(MappingConfig config)
    {
        foreach (var type in config.Types)
        {
            type.SitecoreTemplateId = type.SitecoreTemplateId.Trim();
            type.SitecoreTemplateName = type.SitecoreTemplateName.Trim();
            type.OptimizelyContentType = type.OptimizelyContentType.Trim();
            type.CreateAs = type.CreateAs.Trim().ToLowerInvariant();

            foreach (var field in type.Fields)
            {
                field.Sitecore = field.Sitecore.Trim();
                field.Optimizely = field.Optimizely.Trim();
                field.Kind = field.Kind.Trim();
            }
        }
    }
}
