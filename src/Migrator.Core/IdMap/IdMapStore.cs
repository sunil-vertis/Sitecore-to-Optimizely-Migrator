using Microsoft.Data.Sqlite;

namespace Migrator.Core.IdMap;

public sealed class IdMapEntry
{
    public Guid SitecoreItemId { get; init; }
    public string OptimizelyKey { get; init; } = string.Empty;
    public string ContentType { get; init; } = string.Empty;
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class IdMapStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public IdMapStore(string databasePath)
    {
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS IdMap (
                SitecoreItemId TEXT PRIMARY KEY,
                OptimizelyKey  TEXT NOT NULL,
                ContentType    TEXT NOT NULL,
                SourcePath     TEXT NOT NULL,
                TargetPath     TEXT NOT NULL,
                UpdatedAt      TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public string? GetOptimizelyKey(Guid sitecoreItemId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT OptimizelyKey FROM IdMap WHERE SitecoreItemId = $id";
        cmd.Parameters.AddWithValue("$id", sitecoreItemId.ToString("D"));
        return cmd.ExecuteScalar() as string;
    }

    public IdMapEntry? Get(Guid sitecoreItemId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT SitecoreItemId, OptimizelyKey, ContentType, SourcePath, TargetPath, UpdatedAt
            FROM IdMap WHERE SitecoreItemId = $id
            """;
        cmd.Parameters.AddWithValue("$id", sitecoreItemId.ToString("D"));

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return null;

        return new IdMapEntry
        {
            SitecoreItemId = Guid.Parse(reader.GetString(0)),
            OptimizelyKey = reader.GetString(1),
            ContentType = reader.GetString(2),
            SourcePath = reader.GetString(3),
            TargetPath = reader.GetString(4),
            UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
        };
    }

    public void Upsert(IdMapEntry entry)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            INSERT INTO IdMap (SitecoreItemId, OptimizelyKey, ContentType, SourcePath, TargetPath, UpdatedAt)
            VALUES ($id, $key, $type, $src, $tgt, $ts)
            ON CONFLICT(SitecoreItemId) DO UPDATE SET
                OptimizelyKey = excluded.OptimizelyKey,
                ContentType = excluded.ContentType,
                SourcePath = excluded.SourcePath,
                TargetPath = excluded.TargetPath,
                UpdatedAt = excluded.UpdatedAt;
            """;
        cmd.Parameters.AddWithValue("$id", entry.SitecoreItemId.ToString("D"));
        cmd.Parameters.AddWithValue("$key", entry.OptimizelyKey);
        cmd.Parameters.AddWithValue("$type", entry.ContentType);
        cmd.Parameters.AddWithValue("$src", entry.SourcePath);
        cmd.Parameters.AddWithValue("$tgt", entry.TargetPath);
        cmd.Parameters.AddWithValue("$ts", entry.UpdatedAt.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<IdMapEntry> GetAll()
    {
        var list = new List<IdMapEntry>();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT SitecoreItemId, OptimizelyKey, ContentType, SourcePath, TargetPath, UpdatedAt
            FROM IdMap ORDER BY SourcePath
            """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new IdMapEntry
            {
                SitecoreItemId = Guid.Parse(reader.GetString(0)),
                OptimizelyKey = reader.GetString(1),
                ContentType = reader.GetString(2),
                SourcePath = reader.GetString(3),
                TargetPath = reader.GetString(4),
                UpdatedAt = DateTimeOffset.Parse(reader.GetString(5))
            });
        }

        return list;
    }

    public void Dispose() => _connection.Dispose();
}
