using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

const string GeometryArrayScope = "geo-array-scope";
const string ShadowScope = "shadow-scope";
const string ExcludeScope = "exclude-scope";

const string GeometryArrayTable = "demo_geo_points";
const string ShadowTable = "demo_audit_events";
const string ExcludeTable = "demo_customers";

var baseDir = AppContext.BaseDirectory;
var config = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: false)
    .AddEnvironmentVariables()
    .Build();

var serviceUrl = config["Sync:ServiceUrl"] ?? throw new InvalidOperationException("Sync:ServiceUrl is required.");
var sqlitePath = config["Sync:SqlitePath"];

if (string.IsNullOrWhiteSpace(sqlitePath))
{
    var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotmimSyncSamples");
    Directory.CreateDirectory(dir);
    sqlitePath = Path.Combine(dir, "demo_client.sqlite");
}

Console.WriteLine($"SQLite database: {sqlitePath}");
Console.WriteLine($"Sync endpoint:   {serviceUrl}");
Console.WriteLine();

var menu = BuildMenuItems();

var clientProvider = new SqliteSyncProvider(sqlitePath);

// Accept dev certificate when calling local ASP.NET Core HTTPS endpoint.
var httpHandler = new HttpClientHandler();
httpHandler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;

using var httpClient = new HttpClient(httpHandler)
{
    Timeout = TimeSpan.FromMinutes(30),
};

var remoteOrchestrator = new WebRemoteOrchestrator(new Uri(serviceUrl), client: httpClient);
var options = new SyncOptions();
var agent = new SyncAgent(clientProvider, remoteOrchestrator, options);
var progress = new Progress<ProgressArgs>(p => Console.WriteLine($"{p.ProgressLevel}: {p.Message}"));

while (true)
{
    PrintMenu(menu);
    var choice = Console.ReadLine()?.Trim();

    if (string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
        break;

    if (string.Equals(choice, "4", StringComparison.OrdinalIgnoreCase))
    {
        foreach (var item in menu)
            await RunScopeSyncAsync(agent, sqlitePath, item, progress).ConfigureAwait(false);
        continue;
    }

    var selected = menu.FirstOrDefault(i => string.Equals(i.MenuKey, choice, StringComparison.OrdinalIgnoreCase));
    if (selected == null)
    {
        Console.WriteLine("Unknown command. Choose 1, 2, 3, 4 or q.");
        Console.WriteLine();
        continue;
    }

    await RunScopeSyncAsync(agent, sqlitePath, selected, progress).ConfigureAwait(false);
}

Console.WriteLine("Done.");

static IReadOnlyList<MenuScope> BuildMenuItems()
{
    var geometrySetup = new SyncSetup(GeometryArrayTable);
    var shadowSetup = new SyncSetup(ShadowTable);
    shadowSetup.Tables[ShadowTable]
        .AddShadowColumn<string>("ServerNote")
        .AddShadowColumn<string>("ServerRevision");

    var excludeSetup = new SyncSetup(ExcludeTable);
    excludeSetup.Tables[ExcludeTable]
        .ExcludeColumns("secret_note");

    return
    [
        new MenuScope(
            "1",
            "Sync geometry + integer[] data type demo",
            GeometryArrayScope,
            geometrySetup,
            PrintGeometryRowsAsync),
        new MenuScope(
            "2",
            "Sync shadow columns demo",
            ShadowScope,
            shadowSetup,
            PrintShadowRowsAsync),
        new MenuScope(
            "3",
            "Sync excluded column demo",
            ExcludeScope,
            excludeSetup,
            PrintExcludedRowsAsync),
    ];
}

static async Task RunScopeSyncAsync(
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

static void PrintMenu(IReadOnlyList<MenuScope> menu)
{
    Console.WriteLine("Choose a sync demo:");
    foreach (var item in menu)
        Console.WriteLine($"  {item.MenuKey}. {item.Title}");
    Console.WriteLine("  4. Sync all demos (all scopes)");
    Console.WriteLine("  q. Quit");
    Console.Write("Selection: ");
}

static async Task PrintGeometryRowsAsync(string sqlitePath)
{
    await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
    await conn.OpenAsync().ConfigureAwait(false);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT id, name, place_geom, category_tags
        FROM {GeometryArrayTable}
        ORDER BY name;
        """;

    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    Console.WriteLine("Rows from geometry + integer[] scope:");
    while (await reader.ReadAsync().ConfigureAwait(false))
        Console.WriteLine($"  {SqliteCol(reader, 1)} | geom={SqliteCol(reader, 2)} | tags={SqliteCol(reader, 3)}");
}

static async Task PrintShadowRowsAsync(string sqlitePath)
{
    await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
    await conn.OpenAsync().ConfigureAwait(false);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT id, event_name, event_at_utc, "ServerNote", "ServerRevision"
        FROM {ShadowTable}
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

static async Task PrintExcludedRowsAsync(string sqlitePath)
{
    await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
    await conn.OpenAsync().ConfigureAwait(false);

    await using var infoCmd = conn.CreateCommand();
    infoCmd.CommandText = $"PRAGMA table_info({ExcludeTable});";

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
        FROM {ExcludeTable}
        ORDER BY first_name;
        """;

    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    while (await reader.ReadAsync().ConfigureAwait(false))
        Console.WriteLine($"  {SqliteCol(reader, 1)} {SqliteCol(reader, 2)} | {SqliteCol(reader, 3)}");
}

static string SqliteCol(SqliteDataReader r, int i)
    => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;

internal sealed record MenuScope(
    string MenuKey,
    string Title,
    string ScopeName,
    SyncSetup Setup,
    Func<string, Task> PrintRowsAsync);
