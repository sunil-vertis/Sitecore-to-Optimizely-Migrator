namespace Migrator.Core.Models;

/// <summary>
/// A field that belongs to a Sitecore template after inheritance is flattened
/// (own fields + all base-template fields treated as direct fields).
/// </summary>
public sealed record SitecoreTemplateFieldDefinition(
    string Name,
    string Type,
    Guid DeclaringTemplateId,
    string DeclaringTemplateName,
    bool IsInherited);

public sealed record SitecoreFlattenedTemplate(
    Guid TemplateId,
    string TemplateName,
    IReadOnlyList<Guid> BaseTemplateIds,
    IReadOnlyList<SitecoreTemplateFieldDefinition> Fields);
