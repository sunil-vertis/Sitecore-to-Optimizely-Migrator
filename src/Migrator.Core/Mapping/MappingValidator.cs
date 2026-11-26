using Migrator.Core.Models;
using Migrator.Core.Transforms;

namespace Migrator.Core.Mapping;

public static class MappingValidator
{
    private static readonly HashSet<string> AllowedCreateAs =
        new(StringComparer.OrdinalIgnoreCase) { "page", "component", "folder", "media" };

    public static MappingValidationResult Validate(MappingConfig config)
    {
        var result = new MappingValidationResult();

        if (config.Types.Count == 0)
            result.Errors.Add("Mapping contains no content types.");

        var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var templateNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in config.Types)
        {
            var label = string.IsNullOrWhiteSpace(type.SitecoreTemplateName)
                ? type.SitecoreTemplateId
                : type.SitecoreTemplateName;

            if (string.IsNullOrWhiteSpace(type.SitecoreTemplateId) &&
                string.IsNullOrWhiteSpace(type.SitecoreTemplateName))
            {
                result.Errors.Add("A type mapping is missing both sitecoreTemplateId and sitecoreTemplateName.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(type.OptimizelyContentType))
                result.Errors.Add($"Type '{label}' is missing optimizelyContentType.");

            if (!AllowedCreateAs.Contains(type.CreateAs))
                result.Errors.Add($"Type '{label}' has invalid createAs '{type.CreateAs}'.");

            if (!string.IsNullOrWhiteSpace(type.SitecoreTemplateId) &&
                !templateIds.Add(NormalizeGuid(type.SitecoreTemplateId)))
            {
                result.Errors.Add($"Duplicate sitecoreTemplateId '{type.SitecoreTemplateId}'.");
            }

            if (!string.IsNullOrWhiteSpace(type.SitecoreTemplateName) &&
                !templateNames.Add(type.SitecoreTemplateName))
            {
                result.Errors.Add($"Duplicate sitecoreTemplateName '{type.SitecoreTemplateName}'.");
            }

            if (type.Fields.Count == 0)
                result.Warnings.Add($"Type '{label}' has no field mappings (only display name / structure will migrate).");

            var sitecoreFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var optiFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in type.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Sitecore))
                    result.Errors.Add($"Type '{label}' has a field with empty sitecore name.");

                if (string.IsNullOrWhiteSpace(field.Optimizely))
                    result.Errors.Add($"Type '{label}' field '{field.Sitecore}' has empty optimizely name.");

                if (!string.IsNullOrWhiteSpace(field.Sitecore) && !sitecoreFields.Add(field.Sitecore))
                    result.Errors.Add($"Type '{label}' maps Sitecore field '{field.Sitecore}' more than once (must be 1:1).");

                if (!string.IsNullOrWhiteSpace(field.Optimizely) && !optiFields.Add(field.Optimizely))
                    result.Errors.Add($"Type '{label}' maps Optimizely property '{field.Optimizely}' more than once (must be 1:1).");

                if (!FieldTransformerRegistry.IsKnownKind(field.Kind))
                    result.Errors.Add($"Type '{label}' field '{field.Sitecore}' has unknown kind '{field.Kind}'.");
            }
        }

        return result;
    }

    public static string NormalizeGuid(string value)
    {
        var trimmed = value.Trim().Trim('{', '}');
        return Guid.TryParse(trimmed, out var g) ? g.ToString("D") : value.Trim();
    }
}

public sealed class MappingIndex
{
    private readonly Dictionary<string, TypeMapping> _byTemplateId;
    private readonly Dictionary<string, TypeMapping> _byTemplateName;
    private readonly MappingConfig _config;

    public MappingIndex(MappingConfig config)
    {
        _config = config;
        _byTemplateId = new Dictionary<string, TypeMapping>(StringComparer.OrdinalIgnoreCase);
        _byTemplateName = new Dictionary<string, TypeMapping>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in config.Types)
        {
            if (!string.IsNullOrWhiteSpace(type.SitecoreTemplateId))
                _byTemplateId[MappingValidator.NormalizeGuid(type.SitecoreTemplateId)] = type;

            if (!string.IsNullOrWhiteSpace(type.SitecoreTemplateName))
                _byTemplateName[type.SitecoreTemplateName] = type;
        }
    }

    public MappingConfig Config => _config;

    public TypeMapping? Find(SitecoreItemDto item)
    {
        var idKey = item.TemplateId.ToString("D");
        if (_byTemplateId.TryGetValue(idKey, out var byId))
            return byId;

        if (_byTemplateName.TryGetValue(item.TemplateName, out var byName))
            return byName;

        return null;
    }

    public bool IsIgnoredField(TypeMapping type, string fieldName)
    {
        if (_config.Defaults.IgnoreFields.Any(f =>
                string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase)))
            return true;

        return type.IgnoreFields.Any(f =>
            string.Equals(f, fieldName, StringComparison.OrdinalIgnoreCase));
    }
}
