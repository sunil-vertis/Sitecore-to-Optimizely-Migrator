using System.Text.Json.Serialization;

namespace Migrator.Core.Models;

public sealed class MappingConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("defaults")]
    public MappingDefaults Defaults { get; set; } = new();

    [JsonPropertyName("types")]
    public List<TypeMapping> Types { get; set; } = [];
}

public sealed class MappingDefaults
{
    [JsonPropertyName("locale")]
    public string Locale { get; set; } = "en";

    /// <summary>
    /// When false, any Sitecore template without a type map fails validation / migration.
    /// </summary>
    [JsonPropertyName("skipUnmappedTemplates")]
    public bool SkipUnmappedTemplates { get; set; }

    [JsonPropertyName("ignoreFields")]
    public List<string> IgnoreFields { get; set; } =
    [
        "__Created",
        "__Created by",
        "__Updated",
        "__Updated by",
        "__Revision",
        "__Sortorder",
        "__Security",
        "__Owner",
        "__Base template",
        "__Standard values"
    ];
}

public sealed class TypeMapping
{
    [JsonPropertyName("sitecoreTemplateId")]
    public string SitecoreTemplateId { get; set; } = string.Empty;

    [JsonPropertyName("sitecoreTemplateName")]
    public string SitecoreTemplateName { get; set; } = string.Empty;

    [JsonPropertyName("optimizelyContentType")]
    public string OptimizelyContentType { get; set; } = string.Empty;

    /// <summary>page | component | folder | media</summary>
    [JsonPropertyName("createAs")]
    public string CreateAs { get; set; } = "page";

    [JsonPropertyName("ignoreFields")]
    public List<string> IgnoreFields { get; set; } = [];

    [JsonPropertyName("fields")]
    public List<FieldMapping> Fields { get; set; } = [];
}

public sealed class FieldMapping
{
    [JsonPropertyName("sitecore")]
    public string Sitecore { get; set; } = string.Empty;

    [JsonPropertyName("optimizely")]
    public string Optimizely { get; set; } = string.Empty;

    /// <summary>
    /// string | richText | boolean | number | dateTime | link |
    /// contentReference | contentReferenceList | contentArea | media
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "string";
}
