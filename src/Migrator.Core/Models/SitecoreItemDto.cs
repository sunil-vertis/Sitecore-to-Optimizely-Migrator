namespace Migrator.Core.Models;

public sealed record SitecoreFieldValue(
    string Name,
    string Type,
    string? Value,
    string? RawValue = null,
    /// <summary>True when the field is defined on a base template, not the item's concrete template.</summary>
    bool IsInherited = false,
    string? DeclaringTemplateName = null);

public sealed record SitecoreItemDto(
    Guid Id,
    string Path,
    string Name,
    string DisplayName,
    Guid TemplateId,
    string TemplateName,
    string Language,
    IReadOnlyDictionary<string, SitecoreFieldValue> Fields,
    IReadOnlyList<Guid> ChildIds,
    bool IsMediaItem = false,
    string? MediaUrl = null,
    string? MimeType = null,
    /// <summary>Base template IDs resolved for this item's template (inheritance chain).</summary>
    IReadOnlyList<Guid>? BaseTemplateIds = null);

public sealed record SitecoreMediaBinary(
    Guid ItemId,
    string FileName,
    string ContentType,
    byte[] Data);
