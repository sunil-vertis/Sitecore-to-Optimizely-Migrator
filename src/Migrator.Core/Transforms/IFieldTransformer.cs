using Migrator.Core.Models;

namespace Migrator.Core.Transforms;

public sealed class FieldTransformContext
{
    public required SitecoreItemDto Item { get; init; }
    public required FieldMapping FieldMapping { get; init; }
    public required Func<Guid, string?> ResolveOptimizelyKey { get; init; }
    public required Func<Guid, Task<string?>> ResolveOrUploadMediaAsync { get; init; }
}

public interface IFieldTransformer
{
    string Kind { get; }

    Task<object?> TransformAsync(
        SitecoreFieldValue source,
        FieldTransformContext context,
        CancellationToken cancellationToken = default);
}
