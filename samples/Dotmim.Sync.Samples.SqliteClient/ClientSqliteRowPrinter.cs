using Microsoft.Data.Sqlite;

namespace Dotmim.Sync.Samples.SqliteClient;

internal static class ClientSqliteRowPrinter
{
    public static async Task PrintGeometryRowsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, place_geom, category_tags
            FROM {SyncSampleConstants.GeometryArrayTable}
            ORDER BY name;
            """;

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        Console.WriteLine("Rows from geometry + integer[] scope:");
        while (await reader.ReadAsync().ConfigureAwait(false))
            Console.WriteLine($"  {SqliteCol(reader, 1)} | geom={SqliteCol(reader, 2)} | tags={SqliteCol(reader, 3)}");
    }

    public static async Task PrintShadowRowsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, event_name, event_at_utc, "ServerNote", "ServerRevision"
            FROM {SyncSampleConstants.ShadowTable}
            ORDER BY event_name;
            """;

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        Console.WriteLine("Rows from shadow scope (server-filled shadow values):");
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            Console.WriteLine(
                $"  {SqliteCol(reader, 1)} @ {SqliteCol(reader, 2)} | note={SqliteCol(reader, 3)} | rev={SqliteCol(reader, 4)}");
        }
    }

    public static async Task PrintExcludedRowsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        await using var infoCmd = conn.CreateCommand();
        infoCmd.CommandText = $"PRAGMA table_info({SyncSampleConstants.ExcludeTable});";

        var colNames = new List<string>();
        await using (var infoReader = await infoCmd.ExecuteReaderAsync().ConfigureAwait(false))
        {
            while (await infoReader.ReadAsync().ConfigureAwait(false))
                colNames.Add(SqliteCol(infoReader, 1));
        }

        Console.WriteLine("Rows from excluded-column scope:");
        Console.WriteLine($"  Local columns: {string.Join(", ", colNames)}");
        Console.WriteLine("  (Notice secret_note is excluded from this local table.)");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, first_name, last_name, email
            FROM {SyncSampleConstants.ExcludeTable}
            ORDER BY first_name;
            """;

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
            Console.WriteLine($"  {SqliteCol(reader, 1)} {SqliteCol(reader, 2)} | {SqliteCol(reader, 3)}");
    }

    public static string SqliteCol(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}
