using Dotmim.Sync.Samples.SqliteClient;
using Microsoft.Extensions.Configuration;

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

using var app = new InteractiveClientApp(config, serviceUrl, sqlitePath);
await app.RunAsync().ConfigureAwait(false);
