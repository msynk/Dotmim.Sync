using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Dotmim.Sync.Samples.SqliteClient;

/// <summary>Console menu loop for the SQLite web client sample.</summary>
internal sealed class InteractiveClientApp : IDisposable
{
    private readonly IConfiguration _config;
    private readonly string _serviceUrl;
    private readonly string _sqlitePath;
    private readonly IReadOnlyList<MenuScope> _menu;
    private readonly SyncSetup _loadTestSetup;
    private readonly WebRemoteOrchestrator _remoteOrchestrator;
    private readonly SyncOptions _options;
    private readonly IProgress<ProgressArgs> _progress;
    private readonly HttpClient _httpClient;

    private SqliteSyncProvider _clientProvider = null!;
    private SyncAgent _agent = null!;

    public InteractiveClientApp(IConfiguration config, string serviceUrl, string sqlitePath)
    {
        // Process-wide column exclusions: any column listed here is stripped from EVERY table in EVERY scope/setup
        // that has one of these column names, without having to repeat the rule on each SetupTable or SyncSetup.
        // Must run before any SyncSetup is built (i.e. before DemoMenuBuilder.BuildMenuItems below) and the list must
        // match the server, so both sides produce the same effective schema.
        SyncSetup.GloballyExcludeColumns("audit_created_at", "audit_updated_at", "audit_tenant_id");

        this._config = config;
        this._serviceUrl = serviceUrl;
        this._sqlitePath = sqlitePath;
        this._menu = DemoMenuBuilder.BuildMenuItems();
        this._loadTestSetup = DemoMenuBuilder.CreateLoadTestSetup();

        var httpHandler = new HttpClientHandler();
        httpHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

        this._httpClient = new HttpClient(httpHandler) { Timeout = TimeSpan.FromMinutes(30) };
        this._remoteOrchestrator = new WebRemoteOrchestrator(new Uri(this._serviceUrl), client: this._httpClient);
        this._options = new SyncOptions();
        this._progress = new Progress<ProgressArgs>(p => Console.WriteLine($"{p.ProgressLevel}: {p.Message}"));

        this.RecreateClientAgent();
    }

    public void Dispose() => this._httpClient.Dispose();

    public async Task RunAsync()
    {
        Console.WriteLine($"SQLite database: {this._sqlitePath}");
        Console.WriteLine($"Sync endpoint:   {this._serviceUrl}");
        Console.WriteLine();

        while (true)
        {
            this.PrintMenu();
            var choice = Console.ReadLine()?.Trim();

            if (string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
                break;

            if (string.Equals(choice, "5", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in this._menu)
                    await RunScopeSyncAsync(this._agent, this._sqlitePath, item, this._progress).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(choice, "6", StringComparison.OrdinalIgnoreCase))
            {
                await AdvancedLoadTestRunner.RunAsync(
                    this._serviceUrl,
                    this._sqlitePath,
                    this._menu,
                    this._loadTestSetup,
                    this._config).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(choice, "c", StringComparison.OrdinalIgnoreCase))
            {
                this.TryClearClientDatabase();
                continue;
            }

            var selected = this._menu.FirstOrDefault(i => string.Equals(i.MenuKey, choice, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                Console.WriteLine("Unknown command. Choose 1–6, c (clear client DB), or q.");
                Console.WriteLine();
                continue;
            }

            await RunScopeSyncAsync(this._agent, this._sqlitePath, selected, this._progress).ConfigureAwait(false);
        }

        Console.WriteLine("Done.");
    }

    private void RecreateClientAgent()
    {
        this._clientProvider = new SqliteSyncProvider(this._sqlitePath);
        this._agent = new SyncAgent(this._clientProvider, this._remoteOrchestrator, this._options);
    }

    private void TryClearClientDatabase()
    {
        Console.WriteLine();
        Console.WriteLine("Clear client database — this deletes the SQLite file (and -wal / -shm) so the next sync reprovisions.");
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
        TryDeleteClientDbFile(this._sqlitePath);
        TryDeleteClientDbFile(this._sqlitePath + "-wal");
        TryDeleteClientDbFile(this._sqlitePath + "-shm");
        this.RecreateClientAgent();
        Console.WriteLine("Client database cleared. Run a sync option to pull a fresh client from the server.");
        Console.WriteLine();
    }

    private static void TryDeleteClientDbFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: could not delete {path}: {ex.Message}");
        }
    }

    private void PrintMenu()
    {
        Console.WriteLine("Choose a sync demo:");
        Console.WriteLine("  1. Sync geometry + integer[] data type demo");
        Console.WriteLine("  2. Sync shadow columns demo");
        Console.WriteLine("  3. Sync excluded column demo");
        Console.WriteLine("  4. Sync global-exclude demo (global + setup + per-table Include bypass)");
        Console.WriteLine("  5. Sync all demos (all scopes)");
        Console.WriteLine("  6. Advanced load test (parallel clients + multi-round stress)");
        Console.WriteLine("  c. Clear client SQLite database (fresh test)");
        Console.WriteLine("  q. Quit");
        Console.Write("Selection: ");
    }

    private static async Task RunScopeSyncAsync(
        SyncAgent agent,
        string sqlitePath,
        MenuScope scope,
        IProgress<ProgressArgs> progress)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {scope.Title} ===");
        Console.WriteLine($"Scope: {scope.ScopeName}");

        var result = await agent.SynchronizeAsync(scope.ScopeName, scope.Setup, progress).ConfigureAwait(false);

        Console.WriteLine($"Downloaded: {result.TotalChangesDownloadedFromServer}, Uploaded: {result.TotalChangesUploadedToServer}");
        await scope.PrintRowsAsync(sqlitePath).ConfigureAwait(false);
        Console.WriteLine();
    }
}
