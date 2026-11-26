using Migrator.Core.Models;

namespace Migrator.Core.Abstractions;

public interface ISitecoreClient
{
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<SitecoreItemDto?> GetItemByPathAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<SitecoreItemDto?> GetItemByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SitecoreItemDto>> GetChildrenAsync(
        Guid parentId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SitecoreItemDto>> GetSubtreeAsync(
        string path,
        CancellationToken cancellationToken = default,
        IProgress<string>? progress = null);

    Task<SitecoreMediaBinary?> DownloadMediaAsync(
        Guid mediaItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the template's fields with inheritance flattened — base-template fields
    /// are included as if they were defined directly on this template.
    /// </summary>
    Task<SitecoreFlattenedTemplate?> GetFlattenedTemplateAsync(
        Guid templateId,
        CancellationToken cancellationToken = default);
}
