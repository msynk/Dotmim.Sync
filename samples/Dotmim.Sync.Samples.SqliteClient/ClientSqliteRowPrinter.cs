using Microsoft.Data.Sqlite;

namespace Dotmim.Sync.Samples.SqliteClient;

internal static class ClientSqliteRowPrinter
{
    public static async Task PrintGeometryRowsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        Console.WriteLine("Rows from geometry + integer[] scope:");

        var localColumns = await ReadColumnNamesAsync(conn, SyncSampleConstants.GeometryArrayTable).ConfigureAwait(false);
        Console.WriteLine($"  Local columns: [{string.Join(", ", localColumns)}]");
        Console.WriteLine("  Expected:      [id, name, place_geom, category_tags]");
        Console.WriteLine("  Note: this scope's SyncSetup has NO ExcludeColumn / IncludeColumn configured at all.");
        Console.WriteLine("        The server table has audit_created_at / audit_updated_at / audit_tenant_id columns,");
        Console.WriteLine("        yet they are missing here because SyncSetup.GloballyExcludeColumns was registered");
        Console.WriteLine("        once at startup and applies to every scope / setup across the process.");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, name, place_geom, category_tags
            FROM {SyncSampleConstants.GeometryArrayTable}
            ORDER BY name;
            """;

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
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

        Console.WriteLine("Rows from excluded-column scope:");

        var localColumns = await ReadColumnNamesAsync(conn, SyncSampleConstants.ExcludeTable).ConfigureAwait(false);
        Console.WriteLine($"  Local columns: [{string.Join(", ", localColumns)}]");
        Console.WriteLine("  Expected:      [id, first_name, last_name, email]");
        Console.WriteLine("  Two different exclusion layers acting on the SAME table:");
        Console.WriteLine("    - secret_note: stripped by the per-table SetupTable.ExcludeColumn on this scope.");
        Console.WriteLine("    - audit_created_at / audit_updated_at / audit_tenant_id: stripped by the process-wide");
        Console.WriteLine("      SyncSetup.GloballyExcludeColumns registration — this scope's setup never mentions them.");

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

    public static async Task PrintGlobalExcludeRowsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        Console.WriteLine("Rows from global-exclude scope (layered column filtering):");
        Console.WriteLine();

        await PrintTableShapeAndRowsAsync(
            conn,
            SyncSampleConstants.GlobalAuditTable,
            header: "  [A] Standard table — full exclusion stack applied (global + setup), no IncludeColumn bypass",
            expectedLocalColumns: ["id", "name", "price"],
            "  Expected: audit_* columns removed by SyncSetup.GlobalExcludedColumns;",
            "  internal_notes removed by syncSetup.ExcludeColumn at the scope level.").ConfigureAwait(false);

        Console.WriteLine();

        await PrintTableShapeAndRowsAsync(
            conn,
            SyncSampleConstants.GlobalAuditFeaturedTable,
            header: "  [B] Featured table — one globally-excluded column re-added via SetupTable.IncludeColumn",
            expectedLocalColumns: ["id", "name", "price", "audit_updated_at"],
            "  Expected: audit_updated_at is back (IncludeColumn bypasses the GLOBAL rule for this table only);",
            "  audit_created_at / audit_tenant_id still hidden; internal_notes still hidden (scope-level rule).").ConfigureAwait(false);
    }

    public static string SqliteCol(SqliteDataReader r, int i)
        => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

    private static async Task PrintTableShapeAndRowsAsync(
        SqliteConnection conn,
        string tableName,
        string header,
        IReadOnlyList<string> expectedLocalColumns,
        params string[] extraLegend)
    {
        Console.WriteLine(header);

        var localColumns = await ReadColumnNamesAsync(conn, tableName).ConfigureAwait(false);
        Console.WriteLine($"    Local columns: [{string.Join(", ", localColumns)}]");
        Console.WriteLine($"    Expected:      [{string.Join(", ", expectedLocalColumns)}]");
        foreach (var line in extraLegend)
            Console.WriteLine(line);

        var selectableColumns = expectedLocalColumns
            .Where(c => localColumns.Any(lc => string.Equals(lc, c, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (selectableColumns.Count == 0)
        {
            Console.WriteLine("    (table not present on the client yet — run the sync option first)");
            return;
        }

        var quoted = string.Join(", ", selectableColumns.Select(c => $"\"{c}\""));

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {quoted} FROM {tableName} ORDER BY name;";

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var parts = new List<string>(selectableColumns.Count);
            for (var i = 0; i < selectableColumns.Count; i++)
                parts.Add($"{selectableColumns[i]}={SqliteCol(reader, i)}");

            Console.WriteLine($"    {string.Join(" | ", parts)}");
        }
    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection conn, string tableName)
    {
        var names = new List<string>();
        await using var infoCmd = conn.CreateCommand();
        infoCmd.CommandText = $"PRAGMA table_info({tableName});";

        await using var infoReader = await infoCmd.ExecuteReaderAsync().ConfigureAwait(false);
        while (await infoReader.ReadAsync().ConfigureAwait(false))
            names.Add(SqliteCol(infoReader, 1));

        return names;
    }
}
