using Dotmim.Sync;
using Dotmim.Sync.Sqlite;
using Dotmim.Sync.Web.Client;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

const string DemoTable = "demo_locations";

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

var setup = BuildSyncSetup();

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

var result = await agent.SynchronizeAsync(setup, progress).ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine($"Done. Total changes downloaded: {result.TotalChangesDownloadedFromServer}, uploaded: {result.TotalChangesUploadedToServer}.");

await PrintLocalRowsAsync(sqlitePath).ConfigureAwait(false);

static SyncSetup BuildSyncSetup()
{
    var setup = new SyncSetup(DemoTable);

    setup.Tables[DemoTable]
        .AddShadowColumn<string>("ServerNote")
        .AddShadowColumn<string>("ServerRevision");

    return setup;
}

static async Task PrintLocalRowsAsync(string sqlitePath)
{
    await using var conn = new SqliteConnection($"Data Source={sqlitePath};");
    await conn.OpenAsync().ConfigureAwait(false);

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = $"""
        SELECT id, name, place_geom, category_tags, "ServerNote", "ServerRevision"
        FROM {DemoTable}
        ORDER BY name;
        """;

    await using var reader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
    Console.WriteLine();
    Console.WriteLine("Local SQLite rows (geometry and int[] arrive as text; shadow columns are stored for offline use):");
    while (await reader.ReadAsync().ConfigureAwait(false))
    {
        Console.WriteLine(
            $"  {SqliteCol(reader, 1)} | geom={SqliteCol(reader, 2)} | tags={SqliteCol(reader, 3)} | note={SqliteCol(reader, 4)} | rev={SqliteCol(reader, 5)}");
    }
}

static string SqliteCol(SqliteDataReader r, int i)
    => r.IsDBNull(i) ? string.Empty : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
