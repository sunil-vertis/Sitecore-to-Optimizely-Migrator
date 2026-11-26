using Migrator.Core.Models;

namespace Migrator.Core.Abstractions;

public sealed class OptimizelyCreateContentRequest
{
    public string? Key { get; set; }
    public required string ContentType { get; set; }
    public required string Container { get; set; }
    public required string DisplayName { get; set; }
    public required string Locale { get; set; }
    public Dictionary<string, object?> Properties { get; set; } = new();
}

public sealed class OptimizelyContentResult
{
    public required string Key { get; set; }
    public string? ContentType { get; set; }
    public string? DisplayName { get; set; }
}

public sealed class OptimizelyMediaUploadResult
{
    public required string Key { get; set; }
    public string? Url { get; set; }
}

public interface IOptimizelyClient
{
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    Task<OptimizelyContentResult> CreateContentAsync(
        OptimizelyCreateContentRequest request,
        CancellationToken cancellationToken = default);

    Task<OptimizelyContentResult> UpdateContentAsync(
        string contentKey,
        string locale,
        string displayName,
        Dictionary<string, object?> properties,
        CancellationToken cancellationToken = default);

    Task<OptimizelyMediaUploadResult> UploadMediaAsync(
        string fileName,
        string contentType,
        byte[] data,
        string containerKey,
        CancellationToken cancellationToken = default);
}
