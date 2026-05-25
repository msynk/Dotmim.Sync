using Microsoft.Data.Sqlite;

namespace Dotmim.Sync.Samples.Migration.Client;

/// <summary>Prints locally synced rows from the SQLite client database.</summary>
internal static class ClientRowPrinter
{
    public static async Task PrintProductsAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        Console.WriteLine($"  --- {MigrationConstants.ProductsTable} ---");

        var columns = await ReadColumnNamesAsync(conn, MigrationConstants.ProductsTable).ConfigureAwait(false);
        Console.WriteLine($"  Columns: [{string.Join(", ", columns)}]");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MigrationConstants.ProductsTable} ORDER BY rowid;";

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var parts = Enumerable.Range(0, reader.FieldCount)
                .Select(i => $"{reader.GetName(i)}={FormatValue(reader.GetValue(i))}");
            Console.WriteLine($"  {string.Join(" | ", parts)}");
            count++;
        }

        if (count == 0)
            Console.WriteLine("  (no rows)");
    }

    public static async Task PrintOrdersAsync(string sqlitePath)
    {
        await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
        await conn.OpenAsync().ConfigureAwait(false);

        Console.WriteLine($"  --- {MigrationConstants.OrdersTable} ---");

        var columns = await ReadColumnNamesAsync(conn, MigrationConstants.OrdersTable).ConfigureAwait(false);
        Console.WriteLine($"  Columns: [{string.Join(", ", columns)}]");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {MigrationConstants.OrdersTable} ORDER BY rowid;";

        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            var parts = Enumerable.Range(0, reader.FieldCount)
                .Select(i => $"{reader.GetName(i)}={FormatValue(reader.GetValue(i))}");
            Console.WriteLine($"  {string.Join(" | ", parts)}");
            count++;
        }

        if (count == 0)
            Console.WriteLine("  (no rows)");
    }

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(SqliteConnection conn, string tableName)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName});";
        await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
        var names = new List<string>();
        while (await reader.ReadAsync().ConfigureAwait(false))
            names.Add(reader.GetString(1));
        return names;
    }

    private static string FormatValue(object value) =>
        value is DBNull || value is null ? "NULL" : value.ToString() ?? "NULL";
}
