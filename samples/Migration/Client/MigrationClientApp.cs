using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Dotmim.Sync.Samples.Migration.Client;

/// <summary>Interactive console loop for the migration demo client.</summary>
internal sealed class MigrationClientApp : IDisposable
{
    private readonly string _serviceUrl;
    private readonly string _sqlitePath;
    private readonly WebRemoteOrchestrator _remoteOrchestrator;
    private readonly SyncOptions _options;
    private readonly IProgress<ProgressArgs> _progress;
    private readonly HttpClient _httpClient;

    private SqliteSyncProvider _clientProvider = null!;
    private SyncAgent _agent = null!;

    // The v1 setup — matches what the server exposes for scope "mig_v1".
    // The client is deliberately kept on v1 (old column names) to demonstrate
    // that the migration layer on the server bridges the schema difference.
    private static readonly SyncSetup V1Setup = new(
        MigrationConstants.ProductsTable,
        MigrationConstants.OrdersTable);

    public MigrationClientApp(string serviceUrl, string sqlitePath)
    {
        this._serviceUrl = serviceUrl;
        this._sqlitePath = sqlitePath;

        var httpHandler = new HttpClientHandler();
        httpHandler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        this._httpClient = new HttpClient(httpHandler) { Timeout = TimeSpan.FromMinutes(10) };
        this._remoteOrchestrator = new WebRemoteOrchestrator(new Uri(this._serviceUrl), client: this._httpClient);
        this._options = new SyncOptions();
        this._progress = new Progress<ProgressArgs>(p => Console.WriteLine($"  [{p.ProgressLevel}] {p.Message}"));

        this.RecreateAgent();
    }

    public void Dispose() => this._httpClient.Dispose();

    public async Task RunAsync()
    {
        Console.WriteLine($"SQLite database : {this._sqlitePath}");
        Console.WriteLine($"Sync endpoint   : {this._serviceUrl}");
        Console.WriteLine();

        while (true)
        {
            PrintMenu();
            var choice = Console.ReadLine()?.Trim();

            switch (choice?.ToLowerInvariant())
            {
                case "1":
                    await SyncV1Async().ConfigureAwait(false);
                    break;

                case "2":
                    await AddClientDataAsync().ConfigureAwait(false);
                    break;

                case "3":
                    await AddServerDataAsync().ConfigureAwait(false);
                    break;

                case "p":
                    await PrintLocalDataAsync().ConfigureAwait(false);
                    break;

                case "c":
                    ClearClientDatabase();
                    break;

                case "q":
                    Console.WriteLine("Done.");
                    return;

                default:
                    Console.WriteLine("Unknown command.");
                    Console.WriteLine();
                    break;
            }
        }
    }

    // ─── menu actions ───────────────────────────────────────────────────────────

    private async Task SyncV1Async()
    {
        Console.WriteLine();
        Console.WriteLine($"=== Sync scope '{MigrationConstants.ScopeV1}' ===");

        try
        {
            var result = await this._agent
                .SynchronizeAsync(MigrationConstants.ScopeV1, V1Setup, this._progress)
                .ConfigureAwait(false);

            Console.WriteLine($"  Downloaded : {result.TotalChangesDownloadedFromServer}");
            Console.WriteLine($"  Uploaded   : {result.TotalChangesUploadedToServer}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Inserts a random product + order directly into the local SQLite database
    /// using the <b>v1 column names</b> (<c>product_name</c>, <c>order_date</c>).
    /// On the next sync (option 1) these rows will be uploaded to the server, where
    /// the migration engine will rename them to the v2 column names before applying.
    /// </summary>
    private async Task AddClientDataAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Add random row to CLIENT (local SQLite) ===");

        try
        {
            await using var conn = new SqliteConnection($"Data Source={this._sqlitePath};");
            await conn.OpenAsync().ConfigureAwait(false);

            // Insert product — using the v1 column name "product_name"
            var productId = Guid.NewGuid().ToString();
            var suffix = productId.Replace("-", "")[..8].ToUpperInvariant();
            var productName = $"Client-{suffix}";
            var price = Math.Round(Random.Shared.NextDouble() * 98 + 1, 2);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    INSERT INTO {MigrationConstants.ProductsTable} (id, product_name, description, price)
                    VALUES (@id, @name, @desc, @price);
                    """;
                cmd.Parameters.AddWithValue("@id", productId);
                cmd.Parameters.AddWithValue("@name", productName);
                cmd.Parameters.AddWithValue("@desc", "Client-side test product");
                cmd.Parameters.AddWithValue("@price", price);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            // Insert order — using the v1 column name "order_date"
            var orderId = Guid.NewGuid().ToString();
            var total = Math.Round(price * Random.Shared.Next(1, 4), 2);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"""
                    INSERT INTO {MigrationConstants.OrdersTable} (id, product_id, order_date, total, status)
                    VALUES (@id, @pid, @date, @total, @status);
                    """;
                cmd.Parameters.AddWithValue("@id", orderId);
                cmd.Parameters.AddWithValue("@pid", productId);
                cmd.Parameters.AddWithValue("@date", DateTime.UtcNow.ToString("O"));
                cmd.Parameters.AddWithValue("@total", total);
                cmd.Parameters.AddWithValue("@status", "new");
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            Console.WriteLine($"  Added product '{productName}' (id …{suffix}) + 1 order to local SQLite.");
            Console.WriteLine($"  Run option 1 to upload — the migration will rename columns to v2 on the server.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
            Console.WriteLine("  Tip: sync at least once (option 1) so the tables exist before inserting.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Calls the server's <c>POST /test-data</c> endpoint, which inserts a random
    /// product + order into Postgres using the <b>v2 column names</b>
    /// (<c>name</c>, <c>created_at</c>).
    /// On the next sync (option 1) these rows will be downloaded and the migration
    /// engine will rename the columns back to the v1 names before applying to SQLite.
    /// </summary>
    private async Task AddServerDataAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Add random row to SERVER (Postgres via /test-data) ===");

        try
        {
            var baseUri = new Uri(this._serviceUrl);
            var testDataUri = new Uri(baseUri.GetLeftPart(UriPartial.Authority) + "/test-data");

            var response = await this._httpClient.PostAsync(testDataUri, content: null).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Console.WriteLine($"  ERROR: server returned {(int)response.StatusCode} — {body}");
                return;
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            Console.WriteLine($"  Server inserted: {json}");
            Console.WriteLine($"  Run option 1 to download — the migration will rename columns to v1 for this client.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ERROR: {ex.Message}");
        }

        Console.WriteLine();
    }

    private async Task PrintLocalDataAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== Local SQLite data ===");
        try
        {
            await ClientRowPrinter.PrintProductsAsync(this._sqlitePath).ConfigureAwait(false);
            Console.WriteLine();
            await ClientRowPrinter.PrintOrdersAsync(this._sqlitePath).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  (error reading local data: {ex.Message})");
        }
        Console.WriteLine();
    }

    private void ClearClientDatabase()
    {
        Console.WriteLine();
        Console.WriteLine("Clear client database — deletes the SQLite file so the next sync starts fresh.");
        Console.WriteLine($"  {this._sqlitePath}");
        Console.Write("Type YES to confirm: ");
        var confirm = Console.ReadLine()?.Trim();
        if (!string.Equals(confirm, "YES", StringComparison.Ordinal))
        {
            Console.WriteLine("Cancelled.");
            Console.WriteLine();
            return;
        }

        SqliteConnection.ClearAllPools();
        TryDeleteFile(this._sqlitePath);
        TryDeleteFile(this._sqlitePath + "-wal");
        TryDeleteFile(this._sqlitePath + "-shm");
        this.RecreateAgent();
        Console.WriteLine("Client database cleared. Run option 1 to pull a fresh copy from the server.");
        Console.WriteLine();
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    private void RecreateAgent()
    {
        this._clientProvider = new SqliteSyncProvider(this._sqlitePath);
        this._agent = new SyncAgent(this._clientProvider, this._remoteOrchestrator, this._options);
    }

    private static void PrintMenu()
    {
        Console.WriteLine("Migration demo — choose an option:");
        Console.WriteLine("  1. Sync scope mig_v1  (upload/download changes)");
        Console.WriteLine("  2. Add random row to CLIENT database  [v1 cols: product_name, order_date]");
        Console.WriteLine("  3. Add random row to SERVER database  [v2 cols: name, created_at]");
        Console.WriteLine("  p. Print local SQLite data");
        Console.WriteLine("  c. Clear client SQLite database (fresh sync)");
        Console.WriteLine("  q. Quit");
        Console.Write("Selection: ");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: could not delete {path}: {ex.Message}");
        }
    }
}
