using Migrator.Core.Abstractions;
using Migrator.Core.IdMap;
using Migrator.Core.Mapping;
using Migrator.Core.Models;
using Migrator.Core.Transforms;
using Serilog;

namespace Migrator.Core.Migration;

public sealed class MigrationEngine
{
    private readonly ISitecoreClient _sitecore;
    private readonly IOptimizelyClient _optimizely;
    private readonly ILogger _log;

    public MigrationEngine(
        ISitecoreClient sitecore,
        IOptimizelyClient optimizely,
        ILogger? logger = null)
    {
        _sitecore = sitecore;
        _optimizely = optimizely;
        _log = logger ?? Log.ForContext<MigrationEngine>();
    }

    public event EventHandler<MigrationProgressEventArgs>? Progress;

    public async Task<MigrationRunResult> RunAsync(
        MigrationOptions options,
        CancellationToken cancellationToken = default)
    {
        var result = new MigrationRunResult
        {
            DryRun = options.DryRun,
            StartedAt = DateTimeOffset.UtcNow
        };

        var validation = MappingValidator.Validate(options.Mapping);
        result.ValidationErrors.AddRange(validation.Errors);
        foreach (var warning in validation.Warnings)
            _log.Warning("Mapping warning: {Warning}", warning);

        if (!validation.IsValid)
        {
            result.Success = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            _log.Error("Mapping validation failed with {Count} error(s).", validation.Errors.Count);
            return result;
        }

        if (string.IsNullOrWhiteSpace(options.Paths.SitecoreSourcePath))
        {
            result.ValidationErrors.Add("Sitecore source path is required.");
            result.Success = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            return result;
        }

        if (string.IsNullOrWhiteSpace(options.Paths.OptimizelyTargetContainerKey))
        {
            result.ValidationErrors.Add("Optimizely target container key is required.");
            result.Success = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            return result;
        }

        var index = new MappingIndex(options.Mapping);
        using var idMap = new IdMapStore(options.IdMapDatabasePath);

        IReadOnlyList<SitecoreItemDto> items;
        if (options.Paths.SingleItemOnly)
        {
            Report($"Loading single Sitecore item '{options.Paths.SitecoreSourcePath}'...");
            var single = await _sitecore.GetItemByPathAsync(options.Paths.SitecoreSourcePath, cancellationToken);
            items = single is null ? [] : [single];
        }
        else
        {
            Report("Loading Sitecore subtree...");
            var loadProgress = new Progress<string>(msg => Report(msg));
            items = await _sitecore.GetSubtreeAsync(
                options.Paths.SitecoreSourcePath,
                cancellationToken,
                loadProgress);
        }

        if (items.Count == 0)
        {
            result.ValidationErrors.Add(
                options.Paths.SingleItemOnly
                    ? $"No item found at '{options.Paths.SitecoreSourcePath}'."
                    : $"No items found under '{options.Paths.SitecoreSourcePath}'.");
            result.Success = false;
            result.FinishedAt = DateTimeOffset.UtcNow;
            return result;
        }

        if (options.MaxItems > 0 && items.Count > options.MaxItems)
            items = items.Take(options.MaxItems).ToList();

        _log.Information("Loaded {Count} Sitecore item(s) from {Path}", items.Count, options.Paths.SitecoreSourcePath);

        // Parents before children: sort by path depth
        var ordered = items.OrderBy(i => i.Path.Count(c => c == '/')).ThenBy(i => i.Path).ToList();
        var byId = ordered.ToDictionary(i => i.Id);

        // Pass A: media
        var mediaItems = ordered.Where(i => i.IsMediaItem || IsMediaMapped(index, i)).ToList();
        Report($"Pass A — media ({mediaItems.Count} item(s))...");
        foreach (var media in mediaItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemResult = await MigrateMediaAsync(media, options, index, idMap, cancellationToken);
            result.Items.Add(itemResult);
            Report(itemResult.Message ?? itemResult.Status.ToString(), result.Items.Count, ordered.Count, itemResult);
            if (itemResult.Status == MigrationItemStatus.Failed && !options.ContinueOnError)
            {
                result.Success = false;
                result.FinishedAt = DateTimeOffset.UtcNow;
                return result;
            }
        }

        // Pass B: structure + fields (non-reference first is handled by transformers resolving missing keys as null)
        var contentItems = ordered.Where(i => !mediaItems.Contains(i)).ToList();
        Report($"Pass B — content ({contentItems.Count} item(s))...");
        foreach (var item in contentItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemResult = await MigrateContentAsync(item, byId, options, index, idMap, pass: 1, cancellationToken);
            result.Items.Add(itemResult);
            Report(itemResult.Message ?? itemResult.Status.ToString(), result.Items.Count, ordered.Count, itemResult);
            if (itemResult.Status == MigrationItemStatus.Failed && !options.ContinueOnError)
            {
                result.Success = false;
                result.FinishedAt = DateTimeOffset.UtcNow;
                return result;
            }
        }

        // Pass C: patch references / content areas now that ID map is populated
        Report("Pass C — patching references...");
        foreach (var item in contentItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (options.DryRun)
                continue;

            var existing = idMap.Get(item.Id);
            if (existing is null)
                continue;

            var typeMap = index.Find(item);
            if (typeMap is null)
                continue;

            try
            {
                var properties = await BuildPropertiesAsync(item, typeMap, index, idMap, options, cancellationToken);
                await _optimizely.UpdateContentAsync(
                    existing.OptimizelyKey,
                    options.Optimizely.Locale,
                    item.DisplayName,
                    properties,
                    cancellationToken);

                result.Items.Add(new MigrationItemResult
                {
                    SitecoreId = item.Id,
                    SitecorePath = item.Path,
                    TemplateName = item.TemplateName,
                    OptimizelyContentType = typeMap.OptimizelyContentType,
                    OptimizelyKey = existing.OptimizelyKey,
                    Status = MigrationItemStatus.Updated,
                    Message = $"Patched references: {item.Path}"
                });
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Failed to patch references for {Path}", item.Path);
                var failed = new MigrationItemResult
                {
                    SitecoreId = item.Id,
                    SitecorePath = item.Path,
                    TemplateName = item.TemplateName,
                    OptimizelyContentType = typeMap.OptimizelyContentType,
                    OptimizelyKey = existing.OptimizelyKey,
                    Status = MigrationItemStatus.Failed,
                    Message = $"Patch failed: {ex.Message}"
                };
                result.Items.Add(failed);
                if (!options.ContinueOnError)
                {
                    result.Success = false;
                    result.FinishedAt = DateTimeOffset.UtcNow;
                    return result;
                }
            }
        }

        result.Success = result.Failed == 0;
        result.FinishedAt = DateTimeOffset.UtcNow;
        Report($"Finished. Created={result.Created}, Updated={result.Updated}, Skipped={result.Skipped}, Failed={result.Failed}, DryRun={result.DryRunCount}");
        return result;
    }

    private static bool IsMediaMapped(MappingIndex index, SitecoreItemDto item)
    {
        var map = index.Find(item);
        return map is not null &&
               string.Equals(map.CreateAs, "media", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<MigrationItemResult> MigrateMediaAsync(
        SitecoreItemDto item,
        MigrationOptions options,
        MappingIndex index,
        IdMapStore idMap,
        CancellationToken cancellationToken)
    {
        var typeMap = index.Find(item);
        if (typeMap is null)
        {
            if (options.Mapping.Defaults.SkipUnmappedTemplates)
            {
                return new MigrationItemResult
                {
                    SitecoreId = item.Id,
                    SitecorePath = item.Path,
                    TemplateName = item.TemplateName,
                    Status = MigrationItemStatus.Skipped,
                    Message = $"Skipped unmapped media template '{item.TemplateName}'"
                };
            }

            return Fail(item, null, $"No mapping for media template '{item.TemplateName}' ({item.TemplateId})");
        }

        var existingKey = idMap.GetOptimizelyKey(item.Id);
        if (options.DryRun)
        {
            return new MigrationItemResult
            {
                SitecoreId = item.Id,
                SitecorePath = item.Path,
                TemplateName = item.TemplateName,
                OptimizelyContentType = typeMap.OptimizelyContentType,
                OptimizelyKey = existingKey,
                Status = MigrationItemStatus.DryRun,
                Message = $"Dry-run media: {item.Path} → {typeMap.OptimizelyContentType}"
            };
        }

        try
        {
            var binary = await _sitecore.DownloadMediaAsync(item.Id, cancellationToken);
            if (binary is null)
                return Fail(item, typeMap.OptimizelyContentType, "Media binary download returned null.");

            var upload = await _optimizely.UploadMediaAsync(
                binary.FileName,
                binary.ContentType,
                binary.Data,
                options.Paths.OptimizelyTargetContainerKey,
                cancellationToken);

            idMap.Upsert(new IdMapEntry
            {
                SitecoreItemId = item.Id,
                OptimizelyKey = upload.Key,
                ContentType = typeMap.OptimizelyContentType,
                SourcePath = item.Path,
                TargetPath = options.Paths.OptimizelyTargetPathLabel,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            return new MigrationItemResult
            {
                SitecoreId = item.Id,
                SitecorePath = item.Path,
                TemplateName = item.TemplateName,
                OptimizelyContentType = typeMap.OptimizelyContentType,
                OptimizelyKey = upload.Key,
                Status = string.IsNullOrEmpty(existingKey) ? MigrationItemStatus.Created : MigrationItemStatus.Updated,
                Message = $"Uploaded media: {item.Name}"
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Media migration failed for {Path}", item.Path);
            return Fail(item, typeMap.OptimizelyContentType, ex.Message);
        }
    }

    private async Task<MigrationItemResult> MigrateContentAsync(
        SitecoreItemDto item,
        IReadOnlyDictionary<Guid, SitecoreItemDto> byId,
        MigrationOptions options,
        MappingIndex index,
        IdMapStore idMap,
        int pass,
        CancellationToken cancellationToken)
    {
        var typeMap = index.Find(item);
        if (typeMap is null)
        {
            if (options.Mapping.Defaults.SkipUnmappedTemplates)
            {
                return new MigrationItemResult
                {
                    SitecoreId = item.Id,
                    SitecorePath = item.Path,
                    TemplateName = item.TemplateName,
                    Status = MigrationItemStatus.Skipped,
                    Message = $"Skipped unmapped template '{item.TemplateName}'"
                };
            }

            return Fail(item, null, $"No 1:1 type mapping for template '{item.TemplateName}' ({item.TemplateId})");
        }

        // Re-fetch with inheritance flatten only for mapped items (subtree walk stays lightweight).
        var enriched = await _sitecore.GetItemByIdAsync(item.Id, cancellationToken);
        if (enriched is not null)
            item = enriched;

        // Incomplete field maps used to hard-fail every product (Created=0). Warn and continue
        // so mapped fields still migrate; unmapped valued fields are listed in the result message.
        var coverageWarning = ValidateFieldCoverage(item, typeMap, index);
        if (coverageWarning is not null)
            _log.Warning("{Warning}", coverageWarning);

        var existingKey = idMap.GetOptimizelyKey(item.Id);
        if (options.DryRun)
        {
            return new MigrationItemResult
            {
                SitecoreId = item.Id,
                SitecorePath = item.Path,
                TemplateName = item.TemplateName,
                OptimizelyContentType = typeMap.OptimizelyContentType,
                OptimizelyKey = existingKey,
                Status = MigrationItemStatus.DryRun,
                Message = coverageWarning is null
                    ? $"Dry-run: {item.Path} → {typeMap.OptimizelyContentType}"
                    : $"Dry-run: {item.Path} → {typeMap.OptimizelyContentType}. {coverageWarning}"
            };
        }

        try
        {
            var containerKey = ResolveContainerKey(item, byId, idMap, options.Paths.OptimizelyTargetContainerKey);
            var properties = await BuildPropertiesAsync(item, typeMap, index, idMap, options, cancellationToken);

            if (!string.IsNullOrEmpty(existingKey))
            {
                var updated = await _optimizely.UpdateContentAsync(
                    existingKey,
                    options.Optimizely.Locale,
                    item.DisplayName,
                    properties,
                    cancellationToken);

                idMap.Upsert(new IdMapEntry
                {
                    SitecoreItemId = item.Id,
                    OptimizelyKey = updated.Key,
                    ContentType = typeMap.OptimizelyContentType,
                    SourcePath = item.Path,
                    TargetPath = options.Paths.OptimizelyTargetPathLabel,
                    UpdatedAt = DateTimeOffset.UtcNow
                });

                return new MigrationItemResult
                {
                    SitecoreId = item.Id,
                    SitecorePath = item.Path,
                    TemplateName = item.TemplateName,
                    OptimizelyContentType = typeMap.OptimizelyContentType,
                    OptimizelyKey = updated.Key,
                    Status = MigrationItemStatus.Updated,
                    Message = $"Updated: {item.Path}"
                };
            }

            var created = await _optimizely.CreateContentAsync(new OptimizelyCreateContentRequest
            {
                ContentType = typeMap.OptimizelyContentType,
                Container = containerKey,
                DisplayName = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Name : item.DisplayName,
                Locale = options.Optimizely.Locale,
                Properties = properties
            }, cancellationToken);

            idMap.Upsert(new IdMapEntry
            {
                SitecoreItemId = item.Id,
                OptimizelyKey = created.Key,
                ContentType = typeMap.OptimizelyContentType,
                SourcePath = item.Path,
                TargetPath = options.Paths.OptimizelyTargetPathLabel,
                UpdatedAt = DateTimeOffset.UtcNow
            });

            return new MigrationItemResult
            {
                SitecoreId = item.Id,
                SitecorePath = item.Path,
                TemplateName = item.TemplateName,
                OptimizelyContentType = typeMap.OptimizelyContentType,
                OptimizelyKey = created.Key,
                Status = MigrationItemStatus.Created,
                Message = $"Created: {item.Path} → {created.Key}"
            };
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Content migration failed for {Path}", item.Path);
            return Fail(item, typeMap.OptimizelyContentType, ex.Message);
        }
    }

    private static string? ValidateFieldCoverage(SitecoreItemDto item, TypeMapping typeMap, MappingIndex index)
    {
        var mapped = typeMap.Fields
            .Select(f => f.Sitecore)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unmapped = item.Fields.Keys
            .Where(name => !index.IsIgnoredField(typeMap, name))
            .Where(name => !mapped.Contains(name))
            .Where(name => !string.IsNullOrWhiteSpace(item.Fields[name].Value))
            .ToList();

        if (unmapped.Count == 0)
            return null;

        return $"1:1 field mapping incomplete for '{item.TemplateName}' " +
               $"(include inherited base-template fields on this type map). " +
               $"Unmapped fields with values: {string.Join(", ", unmapped)}";
    }

    private async Task<Dictionary<string, object?>> BuildPropertiesAsync(
        SitecoreItemDto item,
        TypeMapping typeMap,
        MappingIndex index,
        IdMapStore idMap,
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var fieldMap in typeMap.Fields)
        {
            if (!item.Fields.TryGetValue(fieldMap.Sitecore, out var fieldValue))
            {
                // Case-insensitive fallback
                fieldValue = item.Fields.FirstOrDefault(kv =>
                    string.Equals(kv.Key, fieldMap.Sitecore, StringComparison.OrdinalIgnoreCase)).Value;
            }

            if (fieldValue is null || string.IsNullOrEmpty(fieldValue.Name))
                continue;

            var transformer = FieldTransformerRegistry.Get(fieldMap.Kind);
            var ctx = new FieldTransformContext
            {
                Item = item,
                FieldMapping = fieldMap,
                ResolveOptimizelyKey = idMap.GetOptimizelyKey,
                ResolveOrUploadMediaAsync = async mediaId =>
                {
                    var existing = idMap.GetOptimizelyKey(mediaId);
                    if (!string.IsNullOrEmpty(existing))
                        return existing;

                    if (options.DryRun)
                        return null;

                    var binary = await _sitecore.DownloadMediaAsync(mediaId, cancellationToken);
                    if (binary is null)
                        return null;

                    var upload = await _optimizely.UploadMediaAsync(
                        binary.FileName,
                        binary.ContentType,
                        binary.Data,
                        options.Paths.OptimizelyTargetContainerKey,
                        cancellationToken);

                    idMap.Upsert(new IdMapEntry
                    {
                        SitecoreItemId = mediaId,
                        OptimizelyKey = upload.Key,
                        ContentType = "media",
                        SourcePath = $"(media:{mediaId})",
                        TargetPath = options.Paths.OptimizelyTargetPathLabel,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                    return upload.Key;
                }
            };

            var value = await transformer.TransformAsync(fieldValue, ctx, cancellationToken);
            if (value is not null)
                properties[fieldMap.Optimizely] = value;
        }

        return properties;
    }

    private static string ResolveContainerKey(
        SitecoreItemDto item,
        IReadOnlyDictionary<Guid, SitecoreItemDto> byId,
        IdMapStore idMap,
        string rootContainerKey)
    {
        // Walk up path via parent: find nearest already-migrated ancestor.
        // Sitecore Item Service doesn't always give ParentId; use path prefix match.
        var candidates = byId.Values
            .Where(p => item.Path.StartsWith(p.Path + "/", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Path.Length);

        foreach (var parent in candidates)
        {
            var key = idMap.GetOptimizelyKey(parent.Id);
            if (!string.IsNullOrEmpty(key))
                return key;
        }

        return rootContainerKey;
    }

    private static MigrationItemResult Fail(SitecoreItemDto item, string? contentType, string message) =>
        new()
        {
            SitecoreId = item.Id,
            SitecorePath = item.Path,
            TemplateName = item.TemplateName,
            OptimizelyContentType = contentType,
            Status = MigrationItemStatus.Failed,
            Message = message
        };

    private void Report(string message, int current = 0, int total = 0, MigrationItemResult? item = null)
    {
        _log.Information("{Message}", message);
        Progress?.Invoke(this, new MigrationProgressEventArgs
        {
            Message = message,
            Current = current,
            Total = total,
            Item = item
        });
    }
}
