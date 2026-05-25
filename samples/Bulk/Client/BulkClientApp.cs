using Dotmim.Sync;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;

namespace Dotmim.Sync.Samples.Bulk.Client;

/// <summary>
/// Interactive console application for the bulk-sync demo.
///
/// It connects to the Bulk.Server (PostgreSQL) and synchronises a local SQLite
/// database using the new bulk-operation path (INSERT ... ON CONFLICT DO UPDATE
/// staging table strategy).  The initial download of 50 000 products +
/// 100 000 order-lines is the primary benchmark scenario.
/// </summary>
internal sealed class BulkClientApp : IDisposable
{
    private readonly string _serviceUrl;
    private readonly string _sqlitePath;
    private readonly HttpClient _httpClient;
    private readonly WebRemoteOrchestrator _remote;
    private readonly SyncOptions _options;

    private SqliteSyncProvider _clientProvider = null!;
    private SyncAgent _agent = null!;

    private static readonly SyncSetup Setup = new(
        BulkConstants.ProductsTable,
        BulkConstants.OrderLinesTable);

    public BulkClientApp(string serviceUrl, string sqlitePath)
    {
        _serviceUrl = serviceUrl;
        _sqlitePath = sqlitePath;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };

        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
        _remote = new WebRemoteOrchestrator(new Uri(_serviceUrl), client: _httpClient);
        _options = new SyncOptions
        {
            // Match server batch size so memory stays bounded.
            BatchSize = 2_000,
        };

        RecreateAgent();
    }

    public void Dispose() => _httpClient.Dispose();

    // ── main loop ────────────────────────────────────────────────────────────────

    public async Task RunAsync()
    {
        Console.WriteLine($"SQLite  : {_sqlitePath}");
        Console.WriteLine($"Server  : {_serviceUrl}");
        Console.WriteLine();

        while (true)
        {
            PrintMenu();
            var choice = Console.ReadLine()?.Trim().ToLowerInvariant();

            switch (choice)
            {
                case "1": await SyncNormalAsync().ConfigureAwait(false);       break;
                case "2": await SyncReinitAsync().ConfigureAwait(false);       break;
                case "3": await PrintServerStatsAsync().ConfigureAwait(false); break;
                case "4": await PrintLocalStatsAsync().ConfigureAwait(false);  break;
                case "5": await AddClientRowsAsync().ConfigureAwait(false);    break;
                case "6": await AddServerRowsAsync().ConfigureAwait(false);    break;
                case "c": ClearClientDatabase();                               break;
                case "q":
                    Console.WriteLine("Done.");
                    return;
                default:
                    Console.WriteLine("Unknown command.\n");
                    break;
            }
        }
    }

    // ── menu actions ─────────────────────────────────────────────────────────────

    private async Task SyncNormalAsync()
    {
        Console.WriteLine($"\n=== Incremental sync (scope: {BulkConstants.ScopeName}) ===");
        await RunSyncAsync(SyncType.Normal).ConfigureAwait(false);
    }

    private async Task SyncReinitAsync()
    {
        Console.WriteLine($"\n=== Full reinitialise from server (scope: {BulkConstants.ScopeName}) ===");
        Console.WriteLine("This downloads ALL server rows — use it to benchmark bulk insert performance.");
        await RunSyncAsync(SyncType.Reinitialize).ConfigureAwait(false);
    }

    private async Task RunSyncAsync(SyncType syncType)
    {
        var progress = new Progress<ProgressArgs>(p =>
        {
            if (p.ProgressLevel >= SyncProgressLevel.Information)
                Console.WriteLine($"  [{p.ProgressLevel,-11}] {p.Message}");
        });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _agent
                .SynchronizeAsync(BulkConstants.ScopeName, Setup, syncType, progress)
                .ConfigureAwait(false);

            sw.Stop();

            Console.WriteLine();
            Console.WriteLine($"  Downloaded  : {result.TotalChangesDownloadedFromServer,10:N0} rows");
            Console.WriteLine($"  Uploaded    : {result.TotalChangesUploadedToServer,10:N0} rows");
            Console.WriteLine($"  Conflicts   : {result.TotalResolvedConflicts,10:N0}");
            Console.WriteLine($"  Elapsed     : {sw.Elapsed.TotalSeconds,10:F2} s");

            if (result.TotalChangesDownloadedFromServer > 0)
            {
                var rps = result.TotalChangesDownloadedFromServer / sw.Elapsed.TotalSeconds;
                Console.WriteLine($"  Throughput  : {rps,10:N0} rows/s");
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ERROR ({sw.Elapsed.TotalSeconds:F2}s): {ex.Message}");
        }

        Console.WriteLine();
    }

    private async Task PrintServerStatsAsync()
    {
        Console.WriteLine("\n=== Server row counts ===");
        try
        {
            var statsUri = new Uri(new Uri(_serviceUrl).GetLeftPart(UriPartial.Authority) + "/stats");
            var json = await _httpClient.GetStringAsync(statsUri).ConfigureAwait(false);
            Console.WriteLine($"  {json}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }

    private async Task PrintLocalStatsAsync()
    {
        Console.WriteLine("\n=== Local SQLite row counts ===");
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            foreach (var tbl in new[] { BulkConstants.ProductsTable, BulkConstants.OrderLinesTable })
            {
                long count = 0;
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(*) FROM [{tbl}];";
                    count = Convert.ToInt64(
                        await cmd.ExecuteScalarAsync().ConfigureAwait(false),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
                catch { /* table may not exist yet */ }

                Console.WriteLine($"  {tbl,-30}: {count,10:N0} rows");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }

    private async Task AddClientRowsAsync()
    {
        Console.Write("\nHow many products to add locally? [default 10] ");
        var input = Console.ReadLine()?.Trim();
        if (!int.TryParse(input, out var n) || n < 1) n = 10;

        Console.WriteLine($"\n=== Inserting {n} random products + order-lines into local SQLite ===");
        try
        {
            await using var conn = new SqliteConnection($"Data Source={_sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            for (var i = 0; i < n; i++)
            {
                var pid = Guid.NewGuid().ToString();
                var sku = "LOCAL-" + pid[..8].ToUpperInvariant();
                var price = Math.Round(Random.Shared.NextDouble() * 99 + 1, 2);

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        INSERT INTO [{BulkConstants.ProductsTable}]
                            (id, sku, name, category, description,
                             price, stock_qty, weight_g, is_active, created_at, updated_at)
                        VALUES
                            (@id, @sku, @name, @cat, @desc,
                             @price, @qty, @wt, 1, datetime('now'), datetime('now'));
                        """;
                    cmd.Parameters.AddWithValue("@id",    pid);
                    cmd.Parameters.AddWithValue("@sku",   sku);
                    cmd.Parameters.AddWithValue("@name",  $"Local-Product-{sku}");
                    cmd.Parameters.AddWithValue("@cat",   "LocalTest");
                    cmd.Parameters.AddWithValue("@desc",  "Inserted by bulk-demo client");
                    cmd.Parameters.AddWithValue("@price", price);
                    cmd.Parameters.AddWithValue("@qty",   Random.Shared.Next(1, 50));
                    cmd.Parameters.AddWithValue("@wt",    Random.Shared.Next(100, 2000));
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"""
                        INSERT INTO [{BulkConstants.OrderLinesTable}]
                            (id, product_id, quantity, unit_price, discount, line_total,
                             status, ordered_at, notes)
                        VALUES
                            (@id, @pid, 1, @price, 0, @price,
                             'pending', datetime('now'), 'Client-side test row');
                        """;
                    cmd.Parameters.AddWithValue("@id",    Guid.NewGuid().ToString());
                    cmd.Parameters.AddWithValue("@pid",   pid);
                    cmd.Parameters.AddWithValue("@price", price);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
            }

            Console.WriteLine($"  Inserted {n} product(s) + {n} order-line(s). Run sync (option 1) to upload.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
            Console.WriteLine("  Tip: sync at least once (option 1) so the tables exist first.");
        }

        Console.WriteLine();
    }

    private async Task AddServerRowsAsync()
    {
        Console.Write("\nHow many rows to add on the server? [default 100] ");
        var input = Console.ReadLine()?.Trim();
        if (!int.TryParse(input, out var n) || n < 1) n = 100;
        if (n > 10_000) n = 10_000;

        Console.WriteLine($"\n=== Adding {n} rows on the server via POST /add-rows ===");
        try
        {
            var addUri = new Uri(new Uri(_serviceUrl).GetLeftPart(UriPartial.Authority) + $"/add-rows?count={n}");
            var response = await _httpClient.PostAsync(addUri, content: null).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                Console.WriteLine($"  Server: {body}");
            else
                Console.WriteLine($"  ERROR {(int)response.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }

    private void ClearClientDatabase()
    {
        Console.WriteLine("\nClear client database — deletes the SQLite file so the next sync starts fresh.");
        Console.WriteLine($"  {_sqlitePath}");
        Console.Write("Type YES to confirm: ");
        if (!string.Equals(Console.ReadLine()?.Trim(), "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Cancelled.\n");
            return;
        }

        SqliteConnection.ClearAllPools();
        TryDelete(_sqlitePath);
        TryDelete(_sqlitePath + "-wal");
        TryDelete(_sqlitePath + "-shm");
        RecreateAgent();
        Console.WriteLine("Client database cleared. Run option 1 or 2 to pull data from the server.\n");
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private void RecreateAgent()
    {
        _clientProvider = new SqliteSyncProvider(_sqlitePath);
        _agent = new SyncAgent(_clientProvider, _remote, _options);
    }

    private static void PrintMenu()
    {
        Console.WriteLine("Bulk-sync demo — choose an option:");
        Console.WriteLine("  1. Incremental sync               (upload + download changes)");
        Console.WriteLine("  2. Full reinitialise from server  (download ALL rows — bulk benchmark)");
        Console.WriteLine("  3. Print server row counts");
        Console.WriteLine("  4. Print local SQLite row counts");
        Console.WriteLine("  5. Add random rows to LOCAL SQLite");
        Console.WriteLine("  6. Add random rows on the SERVER  (then sync to download)");
        Console.WriteLine("  c. Clear client SQLite database   (start fresh)");
        Console.WriteLine("  q. Quit");
        Console.Write("Selection: ");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Console.WriteLine($"  Warning: could not delete {path}: {ex.Message}"); }
    }
}
