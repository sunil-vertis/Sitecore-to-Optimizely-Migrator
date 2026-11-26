using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public sealed class MigrationOptions
{
    public required SitecoreConnectionSettings Sitecore { get; init; }
    public required OptimizelyConnectionSettings Optimizely { get; init; }
    public required PathSettings Paths { get; init; }
    public required MappingConfig Mapping { get; init; }
    public required string IdMapDatabasePath { get; init; }
    public bool DryRun { get; init; } = true;
    public bool ContinueOnError { get; init; } = true;
    public int MaxItems { get; init; } = 0; // 0 = unlimited
}

public sealed class MigrationProgressEventArgs : EventArgs
{
    public required string Message { get; init; }
    public int Current { get; init; }
    public int Total { get; init; }
    public MigrationItemResult? Item { get; init; }
}
