namespace Migrator.Core.Models;

public enum MigrationItemStatus
{
    Pending,
    Skipped,
    DryRun,
    Created,
    Updated,
    Failed
}

public sealed class MigrationItemResult
{
    public Guid SitecoreId { get; init; }
    public string SitecorePath { get; init; } = string.Empty;
    public string TemplateName { get; init; } = string.Empty;
    public string? OptimizelyContentType { get; init; }
    public string? OptimizelyKey { get; init; }
    public MigrationItemStatus Status { get; set; }
    public string? Message { get; set; }
}

public sealed class MigrationRunResult
{
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<MigrationItemResult> Items { get; } = [];
    public List<string> ValidationErrors { get; } = [];

    public int Created => Items.Count(i => i.Status == MigrationItemStatus.Created);
    public int Updated => Items.Count(i => i.Status == MigrationItemStatus.Updated);
    public int Skipped => Items.Count(i => i.Status == MigrationItemStatus.Skipped);
    public int Failed => Items.Count(i => i.Status == MigrationItemStatus.Failed);
    public int DryRunCount => Items.Count(i => i.Status == MigrationItemStatus.DryRun);
}

public sealed class MappingValidationResult
{
    public bool IsValid => Errors.Count == 0;
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
}
